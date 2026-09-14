// WSL Container Desktop - a WinUI 3 manager for WSL containers.
// Copyright (C) 2026 Michael Hacker
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

using System.Security.Cryptography;
using System.Text;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>One active configuration, no switch-back resurrection and no automatic generation retries.</summary>
public sealed class AiCapabilityService(
    IEnumerable<IAiCapabilityObserver> observers,
    IAiCredentialStore credentials,
    TimeProvider? clock = null) : IAiCapabilityService
{
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateGate = new();
    private AiCapabilitySnapshot? _cached;
    private string _credentialIdentity = "";
    private DateTimeOffset _metadataAt;
    private DateTimeOffset _probeAt;
    private long _generation;
    private CancellationTokenSource? _inFlight;
    private static readonly TimeSpan MetadataLifetime = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ProbeLifetime = TimeSpan.FromMinutes(10);

    public AiCapabilitySnapshot GetCached(AiChatConfiguration configuration)
    {
        lock (_stateGate)
        {
            if (_cached?.Configuration != configuration || _credentialIdentity != CredentialIdentity(configuration))
                return new(configuration);
            if (_clock.GetUtcNow() - _cached.ObservedAt >= ProbeLifetime)
            {
                AiConversationContext.ForgetObservedLimit(configuration);
                return new(configuration);
            }
            if (_clock.GetUtcNow() - _probeAt < ProbeLifetime)
                return _cached;
            // A metadata refresh dates only the metadata, not the generation proof it reuses.
            // Keep independently fresh metadata while withdrawing every expired probe-derived field.
            if (_cached.Context.Source == AiObservationSource.HarmlessProbe)
                AiConversationContext.ForgetObservedLimit(configuration);
            return _cached with
            {
                Chat = WithoutExpiredProbe(_cached.Chat),
                Tools = WithoutExpiredProbe(_cached.Tools),
                StructuredJson = WithoutExpiredProbe(_cached.StructuredJson),
                Streaming = WithoutExpiredProbe(_cached.Streaming),
                Context = _cached.Context.Source == AiObservationSource.HarmlessProbe ? new() : _cached.Context,
            };
        }
    }

    public void Invalidate()
    {
        bool dropped;
        lock (_stateGate)
        {
            dropped = _cached is not null;
            InvalidateCore();
        }
        // Raised outside the lock: a handler that reads back the cache would otherwise re-enter it.
        if (dropped) Changed?.Invoke(this, EventArgs.Empty);
    }

    public event EventHandler? Changed;

    /// <summary>Drops the cached observation. Callers must hold <c>_stateGate</c>.</summary>
    private void InvalidateCore()
    {
        if (_cached is not null) AiConversationContext.ForgetObservedLimit(_cached.Configuration);
        _cached = null;
        _metadataAt = _probeAt = default;
        _generation++;
        _inFlight?.Cancel();
    }

    public async Task<AiCapabilitySnapshot> GetAsync(AiChatConfiguration configuration,
        bool probe = false, CancellationToken ct = default)
    {
        var (result, changed) = await ObserveAsync(configuration, probe, ct).ConfigureAwait(false);
        // Raised after the observation gate is released: a handler that reads capabilities back
        // would otherwise wait on a semaphore this call is still holding.
        if (changed) Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    private async Task<(AiCapabilitySnapshot Snapshot, bool Changed)> ObserveAsync(
        AiChatConfiguration configuration, bool probe, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        CancellationTokenSource? timeout = null;
        try
        {
            AiCapabilitySnapshot? previous;
            AiCapabilitySnapshot onEntry;
            long generation;
            var identity = CredentialIdentity(configuration);
            lock (_stateGate)
            {
                ct.ThrowIfCancellationRequested();
                onEntry = GetCached(configuration);
                if (_cached?.Configuration != configuration || identity != _credentialIdentity)
                    InvalidateCore();
                previous = _cached;
                generation = _generation;
                if (previous is not null && _clock.GetUtcNow() - _metadataAt < MetadataLifetime
                    && (!probe || _clock.GetUtcNow() - _probeAt < ProbeCooldown(previous)))
                    return (GetCached(configuration), false);
            }

            var observer = observers.FirstOrDefault(p => p.Kind == configuration.Kind);
            if (observer is null) return (new(configuration), false);
            timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_stateGate)
            {
                if (generation != _generation) throw new OperationCanceledException("AI capability configuration changed.");
                _inFlight = timeout;
            }
            // Metadata is cheap; explicit cold-start probes get a separate bounded window.
            timeout.CancelAfter(probe ? TimeSpan.FromSeconds(90) : TimeSpan.FromSeconds(10));
            var snapshot = new AiCapabilitySnapshot(configuration);
            var didProbe = false;
            var identityChanged = false;
            try
            {
                snapshot = await observer.ReadMetadataAsync(configuration, timeout.Token)
                    .WaitAsync(timeout.Token).ConfigureAwait(false);
                timeout.Token.ThrowIfCancellationRequested();
                ValidateConfiguration(snapshot, configuration);
                var sameIdentity = previous is not null
                    && previous.RuntimeIdentity == snapshot.RuntimeIdentity && previous.ModelIdentity == snapshot.ModelIdentity;
                identityChanged = !sameIdentity;
                lock (_stateGate)
                {
                    if (generation != _generation || identity != CredentialIdentity(configuration))
                        throw new OperationCanceledException("AI capability configuration changed.");
                }
                if (sameIdentity && _clock.GetUtcNow() - _probeAt < ProbeCooldown(previous!))
                {
                    snapshot = snapshot with
                    {
                        Chat = PreferMetadata(snapshot.Chat, previous!.Chat),
                        Tools = PreferMetadata(snapshot.Tools, previous.Tools),
                        StructuredJson = PreferMetadata(snapshot.StructuredJson, previous.StructuredJson),
                        Streaming = PreferMetadata(snapshot.Streaming, previous.Streaming),
                        Context = snapshot.Context.Support == AiSupport.Unknown ? previous.Context : snapshot.Context,
                    };
                }
                else if (probe)
                {
                    didProbe = true;
                    snapshot = await observer.ProbeAsync(snapshot, timeout.Token)
                        .WaitAsync(timeout.Token).ConfigureAwait(false);
                    ValidateConfiguration(snapshot, configuration);
                }
                timeout.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                snapshot = snapshot with { ProbeTimedOut = true };
            }
            // Caller cancellation and invalidated in-flight observations never publish.
            ct.ThrowIfCancellationRequested();
            snapshot = snapshot with { ObservedAt = _clock.GetUtcNow() };
            lock (_stateGate)
            {
                ct.ThrowIfCancellationRequested();
                if (generation != _generation || identity != CredentialIdentity(configuration))
                    throw new OperationCanceledException("AI capability configuration changed.");
                _cached = snapshot;
                _credentialIdentity = identity;
                _metadataAt = _clock.GetUtcNow();
                if (identityChanged) _probeAt = default;
                if (didProbe) _probeAt = _clock.GetUtcNow();
                AiConversationContext.SetObservedLimit(configuration,
                    snapshot.Context.Support == AiSupport.Supported ? snapshot.Context.InputByteCeiling : null,
                    (snapshot.Context.Source == AiObservationSource.HarmlessProbe ? _probeAt : snapshot.ObservedAt) + ProbeLifetime,
                    snapshot.Context.ContextTokens);
            }
            var result = GetCached(configuration);
            // Compare the evidence, not the reading: a metadata refresh that observes exactly the
            // same state carries a fresh ObservedAt, and announcing that would churn the UI on a
            // timer for no visible reason.
            return (result, result with { ObservedAt = default } != onEntry with { ObservedAt = default });
        }
        finally
        {
            lock (_stateGate) _inFlight = null;
            timeout?.Dispose();
            _gate.Release();
        }
    }

    private static AiCapabilityObservation PreferMetadata(AiCapabilityObservation metadata, AiCapabilityObservation probe) =>
        metadata.Support == AiSupport.Unknown ? probe : metadata;

    private static AiCapabilityObservation WithoutExpiredProbe(AiCapabilityObservation observation) =>
        observation.Source == AiObservationSource.HarmlessProbe ? new() : observation;

    private static void ValidateConfiguration(AiCapabilitySnapshot snapshot, AiChatConfiguration configuration)
    {
        if (snapshot.Configuration != configuration)
            throw new InvalidOperationException("The capability observer returned a different configuration; observations were not saved.");
    }

    private static TimeSpan ProbeCooldown(AiCapabilitySnapshot snapshot) =>
        snapshot.CanChat ? ProbeLifetime : MetadataLifetime;

    private string CredentialIdentity(AiChatConfiguration configuration)
    {
        if (configuration.Kind is not (AiProviderKind.OpenAi or AiProviderKind.AzureOpenAi)) return "";
        credentials.TryReadSecret(configuration.Kind, out var secret);
        return HashIdentity(secret ?? "");
    }

    internal static string HashIdentity(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}
