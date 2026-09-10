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
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;

namespace WslContainerDesktop.Services;

/// <summary>No inferred endpoint, DNS destination, proxy, credentials, or redirect escape.</summary>
public static class FoundryLocalEndpoint
{
    public static Uri Validate(string? endpoint)
    {
        var value = endpoint?.Trim() ?? "";
        if (!Regex.IsMatch(value, @"^https?://(?:\[[^\]]+\]|[^:/?#@]+):[0-9]{1,5}(?:/v1)?/?$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || !Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || !uri.IsWellFormedOriginalString() || uri.Port <= 0
            || !(uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                || IPAddress.TryParse(uri.Host.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address)))
            throw new ArgumentException("Foundry Local requires an explicit loopback HTTP(S) URL and port, optionally ending in /v1. Remote hosts, credentials, query strings and fragments are not allowed.");
        return uri;
    }

    public static Uri BuildUri(string endpoint, string route)
    {
        var uri = Validate(endpoint);
        // Preserve localhost for HTTP authority and TLS certificate-name validation.
        // The dedicated socket callback pins the destination without DNS/hosts resolution.
        var builder = new UriBuilder(uri)
        {
            Path = "/" + route,
            Query = "",
            Fragment = "",
        };
        return builder.Uri;
    }
}

/// <summary>Kept separate from the shared client, which permits redirects.</summary>
public sealed class FoundryLocalHttpClient : IDisposable
{
    internal AiHttpClient Transport { get; }
    public FoundryLocalHttpClient() : this(CreateHandler()) { }
    internal FoundryLocalHttpClient(HttpMessageHandler handler) => Transport = new AiHttpClient(handler);
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        Credentials = null,
        ConnectCallback = ConnectLoopbackAsync,
    };

    internal static IPEndPoint SocketEndpoint(DnsEndPoint endpoint)
    {
        var address = endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            ? IPAddress.Loopback
            : IPAddress.TryParse(endpoint.Host.Trim('[', ']'), out var literal) && IPAddress.IsLoopback(literal)
                ? literal : throw new ArgumentException("Foundry Local socket destinations must be loopback.");
        if (endpoint.Port <= 0) throw new ArgumentException("An explicit Foundry Local socket port is required.");
        return new(address, endpoint.Port);
    }

    private static async ValueTask<Stream> ConnectLoopbackAsync(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var endpoint = SocketEndpoint(context.DnsEndPoint);
        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            // Transfer ownership only after connection succeeds.
            socket.Dispose();
            throw;
        }
    }
    public void Dispose() => Transport.Dispose();
}
