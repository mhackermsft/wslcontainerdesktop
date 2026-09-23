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

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using WslContainerDesktop.Services;
using Xunit;

namespace WslContainerDesktop.Tests.Services;

public sealed class MsixPackageInspectorTests : IDisposable
{
    private const string Manifest = """
        <?xml version="1.0" encoding="utf-8"?>
        <Package xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10">
          <Identity Name="393193CD-4A5B-4502-BC94-7C6AF142CD28" Publisher="CN=Michael Hacker" Version="1.9.0.0" ProcessorArchitecture="x64" />
        </Package>
        """;

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "wslcd-msix-tests-" + Guid.NewGuid().ToString("N"));

    public MsixPackageInspectorTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Best effort cleanup of the per-test temp folder.
        }
    }

    [Fact]
    public void ReadsTheIdentityFromAPackage()
    {
        var path = WritePackage(("AppxManifest.xml", Encoding.UTF8.GetBytes(Manifest)));

        var identity = MsixPackageInspector.ReadIdentity(path);

        Assert.Equal("393193CD-4A5B-4502-BC94-7C6AF142CD28", identity.Name);
        Assert.Equal("CN=Michael Hacker", identity.Publisher);
        Assert.Equal(new Version(1, 9, 0, 0), identity.Version);
        Assert.Equal("x64", identity.Architecture);
    }

    [Fact]
    public void DefaultsToNeutralArchitecture()
    {
        var manifest = Manifest.Replace(" ProcessorArchitecture=\"x64\"", string.Empty, StringComparison.Ordinal);
        Assert.Equal("neutral", MsixPackageInspector.ParseIdentity(new MemoryStream(Encoding.UTF8.GetBytes(manifest))).Architecture);
    }

    [Theory]
    [InlineData("<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\" />")]
    [InlineData("<Package><Identity Name=\"a\" Publisher=\"CN=b\" Version=\"1.0.0.0\" /></Package>")]
    [InlineData("<Package xmlns=\"http://schemas.microsoft.com/appx/manifest/foundation/windows10\"><Identity Name=\"a\" Publisher=\"CN=b\" Version=\"one\" /></Package>")]
    [InlineData("<?xml version=\"1.0\"?><!DOCTYPE p [<!ENTITY e \"x\">]><Package />")]
    [InlineData("not xml")]
    public void RejectsManifestsWithoutACompleteIdentity(string manifest) =>
        Assert.Throws<InvalidDataException>(() => MsixPackageInspector.ParseIdentity(new MemoryStream(Encoding.UTF8.GetBytes(manifest))));

    [Fact]
    public void RejectsAPackageWithoutAManifest()
    {
        var path = WritePackage(("other.txt", [1, 2, 3]));
        Assert.Throws<InvalidDataException>(() => MsixPackageInspector.ReadIdentity(path));
    }

    [Fact]
    public void ReadsThePackageSignatureWithoutItsMagic()
    {
        var path = WritePackage(("AppxManifest.xml", Encoding.UTF8.GetBytes(Manifest)), ("AppxSignature.p7x", [.. "PKCX"u8, 0x30, 0x01]));
        Assert.Equal(new byte[] { 0x30, 0x01 }, MsixPackageInspector.ReadPackageSignature(path));
    }

    [Fact]
    public void TreatsAMissingOrMalformedSignatureAsUnsigned()
    {
        Assert.Null(MsixPackageInspector.ReadPackageSignature(WritePackage(("AppxManifest.xml", Encoding.UTF8.GetBytes(Manifest)))));
        Assert.Null(MsixPackageInspector.ReadPackageSignature(WritePackage(("AppxSignature.p7x", [0x30, 0x01]))));
        Assert.Null(MsixPackageInspector.ReadInstalledSignature(_folder));
    }

    [Fact]
    public void ReadsAnInstalledSignature()
    {
        File.WriteAllBytes(Path.Combine(_folder, "AppxSignature.p7x"), [.. "PKCX"u8, 0x30]);
        Assert.Equal(new byte[] { 0x30 }, MsixPackageInspector.ReadInstalledSignature(_folder));
    }

    [Fact]
    public void ReturnsTheVerifiedSignerOfASignedMessage()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var cert = CreateSigningCertificate();
        var signed = Pkcs7Signer.Sign(cert, "package hashes"u8.ToArray());

        using var signer = MsixPackageInspector.GetVerifiedSigner(signed);

        Assert.NotNull(signer);
        Assert.Equal(cert.GetCertHashString(HashAlgorithmName.SHA256), MsixPackageInspector.Thumbprint(signer));
    }

    [Fact]
    public void RejectsASignedMessageWhoseContentWasAltered()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var cert = CreateSigningCertificate();
        var content = Encoding.ASCII.GetBytes("package hashes that must not change");
        var signed = Pkcs7Signer.Sign(cert, content);

        // Flip a byte of the embedded content (stored verbatim inside the SignedData).
        var at = signed.AsSpan().IndexOf(content);
        Assert.True(at > 0);
        signed[at] ^= 0x01;

        Assert.Null(MsixPackageInspector.GetVerifiedSigner(signed));
    }

    [Fact]
    public void RejectsCertificatesOnlyAndGarbageData()
    {
        using var cert = CreateSigningCertificate();
        var certsOnly = new X509Certificate2Collection(cert).Export(X509ContentType.Pkcs7)!;

        Assert.Null(MsixPackageInspector.GetVerifiedSigner(certsOnly));
        Assert.Null(MsixPackageInspector.GetVerifiedSigner([0x30, 0x03, 0x02, 0x01, 0x00]));
        Assert.Null(MsixPackageInspector.GetVerifiedSigner([]));
    }

    private string WritePackage(params (string Name, byte[] Data)[] entries)
    {
        var path = Path.Combine(_folder, Guid.NewGuid().ToString("N") + ".msix");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(data);
        }

        return path;
    }

    private static X509Certificate2 CreateSigningCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=Michael Hacker", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    }

    /// <summary>Produces an attached PKCS #7 SignedData with CryptoAPI, as signtool does for a package.</summary>
    private static class Pkcs7Signer
    {
        private const uint X509_ASN_ENCODING = 0x00000001;
        private const uint PKCS_7_ASN_ENCODING = 0x00010000;
        private const string Sha256Oid = "2.16.840.1.101.3.4.2.1";

        public static byte[] Sign(X509Certificate2 cert, byte[] content)
        {
            var certHandle = cert.Handle;
            var certArray = Marshal.AllocHGlobal(IntPtr.Size);
            var oid = Marshal.StringToHGlobalAnsi(Sha256Oid);
            var contentHandle = GCHandle.Alloc(content, GCHandleType.Pinned);
            try
            {
                Marshal.WriteIntPtr(certArray, certHandle);
                var para = new CryptSignMessagePara
                {
                    cbSize = (uint)Marshal.SizeOf<CryptSignMessagePara>(),
                    dwMsgEncodingType = X509_ASN_ENCODING | PKCS_7_ASN_ENCODING,
                    pSigningCert = certHandle,
                    HashAlgorithm = new CryptAlgorithmIdentifier { pszObjId = oid },
                    cMsgCert = 1,
                    rgpMsgCert = certArray,
                };

                var contents = new[] { contentHandle.AddrOfPinnedObject() };
                var lengths = new[] { (uint)content.Length };
                uint size = 0;
                if (!CryptSignMessage(ref para, false, 1, contents, lengths, null, ref size))
                {
                    throw new CryptographicException(Marshal.GetLastPInvokeError());
                }

                var signed = new byte[size];
                if (!CryptSignMessage(ref para, false, 1, contents, lengths, signed, ref size))
                {
                    throw new CryptographicException(Marshal.GetLastPInvokeError());
                }

                return signed[..(int)size];
            }
            finally
            {
                contentHandle.Free();
                Marshal.FreeHGlobal(oid);
                Marshal.FreeHGlobal(certArray);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CryptAlgorithmIdentifier
        {
            public IntPtr pszObjId;
            public uint cbParameters;
            public IntPtr pbParameters;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CryptSignMessagePara
        {
            public uint cbSize;
            public uint dwMsgEncodingType;
            public IntPtr pSigningCert;
            public CryptAlgorithmIdentifier HashAlgorithm;
            public IntPtr pvHashAuxInfo;
            public uint cMsgCert;
            public IntPtr rgpMsgCert;
            public uint cMsgCrl;
            public IntPtr rgpMsgCrl;
            public uint cAuthAttr;
            public IntPtr rgAuthAttr;
            public uint cUnauthAttr;
            public IntPtr rgUnauthAttr;
            public uint dwFlags;
            public uint dwInnerContentType;
        }

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CryptSignMessage(
            ref CryptSignMessagePara pSignPara,
            [MarshalAs(UnmanagedType.Bool)] bool fDetachedSignature,
            uint cToBeSigned,
            IntPtr[] rgpbToBeSigned,
            uint[] rgcbToBeSigned,
            byte[]? pbSignedBlob,
            ref uint pcbSignedBlob);
    }
}
