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

using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>
/// Checks Compose network subnet, gateway and IP-range validation before settings become engine arguments.
/// </summary>
public sealed class NetworkAddressingTests
{
    [Theory]
    [InlineData(null, null, null)]
    [InlineData("", " ", "")]
    [InlineData("172.28.0.0/16", null, null)]
    [InlineData("172.28.0.0/16", "172.28.0.1", "172.28.5.0/24")]
    [InlineData("fd00:1::/64", "fd00:1::1", "fd00:1::/80")]
    public void AcceptsValidCombinations(string? subnet, string? gateway, string? ipRange) =>
        Assert.Null(NetworkAddressing.Validate(subnet, gateway, ipRange));

    [Theory]
    [InlineData(null, "172.28.0.1", null, "Enter a subnet")]
    [InlineData(null, null, "172.28.5.0/24", "Enter a subnet")]
    [InlineData("172.28.0.0", null, null, "not a valid subnet")]
    [InlineData("-o evil", null, null, "not a valid subnet")]
    [InlineData("172.28.0.0/16", "10.0.0.1", null, "not inside the subnet")]
    [InlineData("172.28.0.0/16", "172.28.0.1/16", null, "not a valid gateway")]
    [InlineData("172.28.0.0/16", null, "10.1.0.0/24", "must be inside the subnet")]
    [InlineData("172.28.0.0/24", null, "172.28.0.0/16", "must be inside the subnet")]
    [InlineData("172.28.0.0/16", null, "fd00::/64", "must be inside the subnet")]
    public void RejectsInvalidCombinations(string? subnet, string? gateway, string? ipRange, string expected) =>
        Assert.Contains(expected, NetworkAddressing.Validate(subnet, gateway, ipRange));
}
