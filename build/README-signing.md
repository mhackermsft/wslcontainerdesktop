# Release signing

The release workflow (`.github/workflows/release.yml`) builds a **self-signed, self-contained
MSIX** and publishes it as a GitHub Release. Signing uses two repository secrets:

| Secret | Contents |
|--------|----------|
| `PFX_BASE64` | Base64 of a PFX containing the code-signing certificate and its private key. The certificate subject **must** match the package manifest `Publisher` (`CN=Michael Hacker`). |
| `PFX_PASSWORD` | The password protecting that PFX. |

These are already set for this repository. The workflow decodes the PFX, builds the MSIX
unsigned, then signs it with `signtool` (from the `Microsoft.Windows.SDK.BuildTools` NuGet
package). It also exports the public `.cer` from the PFX and attaches it to the release so end
users can trust the publisher.

## Why the same certificate matters

Windows treats an MSIX update as the *same* app only when the new package has the **same
publisher identity** (and a higher version). Re-using one stored certificate across every
release means users trust the certificate once and can update in place. Generating a fresh
certificate per build would force users to re-trust and reinstall each time — which is why the
certificate lives in secrets rather than being created on the fly.

## Rotating / recreating the certificate

> [!WARNING]
> **Rotating the certificate ends automatic updates for every existing installation.** The in-app
> updater installs a release only if it is signed with the *same* certificate (compared by SHA-256
> thumbprint) as the copy already installed, and Windows only accepts a self-signed package whose
> exact certificate the user has trusted. A release signed with a new certificate therefore shows
> *"This release is signed with a different certificate…"* in the app, and every user has to
> download it and trust the new `.cer` by hand, as for a first install. Only rotate when you have
> to (compromise or imminent expiry — the current certificate is valid for ten years), and say so
> prominently in that release's notes and `CHANGELOG.md`.

Run locally (PowerShell), then update the two secrets:

```powershell
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=Michael Hacker" `
  -KeyUsage DigitalSignature -FriendlyName "WSL Container Desktop Signing" `
  -CertStoreLocation "Cert:\CurrentUser\My" `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3","2.5.29.19={text}") `
  -NotAfter (Get-Date).AddYears(10)

$pw = ConvertTo-SecureString "<a-strong-password>" -Force -AsPlainText
Export-PfxCertificate -Cert $cert -FilePath .\sign.pfx -Password $pw
Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"   # keep only the .pfx file

$b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes(".\sign.pfx"))
$b64 | gh secret set PFX_BASE64 --repo mhackermsft/wslcontainerdesktop
"<a-strong-password>" | gh secret set PFX_PASSWORD --repo mhackermsft/wslcontainerdesktop
```

> The `Publisher` in `src/WslContainerDesktop/Package.appxmanifest` must equal the certificate
> subject. If you change one, change the other.

## Upgrading to a real certificate

To remove the "unknown publisher / trust this certificate" step for users, replace the
self-signed certificate with one from a trusted CA (or an Azure Trusted Signing account) and
update the two secrets plus the manifest `Publisher` to match the new subject. No workflow
changes are required. A new `Publisher` makes it a different package to Windows, so — as with a
rotation — existing installations cannot update in place and users must install the new release
manually (and uninstall the old one).
