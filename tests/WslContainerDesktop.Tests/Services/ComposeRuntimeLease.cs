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

using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;

namespace WslContainerDesktop.Tests.Services;

/// <summary>Test-only deny-by-default lease around the real CLI service. Never uses prune or pulls.</summary>
internal sealed class ComposeRuntimeLease : IAsyncDisposable
{
    internal const string OwnerLabel = "com.wsldesktop.conformance-run";
    private readonly IWslcService real;
    private readonly ProcessRunner runner;
    private readonly Dictionary<string, string?> containers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string?> networks = new(StringComparer.Ordinal);
    private readonly List<object> events = [];
    private readonly string evidence;
    private readonly WslcCapabilitiesService capabilities;
    private string[] baselineNetworks = [];
    private string[] baselineVolumes = [];
    private bool initialized;
    public string RunId { get; } = "wcd-conformance-" + Guid.NewGuid().ToString("N");
    public string Image { get; }
    public IWslcService Service { get; }
    public ISettingsService Settings { get; }
    public IWslcCapabilitiesService Capabilities => capabilities;
    public RestartSuppressionState Suppression { get; } = new();
    public Action<string>? BeforeStart { get; set; }
    public List<string> Started { get; } = [];

    public ComposeRuntimeLease()
    {
        if (Environment.GetEnvironmentVariable("WCD_COMPOSE_RUNTIME") != ComposeRuntimeFactAttribute.Consent)
            throw new InvalidOperationException("Runtime consent is absent; no engine access permitted.");
        var path = Required("WCD_RUNTIME_WSLC");
        var hash = Required("WCD_RUNTIME_WSLC_SHA256");
        Image = Required("WCD_RUNTIME_IMAGE_ID");
        evidence = Required("WCD_RUNTIME_EVIDENCE");
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path) ||
            !Path.IsPathFullyQualified(evidence) || !Directory.Exists(evidence))
            throw new InvalidOperationException("An existing absolute WSLC executable and evidence directory are required.");
        if (!IsDigest(hash) || !IsDigest(Image))
            throw new InvalidOperationException("WSLC checksum and preloaded image ID must be full 64-digit SHA256 values.");
        using (var stream = File.OpenRead(path))
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("WSLC executable checksum changed; no engine access permitted.");
        var health = new List<HealthCheckConfig>();
        var restart = new List<RestartPolicyConfig>();
        Settings = NetworkTestProxy.Create<ISettingsService>((method, args) => method.Name switch
        {
            "get_WslcPath" => path,
            "get_HealthChecks" => health,
            "set_HealthChecks" => SetHealth((List<HealthCheckConfig>)args[0]!),
            "get_RestartPolicies" => restart,
            "set_RestartPolicies" => SetRestart((List<RestartPolicyConfig>)args[0]!),
            "add_Changed" or "remove_Changed" or nameof(ISettingsService.Save) => null,
            _ => throw new InvalidOperationException($"Unapproved runtime setting: {method.Name}."),
        });
        object? SetHealth(List<HealthCheckConfig> value) { health = value; return null; }
        object? SetRestart(List<RestartPolicyConfig> value) { restart = value; return null; }
        capabilities = new(Settings, NullLogger<WslcCapabilitiesService>.Instance);
        runner = new ProcessRunner(Settings);
        real = new WslcService(runner, NullLogger<WslcService>.Instance,
            capabilities, Settings, Suppression);
        Service = NetworkTestProxy.Create<IWslcService>(Dispatch);
        Record("lease", new { executableSha256 = hash.ToLowerInvariant(), imageId = Image });
    }

    public async Task InitializeAsync(CancellationToken ct)
    {
        Require(await real.GetVersionAsync(ct), "WSLC version");
        var existing = await real.ListContainersAsync(true, ct);
        if (existing.Count != 0)
            throw new InvalidOperationException("Runtime suite requires an empty disposable container inventory. Existing workloads were not changed.");
        baselineNetworks = (await ReadNetworksAsync(ct)).Select(n => n.Name + ":" + n.Id).Order(StringComparer.Ordinal).ToArray();
        baselineVolumes = (await ReadVolumesAsync(ct)).Select(v => v.Name).Order(StringComparer.Ordinal).ToArray();
        var image = await real.InspectImageAsync(Image, ct);
        Require(image, "Preloaded image lookup (pulling is forbidden)");
        using var doc = JsonDocument.Parse(image.StandardOutput);
        var root = Object(doc.RootElement);
        var id = root.GetProperty("Id").GetString();
        if (id?.Replace("sha256:", "", StringComparison.Ordinal) != Image)
            throw new InvalidOperationException("Inspected image ID does not match approved immutable local image.");
        var snapshot = await capabilities.GetAsync(ct);
        foreach (var feature in new[] { WslcFeature.NetworkConnect, WslcFeature.NetworkDisconnect })
            if (snapshot[feature].Support != WslcCapabilitySupport.Supported)
                throw new InvalidOperationException($"Runtime multi-network prerequisite {feature}: {snapshot[feature].Diagnostic}. No mutation attempted.");
        Record("preflight", new
        {
            version = (await real.GetVersionAsync(ct)).StandardOutput.Trim(),
            baselineNetworks, baselineVolumes,
            networkConnect = snapshot[WslcFeature.NetworkConnect],
            networkDisconnect = snapshot[WslcFeature.NetworkDisconnect],
        });
        initialized = true;
    }

    public async Task<string> CreateNetworkAsync(string suffix, CancellationToken ct)
    {
        if (!initialized) throw new InvalidOperationException("Runtime preflight has not succeeded.");
        var name = RunId + "-" + suffix;
        var before = await ReadNetworksAsync(ct);
        if (before.Any(n => n.Name == name))
            throw new InvalidOperationException("Network name collision; refusing adoption.");
        networks.Add(name, null);
        Require(await real.CreateNetworkAsync(name, driver: null, driverOpts: null,
            labels: new Dictionary<string, string> { [OwnerLabel] = RunId }, ct: ct), "Create owned network");
        await RequireNetworkAsync(name, ct);
        Record("network-created", new { name, id = networks[name] });
        return name;
    }

    private object Dispatch(MethodInfo method, object?[] args)
    {
        if (method.Name == nameof(IWslcService.ListContainersAsync))
            return real.ListContainersAsync((bool)args[0]!, (CancellationToken)args[1]!);
        if (method.Name == nameof(IWslcService.InspectContainerAsync))
            return InspectAsync((string)args[0]!, (CancellationToken)args[1]!);
        if (method.Name == nameof(IWslcService.InspectNetworkAsync))
            return InspectNetworkAsync((string)args[0]!, (CancellationToken)args[1]!);
        if (method.ReturnType != typeof(Task<CommandResult>))
            throw new InvalidOperationException($"Unapproved runtime method {method.Name}.");
        return MutateAsync(method, args);
    }

    private async Task<CommandResult> MutateAsync(MethodInfo method, object?[] args)
    {
        if (!initialized) throw new InvalidOperationException("Runtime preflight has not succeeded.");
        var ct = args.OfType<CancellationToken>().Single();
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(ct);
        bounded.CancelAfter(TimeSpan.FromSeconds(30));
        ct = bounded.Token;
        if (method.Name is nameof(IWslcService.CreateContainerAsync) or nameof(IWslcService.RunContainerAsync))
        {
            var options = (RunContainerOptions)args[0]!;
            ValidateOptions(options, RunId, Image);
            foreach (var network in options.GetNetworkAttachments())
                await RequireNetworkAsync(network.Network, ct);
            var before = await real.ListContainersAsync(true, ct);
            if (before.Any(c => c.Name == options.Name))
                throw new InvalidOperationException("Container name collision; refusing replacement.");
            if (containers.ContainsKey(options.Name!))
                throw new InvalidOperationException("Creation already attempted for this name; unresolved ownership.");
            containers.Add(options.Name!, null);
            options = options.Clone();
            options.Labels[OwnerLabel] = RunId;
            var result = method.Name == nameof(IWslcService.CreateContainerAsync)
                ? await real.CreateContainerAsync(options, ct)
                : await real.RunContainerAsync(options, ct);
            Record("create-attempt", new { name = options.Name, result.ExitCode });
            // Failed or cancelled calls keep the reserved name for independent cleanup.
            if (result.Success)
            {
                await RequireContainerAsync(options.Name!, ct);
                if (method.Name == nameof(IWslcService.RunContainerAsync))
                    Started.Add(options.Name!);
            }
            return result;
        }
        var target = method.Name == nameof(IWslcService.ConnectNetworkAsync) ||
                     method.Name == nameof(IWslcService.DisconnectNetworkAsync) ? (string)args[1]! : (string)args[0]!;
        var state = await RequireContainerAsync(target, ct);
        var name = containers.Single(c => c.Value == state.Id).Key;
        CommandResult response;
        switch (method.Name)
        {
            case nameof(IWslcService.StartContainerAsync):
                BeforeStart?.Invoke(name);
                response = await real.StartContainerAsync(state.Id, ct, (bool)args[2]!);
                if (response.Success)
                {
                    Started.Add(name);
                }
                break;
            case nameof(IWslcService.StopContainerAsync):
                response = await real.StopContainerAsync(state.Id, ct);
                break;
            case nameof(IWslcService.RemoveContainerAsync):
                response = await real.RemoveContainerAsync(state.Id, true, ct);
                if (response.Success) containers.Remove(name);
                break;
            case nameof(IWslcService.ConnectNetworkAsync):
                var endpoint = (NetworkAttachment)args[0]!;
                await RequireNetworkAsync(endpoint.Network, ct);
                response = await real.ConnectNetworkAsync(endpoint, state.Id, ct);
                break;
            case nameof(IWslcService.DisconnectNetworkAsync):
                await RequireNetworkAsync((string)args[0]!, ct);
                response = await real.DisconnectNetworkAsync((string)args[0]!, state.Id, ct);
                break;
            case nameof(IWslcService.ExecHealthAsync):
                response = await real.ExecHealthAsync(state.Id, (NativeHealthOptions)args[1]!, ct);
                break;
            default:
                throw new InvalidOperationException($"Unapproved runtime mutation {method.Name}; no command executed.");
        }
        Record(method.Name, new { name, id = state.Id, response.ExitCode });
        return response;
    }

    internal static void ValidateOptions(RunContainerOptions options, string runId, string image)
    {
        if (options.Name is null || !options.Name.StartsWith(runId + "_", StringComparison.Ordinal) ||
            options.Image != image || options.Volumes.Count != 0 || options.PortMappings.Count != 0 ||
            options.AllGpus || options.RemoveOnExit || options.HasSpecialNetworkMode ||
            options.GetNetworkAttachments().Any(n => !n.Network.StartsWith(runId + "-", StringComparison.Ordinal)))
            throw new InvalidOperationException("Runtime creation must use the approved local image, owned name/networks, no host mounts, published ports, GPU or special network mode.");
    }

    private async Task<CommandResult> InspectAsync(string target, CancellationToken ct)
    {
        if (!containers.ContainsKey(target) && !containers.Values.Contains(target))
            throw new InvalidOperationException("Inspect target is not reserved by this runtime lease.");
        return await real.InspectContainerAsync(target, ct);
    }

    public async Task<ContainerNetworkState> RequireContainerAsync(string target, CancellationToken ct)
    {
        var inspected = await InspectAsync(target, ct);
        Require(inspected, "Inspect owned container");
        var state = ContainerNetworkState.Parse(inspected.StandardOutput);
        var name = containers.ContainsKey(target) ? target : containers.Single(c => c.Value == target).Key;
        RequireOwnership(state, RunId, containers[name]);
        containers[name] = state.Id;
        return state;
    }

    internal static void RequireOwnership(ContainerNetworkState state, string runId, string? expectedId)
    {
        if (!state.HasLabel(OwnerLabel, runId) || expectedId is not null && state.Id != expectedId)
            throw new InvalidOperationException("Container ownership/identity changed; refusing mutation or cleanup.");
    }

    private async Task<CommandResult> InspectNetworkAsync(string name, CancellationToken ct)
    {
        await RequireNetworkAsync(name, ct);
        return await real.InspectNetworkAsync(name, ct);
    }

    private async Task RequireNetworkAsync(string name, CancellationToken ct)
    {
        if (!networks.TryGetValue(name, out var expected))
            throw new InvalidOperationException("Network is not reserved by this runtime lease.");
        var inspected = await real.InspectNetworkAsync(name, ct);
        Require(inspected, "Inspect owned network");
        using var doc = JsonDocument.Parse(inspected.StandardOutput);
        var root = Object(doc.RootElement);
        var id = root.GetProperty("Id").GetString();
        if (string.IsNullOrWhiteSpace(id) || !root.TryGetProperty("Labels", out var labels) ||
            !labels.TryGetProperty(OwnerLabel, out var owner) || owner.GetString() != RunId ||
            expected is not null && id != expected)
            throw new InvalidOperationException("Network ownership/identity unavailable or changed; refusing mutation or cleanup.");
        networks[name] = id;
    }

    public void Record(string action, object detail) =>
        events.Add(new { atUtc = DateTimeOffset.UtcNow, action, detail });

    public async ValueTask DisposeAsync()
    {
        var errors = new List<Exception>();
        // Each resource gets an independent bounded cleanup token; one failure cannot skip the rest.
        foreach (var name in containers.Keys.Reverse().ToArray())
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                var inventory = await real.ListContainersAsync(true, cleanup.Token);
                if (!inventory.Any(c => c.Name == name || c.Id == containers[name])) continue;
                var state = await RequireContainerAsync(name, cleanup.Token);
                Require(await real.RemoveContainerAsync(state.Id, true, cleanup.Token), "Cleanup owned container");
            }
            catch (Exception ex) { errors.Add(ex); Record("cleanup-refused-or-failed", new { name, error = ex.Message }); }
        }
        foreach (var name in networks.Keys.Reverse().ToArray())
        {
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            try
            {
                if (!(await ReadNetworksAsync(cleanup.Token)).Any(n => n.Name == name)) continue;
                await RequireNetworkAsync(name, cleanup.Token);
                Require(await real.RemoveNetworkAsync(name, cleanup.Token), "Cleanup owned network");
            }
            catch (Exception ex) { errors.Add(ex); Record("cleanup-refused-or-failed", new { name, error = ex.Message }); }
        }
        try
        {
            if (initialized)
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var remaining = await real.ListContainersAsync(true, cleanup.Token);
                var afterNetworks = (await ReadNetworksAsync(cleanup.Token)).Select(n => n.Name + ":" + n.Id).Order(StringComparer.Ordinal).ToArray();
                var afterVolumes = (await ReadVolumesAsync(cleanup.Token)).Select(v => v.Name).Order(StringComparer.Ordinal).ToArray();
                if (remaining.Count != 0 || !baselineNetworks.SequenceEqual(afterNetworks) || !baselineVolumes.SequenceEqual(afterVolumes))
                    throw new InvalidOperationException("Post-run inventory differs from baseline; no global cleanup will be attempted.");
                Record("cleanup-verified", new { afterNetworks, afterVolumes });
            }
        }
        catch (Exception ex) { errors.Add(ex); }
        finally
        {
            capabilities.Dispose();
            var report = Path.Combine(evidence, RunId + ".json");
            await File.WriteAllTextAsync(report, JsonSerializer.Serialize(new
            {
                runId = RunId, events, cleanupErrors = errors.Select(e => e.Message).ToArray(),
                evidenceScope = "Real CLI service, Compose orchestrator/supervisor, test-driven health observations; not packaged UI watchdog certification.",
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        if (errors.Count > 0) throw new AggregateException("Runtime lease cleanup/inventory verification failed.", errors);
    }

    private static string Required(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value :
            throw new InvalidOperationException($"Explicit runtime configuration {name} is required.");
    private async Task<IReadOnlyList<NetworkInfo>> ReadNetworksAsync(CancellationToken ct) =>
        ParseNetworks(await runner.RunAsync(["network", "list", "--format", "json"], ct));
    private async Task<IReadOnlyList<VolumeInfo>> ReadVolumesAsync(CancellationToken ct) =>
        WslcJsonParser.ParseVolumes(await runner.RunAsync(["volume", "list", "--format", "json"], ct));

    internal static IReadOnlyList<NetworkInfo> ParseNetworks(CommandResult result)
    {
        Require(result, "Authoritative network inventory; absence is unknown on failure");
        var values = WslcJsonParser.ParseList<NetworkInfo>(result.StandardOutput);
        if (values.Any(n => n is null || string.IsNullOrWhiteSpace(n.Name) || string.IsNullOrWhiteSpace(n.Id)) ||
            values.Select(n => n.Name).Distinct(StringComparer.Ordinal).Count() != values.Count ||
            values.Select(n => n.Id).Distinct(StringComparer.Ordinal).Count() != values.Count)
            throw new JsonException("Network inventory contains missing or duplicate identities.");
        return values;
    }
    private static bool IsDigest(string value) => value.Length == 64 && value.All(Uri.IsHexDigit);
    private static JsonElement Object(JsonElement root) =>
        root.ValueKind == JsonValueKind.Array && root.GetArrayLength() == 1 ? root[0] : root;
    public static void Require(CommandResult result, string context)
    {
        if (!result.Success) throw new InvalidOperationException($"{context}: {result.ErrorText}");
    }
}
