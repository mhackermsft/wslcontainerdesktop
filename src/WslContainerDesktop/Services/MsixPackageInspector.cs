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

using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Xml;
using System.Xml.Linq;
using WslContainerDesktop.Models;

namespace WslContainerDesktop.Services;

/// <summary>
/// Reads the identity and signer of an MSIX without installing it, so the updater can refuse a
/// package that is not a newer build of this app signed by the same certificate. Windows itself
/// still validates every file hash and the certificate trust when the package is installed; this
/// check exists so a package signed by anyone else — even a certificate the machine trusts — is
/// never handed to the installer.
/// </summary>
public static class MsixPackageInspector
{
    public const string ManifestEntry = "AppxManifest.xml";
    public const string SignatureEntry = "AppxSignature.p7x";

    private const string FoundationNamespace = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private const int MaxManifestBytes = 4 * 1024 * 1024;
    private const int MaxSignatureBytes = 1024 * 1024;

    // An AppxSignature.p7x is the ASCII magic "PKCX" followed by a DER PKCS #7 SignedData.
    private static ReadOnlySpan<byte> SignatureMagic => "PKCX"u8;

    /// <summary>Reads the package identity from an MSIX's manifest.</summary>
    /// <exception cref="InvalidDataException">The file is not an MSIX with a readable identity.</exception>
    public static MsixIdentity ReadIdentity(string msixPath)
    {
        using var zip = ZipFile.OpenRead(msixPath);
        var entry = zip.GetEntry(ManifestEntry)
            ?? throw new InvalidDataException("The package has no AppxManifest.xml.");
        if (entry.Length > MaxManifestBytes)
        {
            throw new InvalidDataException("The package manifest is unexpectedly large.");
        }

        using var stream = entry.Open();
        return ParseIdentity(stream);
    }

    /// <summary>Parses the <c>Identity</c> element of an <c>AppxManifest.xml</c>.</summary>
    /// <exception cref="InvalidDataException">The manifest has no complete identity.</exception>
    public static MsixIdentity ParseIdentity(Stream manifest)
    {
        XDocument doc;
        try
        {
            using var reader = XmlReader.Create(manifest, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            });
            doc = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException("The package manifest is not valid XML.", ex);
        }

        var identity = doc.Root?.Element(XName.Get("Identity", FoundationNamespace))
            ?? throw new InvalidDataException("The package manifest has no Identity.");

        var name = (string?)identity.Attribute("Name");
        var publisher = (string?)identity.Attribute("Publisher");
        var versionText = (string?)identity.Attribute("Version");
        var architecture = (string?)identity.Attribute("ProcessorArchitecture") ?? "neutral";

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(publisher)
            || !Version.TryParse(versionText, out var version))
        {
            throw new InvalidDataException("The package identity is incomplete.");
        }

        return new MsixIdentity(name, publisher, version, architecture);
    }

    /// <summary>Returns the PKCS #7 signature embedded in an MSIX, or null if it is unsigned.</summary>
    public static byte[]? ReadPackageSignature(string msixPath)
    {
        using var zip = ZipFile.OpenRead(msixPath);
        var entry = zip.GetEntry(SignatureEntry);
        if (entry is null || entry.Length > MaxSignatureBytes)
        {
            return null;
        }

        using var stream = entry.Open();
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return StripSignatureMagic(buffer.ToArray());
    }

    /// <summary>
    /// Returns the PKCS #7 signature of an installed package from its install folder, or null for a
    /// development registration (loose files are not signed).
    /// </summary>
    public static byte[]? ReadInstalledSignature(string installFolder)
    {
        var path = Path.Combine(installFolder, SignatureEntry);
        var info = new FileInfo(path);
        if (!info.Exists || info.Length > MaxSignatureBytes)
        {
            return null;
        }

        return StripSignatureMagic(File.ReadAllBytes(path));
    }

    /// <summary>Removes the <c>PKCX</c> prefix of a p7x file; null when the prefix is missing.</summary>
    public static byte[]? StripSignatureMagic(byte[] p7x) =>
        p7x.AsSpan().StartsWith(SignatureMagic) ? p7x[SignatureMagic.Length..] : null;

    /// <summary>
    /// Returns the certificate of the single signer of a PKCS #7 SignedData after checking that
    /// the signature over the signed content verifies with that certificate's key, or null when the
    /// data is not a validly signed message with exactly one signer. Chain trust is not evaluated
    /// here — the app's certificate is self-signed and Windows checks trust at install time.
    /// </summary>
    public static X509Certificate2? GetVerifiedSigner(byte[] pkcs7)
    {
        if (!OperatingSystem.IsWindows() || pkcs7.Length == 0)
        {
            return null;
        }

        var handle = GCHandle.Alloc(pkcs7, GCHandleType.Pinned);
        var blobPtr = IntPtr.Zero;
        var store = IntPtr.Zero;
        var message = IntPtr.Zero;
        var signerInfo = IntPtr.Zero;
        var context = IntPtr.Zero;
        try
        {
            var blob = new Crypt32.CryptDataBlob { cbData = (uint)pkcs7.Length, pbData = handle.AddrOfPinnedObject() };
            blobPtr = Marshal.AllocHGlobal(Marshal.SizeOf<Crypt32.CryptDataBlob>());
            Marshal.StructureToPtr(blob, blobPtr, false);

            if (!Crypt32.CryptQueryObject(
                    Crypt32.CERT_QUERY_OBJECT_BLOB,
                    blobPtr,
                    Crypt32.CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED,
                    Crypt32.CERT_QUERY_FORMAT_FLAG_BINARY,
                    0,
                    out _,
                    out _,
                    out _,
                    out store,
                    out message,
                    IntPtr.Zero))
            {
                return null;
            }

            var countSize = (uint)sizeof(uint);
            var countBuffer = new byte[countSize];
            if (!Crypt32.CryptMsgGetParam(message, Crypt32.CMSG_SIGNER_COUNT_PARAM, 0, countBuffer, ref countSize)
                || BitConverter.ToUInt32(countBuffer, 0) != 1)
            {
                return null;
            }

            uint infoSize = 0;
            if (!Crypt32.CryptMsgGetParam(message, Crypt32.CMSG_SIGNER_CERT_INFO_PARAM, 0, IntPtr.Zero, ref infoSize) || infoSize == 0)
            {
                return null;
            }

            signerInfo = Marshal.AllocHGlobal((int)infoSize);
            if (!Crypt32.CryptMsgGetParam(message, Crypt32.CMSG_SIGNER_CERT_INFO_PARAM, 0, signerInfo, ref infoSize))
            {
                return null;
            }

            context = Crypt32.CertFindCertificateInStore(
                store,
                Crypt32.X509_ASN_ENCODING | Crypt32.PKCS_7_ASN_ENCODING,
                0,
                Crypt32.CERT_FIND_SUBJECT_CERT,
                signerInfo,
                IntPtr.Zero);
            if (context == IntPtr.Zero)
            {
                return null;
            }

            var cert = Marshal.PtrToStructure<Crypt32.CertContext>(context);
            if (!Crypt32.CryptMsgControl(message, 0, Crypt32.CMSG_CTRL_VERIFY_SIGNATURE, cert.pCertInfo))
            {
                return null;
            }

            var encoded = new byte[cert.cbCertEncoded];
            Marshal.Copy(cert.pbCertEncoded, encoded, 0, encoded.Length);
            return X509CertificateLoader.LoadCertificate(encoded);
        }
        catch (CryptographicException)
        {
            return null;
        }
        finally
        {
            if (context != IntPtr.Zero)
            {
                Crypt32.CertFreeCertificateContext(context);
            }

            if (signerInfo != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(signerInfo);
            }

            if (message != IntPtr.Zero)
            {
                Crypt32.CryptMsgClose(message);
            }

            if (store != IntPtr.Zero)
            {
                Crypt32.CertCloseStore(store, 0);
            }

            if (blobPtr != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(blobPtr);
            }

            handle.Free();
        }
    }

    /// <summary>Upper-case hex SHA-256 thumbprint of a certificate.</summary>
    public static string Thumbprint(X509Certificate2 certificate) =>
        certificate.GetCertHashString(HashAlgorithmName.SHA256);

    private static class Crypt32
    {
        public const uint CERT_QUERY_OBJECT_BLOB = 2;
        public const uint CERT_QUERY_CONTENT_FLAG_PKCS7_SIGNED = 1 << 8;
        public const uint CERT_QUERY_FORMAT_FLAG_BINARY = 1 << 1;
        public const uint CMSG_SIGNER_COUNT_PARAM = 5;
        public const uint CMSG_SIGNER_CERT_INFO_PARAM = 7;
        public const uint CMSG_CTRL_VERIFY_SIGNATURE = 1;
        public const uint X509_ASN_ENCODING = 0x00000001;
        public const uint PKCS_7_ASN_ENCODING = 0x00010000;
        public const uint CERT_FIND_SUBJECT_CERT = 11 << 16;

        [StructLayout(LayoutKind.Sequential)]
        public struct CryptDataBlob
        {
            public uint cbData;
            public IntPtr pbData;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CertContext
        {
            public uint dwCertEncodingType;
            public IntPtr pbCertEncoded;
            public uint cbCertEncoded;
            public IntPtr pCertInfo;
            public IntPtr hCertStore;
        }

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CryptQueryObject(
            uint dwObjectType,
            IntPtr pvObject,
            uint dwExpectedContentTypeFlags,
            uint dwExpectedFormatTypeFlags,
            uint dwFlags,
            out uint pdwMsgAndCertEncodingType,
            out uint pdwContentType,
            out uint pdwFormatType,
            out IntPtr phCertStore,
            out IntPtr phMsg,
            IntPtr ppvContext);

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CryptMsgGetParam(IntPtr hCryptMsg, uint dwParamType, uint dwIndex, byte[] pvData, ref uint pcbData);

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CryptMsgGetParam(IntPtr hCryptMsg, uint dwParamType, uint dwIndex, IntPtr pvData, ref uint pcbData);

        [DllImport("crypt32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CryptMsgControl(IntPtr hCryptMsg, uint dwFlags, uint dwCtrlType, IntPtr pvCtrlPara);

        [DllImport("crypt32.dll", SetLastError = true)]
        public static extern IntPtr CertFindCertificateInStore(
            IntPtr hCertStore,
            uint dwCertEncodingType,
            uint dwFindFlags,
            uint dwFindType,
            IntPtr pvFindPara,
            IntPtr pPrevCertContext);

        [DllImport("crypt32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CertFreeCertificateContext(IntPtr pCertContext);

        [DllImport("crypt32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CryptMsgClose(IntPtr hCryptMsg);

        [DllImport("crypt32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CertCloseStore(IntPtr hCertStore, uint dwFlags);
    }
}
