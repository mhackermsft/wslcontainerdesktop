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

using System.Globalization;
using System.Text.Json;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Models;

public sealed class ContainerInfoCompatibilityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void MixedSchemas_SupportArraysAndObjectStreams(bool array)
    {
        const string legacy = """
            {"Id":"legacy","Name":"/old","Image":"nginx","State":2,"CreatedAt":1,"StateChangedAt":2,
             "Ports":[{"BindingAddress":"127.0.0.1","ContainerPort":80,"HostPort":8080,"Protocol":6}]}
            """;
        // Observed WSLC 2.9.11 list representation, including stopped-container empty display ports.
        const string current = """
            {"ID":"fe1efb58e1ab","Names":"wslcd-ollama","Image":"ollama/ollama","State":"exited",
             "CreatedAt":"2026-07-17 14:19:31 -0400 EDT","Ports":"","Status":"Exited (0) 6 days ago",
             "Networks":"bridge","Mounts":"wslcd-ollama"}
            """;
        var rows = WslcJsonParser.ParseList<ContainerInfo>(array ? $"[{legacy},{current}]" : $"{legacy}\n{current}");
        Assert.Equal(2, rows.Count);
        Assert.Equal("old", rows[0].Name);
        Assert.Equal(ContainerState.Running, rows[0].State);
        Assert.Equal(8080, Assert.Single(rows[0].Ports).HostPort);
        Assert.True(rows[0].PortsKnown);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2), rows[0].StateChangedUtc);
        Assert.Equal("wslcd-ollama", rows[1].Name);
        Assert.Equal(ContainerState.Stopped, rows[1].State);
        Assert.Equal(new DateTimeOffset(2026, 7, 17, 18, 19, 31, TimeSpan.Zero), rows[1].CreatedUtc);
        Assert.False(rows[1].PortsKnown);
        Assert.Equal(rows[1].CreatedUtc, rows[1].StateChangedUtc);
    }

    [Theory]
    [InlineData("1", ContainerState.Created)]
    [InlineData("2", ContainerState.Running)]
    [InlineData("3", ContainerState.Stopped)]
    [InlineData("4", ContainerState.Paused)]
    [InlineData("999", ContainerState.Unknown)]
    [InlineData("\"running\"", ContainerState.Running)]
    [InlineData("\"CREATED\"", ContainerState.Created)]
    [InlineData("\"exited\"", ContainerState.Stopped)]
    [InlineData("\"stopped\"", ContainerState.Stopped)]
    [InlineData("\"paused\"", ContainerState.Paused)]
    [InlineData("\"restarting\"", ContainerState.Unknown)]
    [InlineData("\"dead\"", ContainerState.Unknown)]
    [InlineData("null", ContainerState.Unknown)]
    public void States_AreExplicit(string state, ContainerState expected) =>
        Assert.Equal(expected, Parse($$"""{"Id":"a","State":{{state}}}""").State);

    [Fact]
    public void MissingStateAndNames_AreSafe()
    {
        var row = Parse("""{"Id":"a","Names":["/first","second"]}""");
        Assert.Equal(ContainerState.Unknown, row.State);
        Assert.Equal("first", row.Name);
        Assert.Equal(DateTimeOffset.UnixEpoch, row.CreatedUtc);
        Assert.False(row.CreatedAtKnown);
        Assert.False(row.PortsKnown);
        Assert.Equal("preferred", Parse("""{"Id":"a","Name":"preferred","Names":"alias"}""").Name);
    }

    [Theory]
    [InlineData("18446744011573954816")]
    [InlineData("9223372036854775807")]
    [InlineData("-1")]
    [InlineData("0")]
    [InlineData("\"not a date\"")]
    [InlineData("null")]
    public void NeverStartedOrInvalidStateTime_FallsBackWithoutThrowing(string value)
    {
        var row = Parse($$"""{"Id":"a","CreatedAt":10,"StateChangedAt":{{value}}}""");
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(10), row.StateChangedUtc);
        Assert.False(row.StateChangedAtKnown);
    }

    [Theory]
    [InlineData("9223372036854775807")]
    [InlineData("-62135596801")]
    [InlineData("\"invalid\"")]
    [InlineData("null")]
    public void InvalidCreation_FallsBackWithoutThrowing(string value)
    {
        var row = Parse($$"""{"Id":"a","CreatedAt":{{value}}}""");
        Assert.Equal(DateTimeOffset.UnixEpoch, row.CreatedUtc);
        Assert.False(row.CreatedAtKnown);
    }

    [Theory]
    [InlineData("fr-FR")]
    [InlineData("ar-SA")]
    public void FormattedDates_AreCultureIndependent(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var row = Parse("""
                {"Id":"a","CreatedAt":"2026-07-17 14:19:31 -0400 EDT","StateChangedAt":"2026-09-02T16:01:46.133740583Z"}
                """);
            Assert.Equal(new DateTimeOffset(2026, 7, 17, 18, 19, 31, TimeSpan.Zero), row.CreatedUtc);
            Assert.Equal(new DateTimeOffset(2026, 9, 2, 16, 1, 46, TimeSpan.Zero), row.StateChangedUtc);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Theory]
    [InlineData("\"\"", false)]
    [InlineData("\"80/tcp\"", false)]
    [InlineData("\"127.0.0.1:8000-8001->80-81/tcp\"", false)]
    [InlineData("\"127.0.0.1:8080->80/tcp, invalid\"", false)]
    [InlineData("null", false)]
    [InlineData("[]", true)]
    public void EmptyOrAmbiguousPorts_AreNotFalseAbsence(string ports, bool known)
    {
        var row = Parse($$"""{"Id":"a","Ports":{{ports}}}""");
        Assert.Equal(known, row.PortsKnown);
        Assert.Empty(row.Ports);
    }

    [Fact]
    public void ExplicitDisplayBindings_AreLossless()
    {
        var row = Parse("""{"Id":"a","Ports":"127.0.0.1:8080->80/tcp, [::1]:5353->53/udp"}""");
        Assert.True(row.PortsKnown);
        Assert.Equal(2, row.Ports.Count);
        Assert.Equal(6, row.Ports[0].Protocol);
        Assert.Equal(17, row.Ports[1].Protocol);
        Assert.Equal("http://[::1]:5353", row.Ports[1].HostUrl);
    }

    [Fact]
    public void LegacyOptionalStructuredFields_KeepTheirDefaults()
    {
        var row = Parse("""{"Id":"a","Ports":[{"ContainerPort":80}]}""");
        Assert.True(row.PortsKnown);
        Assert.Equal(0, Assert.Single(row.Ports).HostPort);
        Assert.Equal(0, row.Ports[0].Protocol);
    }

    [Fact]
    public void DisplayIpv6Wildcard_UsesLoopbackUrl()
    {
        var row = Parse("""{"Id":"a","Ports":"[::]:8080->80/tcp"}""");
        Assert.Equal("http://localhost:8080", Assert.Single(row.Ports).HostUrl);
    }

    [Theory]
    [InlineData("""{"Id":"valid"} {"Id":""}""")]
    [InlineData("""[{"Id":"valid"},{"State":2}]""")]
    [InlineData("""{"Id":"a","State":{}}""")]
    [InlineData("""{"Id":"a","Ports":{}}""")]
    [InlineData("""{"Id":"a","Name":42}""")]
    [InlineData("""{"Id":"a"} {"Id":""")]
    public void MalformedRecords_RejectWholeInventory(string json) =>
        Assert.Throws<JsonException>(() => WslcJsonParser.ParseList<ContainerInfo>(json));

    [Theory]
    [InlineData("""[{"Id":"a"},null]""")]
    [InlineData("""[{"Id":"a"},{"ID":"a"}]""")]
    [InlineData("""{"Id":"a"} {"ID":"a"}""")]
    public void NullOrDuplicateRecords_AreNotSuccessfulInventories(string json) =>
        Assert.Throws<JsonException>(() => WslcJsonParser.ParseContainers(json));

    [Theory]
    [InlineData("")]
    [InlineData(" \r\n")]
    [InlineData("[]")]
    public void LegitimatelyEmptyInventory_Succeeds(string json) =>
        Assert.Empty(WslcJsonParser.ParseContainers(json));

    [Fact]
    public void Serialization_PreservesUnknownPorts()
    {
        var row = Parse("""{"Id":"a","Ports":""}""");
        Assert.False(Parse(JsonSerializer.Serialize(row)).PortsKnown);
    }

    [Fact]
    public void PersistedIdentity_CorrelatesUniqueShortAndFullIdsOnly()
    {
        const string shortId = "fe1efb58e1ab";
        const string fullId = shortId + "a1b531846d13c8683296f411edd9b9891f38c95ffecc9c9e1cbb";
        Assert.Equal(shortId, ContainerIdentity.ResolveId([shortId], fullId));
        Assert.Equal(fullId, ContainerIdentity.ResolveId([fullId], shortId));
        Assert.Null(ContainerIdentity.ResolveId([fullId, shortId + "000000000000"], shortId));
        Assert.Equal(fullId, ContainerIdentity.ResolveId([shortId, fullId], fullId));
        Assert.Null(ContainerIdentity.ResolveId([fullId], "fe1efb"));
        Assert.Null(ContainerIdentity.ResolveId(["not-hexadecimal-id-long"], "not-hexadecimal-id"));
    }

    private static ContainerInfo Parse(string json) => Assert.Single(WslcJsonParser.ParseList<ContainerInfo>(json));
}
