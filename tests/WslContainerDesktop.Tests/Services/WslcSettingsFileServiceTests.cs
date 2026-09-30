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

using Microsoft.Extensions.Logging.Abstractions;
using WslContainerDesktop.Models;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

/// <summary>Covers <c>wslc</c> settings-file edits so storage paths, comments, CRLF, YAML quoting, parsing, and validation are preserved.</summary>
public sealed class WslcSettingsFileServiceTests
{
    [Fact]
    public void InsertsSessionBlockWhenMissing()
    {
        var updated = WslcSettingsFileService.SetSessionStoragePath("# settings\r\ncredentialStore: wincred\r\n", @"D:\wslc");
        Assert.Contains("session:\r\n  storagePath: 'D:\\wslc'\r\n", updated);
        Assert.Contains("credentialStore: wincred", updated);
    }

    [Fact]
    public void UncommentsStoragePathAndPreservesCrlfAndComments()
    {
        var text = "# header\r\nsession:\r\n  # storagePath: default\r\n  # cpuCount: default\r\n";
        var updated = WslcSettingsFileService.SetSessionStoragePath(text, @"D:\empty");
        Assert.Contains("\r\n", updated);
        Assert.Contains("  storagePath: 'D:\\empty'\r\n", updated);
        Assert.Contains("  # cpuCount: default", updated);
    }

    [Fact]
    public void ReplacesExistingStoragePathAndPreservesOtherKeys()
    {
        var text = "session:\n  cpuCount: 4\n  storagePath: C:\\old\n  memorySize: 2GB\n";
        var updated = WslcSettingsFileService.SetSessionStoragePath(text, @"D:\new");
        Assert.Contains("  cpuCount: 4\n", updated);
        Assert.Contains("  storagePath: 'D:\\new'\n", updated);
        Assert.Contains("  memorySize: 2GB\n", updated);
    }

    [Fact]
    public void ResetWritesDefault()
    {
        var updated = WslcSettingsFileService.SetSessionStoragePath("session:\n  storagePath: D:\\new\n", "default");
        Assert.Contains("storagePath: default", updated);
    }

    [Fact]
    public void ParsesSessionAndCredentialValues()
    {
        var settings = WslcSettingsFileService.Parse("""
            session:
              storagePath: D:\wslc
              cpuCount: 4
              memorySize: 8GB
              maxStorageSize: 500GB
              defaultBindingAddress: 0.0.0.0
            credentialStore: file
            """);

        Assert.Equal(@"D:\wslc", settings.StoragePath);
        Assert.Equal("4", settings.CpuCount);
        Assert.Equal("8GB", settings.MemorySize);
        Assert.Equal("500GB", settings.MaxStorageSize);
        Assert.Equal("0.0.0.0", settings.DefaultBindingAddress);
        Assert.Equal("file", settings.CredentialStore);
    }

    [Fact]
    public void QuotesPathsWithYamlIndicatorsAndRoundTrips()
    {
        var path = @"D:\it's #1: data";
        var updated = WslcSettingsFileService.SetSessionStoragePath("session:\n  storagePath: default\n", path);
        Assert.Contains("  storagePath: 'D:\\it''s #1: data'\n", updated);
        Assert.Equal(path, WslcSettingsFileService.Parse(updated).StoragePath);
    }

    [Fact]
    public async Task SetStoragePathRewritesExistingFileInPlaceWithoutBom()
    {
        var root = Path.Combine(Path.GetTempPath(), "wcd-settings-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var file = Path.Combine(root, "settings.yaml");
        await File.WriteAllTextAsync(file, "# header\nsession:\n  # storagePath: default\n");
        try
        {
            var service = new WslcSettingsFileService(
                NetworkTestProxy.Create<IWslcService>((_, _) => Task.FromResult(new WslcSystemInfo
                {
                    Client = new WslcClientInfo { SettingsFile = file },
                })),
                NullLogger<WslcSettingsFileService>.Instance);

            await service.SetStoragePathAsync(@"E:\wslc");

            var bytes = await File.ReadAllBytesAsync(file);
            Assert.NotEqual(0xEF, bytes[0]);
            Assert.Equal(@"E:\wslc", WslcSettingsFileService.Parse(await File.ReadAllTextAsync(file)).StoragePath);
            Assert.Single(Directory.GetFiles(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SetStoragePathRefusesToCreateMissingSettingsFile()
    {
        var file = Path.Combine(Path.GetTempPath(), "wcd-missing-" + Guid.NewGuid().ToString("N"), "settings.yaml");
        var service = new WslcSettingsFileService(
            NetworkTestProxy.Create<IWslcService>((_, _) => Task.FromResult(new WslcSystemInfo
            {
                Client = new WslcClientInfo { SettingsFile = file },
            })),
            NullLogger<WslcSettingsFileService>.Instance);

        Assert.False((await service.ReadAsync()).DirectEditAvailable);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.SetStoragePathAsync(@"E:\wslc"));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void ValidatesStoragePathMustBeEmptyDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "wcd-storage-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var service = new WslcSettingsFileService(
                NetworkTestProxy.Create<IWslcService>((_, _) => Task.FromResult(new WslcSystemInfo())),
                NullLogger<WslcSettingsFileService>.Instance);
            Assert.True(service.ValidateStoragePath(root).IsValid);
            File.WriteAllText(Path.Combine(root, "file.txt"), "x");
            Assert.False(service.ValidateStoragePath(root).IsValid);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
