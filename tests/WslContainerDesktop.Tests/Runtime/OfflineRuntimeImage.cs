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

using System.Formats.Tar;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WslContainerDesktop.Tests.Runtime;

/// <summary>Builds a tiny local Linux image without fetching a container dependency.</summary>
internal static class OfflineRuntimeImage
{
    internal static void Create(string archive, string reference)
    {
        using var layer = new MemoryStream();
        using (var tar = new TarWriter(layer, TarEntryFormat.Pax, leaveOpen: true))
            Write(tar, "probe", LinuxProbe(), UnixFileMode.UserRead | UnixFileMode.UserWrite |
                UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        var layerBytes = layer.ToArray();
        var layerHash = Convert.ToHexStringLower(SHA256.HashData(layerBytes));
        var config = JsonSerializer.SerializeToUtf8Bytes(new
        {
            architecture = "amd64",
            os = "linux",
            config = new { Entrypoint = new[] { "/probe" } },
            rootfs = new { type = "layers", diff_ids = new[] { $"sha256:{layerHash}" } },
        });
        var configName = Convert.ToHexStringLower(SHA256.HashData(config)) + ".json";
        var layerName = layerHash + ".tar";
        var manifest = JsonSerializer.SerializeToUtf8Bytes(new[]
        {
            new { Config = configName, RepoTags = new[] { reference }, Layers = new[] { layerName } },
        });
        using var output = File.Create(archive);
        using var writer = new TarWriter(output, TarEntryFormat.Pax);
        Write(writer, configName, config, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Write(writer, layerName, layerBytes, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Write(writer, "manifest.json", manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void Write(TarWriter writer, string name, byte[] bytes, UnixFileMode mode)
    {
        using var data = new MemoryStream(bytes, writable: false);
        writer.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = data, Mode = mode });
    }

    private static byte[] LinuxProbe()
    {
        // A static x86-64 ELF writes "ready\n" and waits in pause(2), requiring no shell or image download.
        byte[] code =
        [
            0xb8, 1, 0, 0, 0, 0xbf, 1, 0, 0, 0,
            0x48, 0x8d, 0x35, 16, 0, 0, 0,
            0xba, 6, 0, 0, 0, 0x0f, 0x05,
            0xb8, 34, 0, 0, 0, 0x0f, 0x05, 0xeb, 0xf7,
            .. Encoding.ASCII.GetBytes("ready\n"),
        ];
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        writer.Write(new byte[] { 0x7f, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
        writer.Write((ushort)2); writer.Write((ushort)62); writer.Write(1u);
        writer.Write(0x400078ul); writer.Write(64ul); writer.Write(0ul);
        writer.Write(0u); writer.Write((ushort)64); writer.Write((ushort)56); writer.Write((ushort)1);
        writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0);
        writer.Write(1u); writer.Write(5u); writer.Write(0ul);
        writer.Write(0x400000ul); writer.Write(0x400000ul);
        writer.Write((ulong)(120 + code.Length)); writer.Write((ulong)(120 + code.Length));
        writer.Write(4096ul); writer.Write(code);
        return bytes.ToArray();
    }
}
