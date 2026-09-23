# In-app update end-to-end test

Proves the whole update path on a real machine: find release → download → verify → Windows closes
the app → install → Windows relaunches it → "Update installed". Unit tests cannot cover the last
four steps. Run this after changing anything in the updater, and ideally before a release that
changes packaging.

The test uses a **side-by-side test copy** (`WslContainerDesktop.UpdateTest`, shown as
*WSL Container Desktop (update test)*) so your real installation is never touched, a throwaway
certificate, and a scratch public GitHub repository for the fake releases. Everything except the
package name, update repository and certificate is the shipping code path.

## 1. One-time setup

```powershell
# Throwaway signing certificate (current user; no admin needed). Subject must match the manifest Publisher.
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=Michael Hacker" `
  -KeyUsage DigitalSignature -FriendlyName "WSLCD UPDATE TEST - delete after testing" `
  -CertStoreLocation Cert:\CurrentUser\My `
  -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3","2.5.29.19={text}") -NotAfter (Get-Date).AddDays(14)
$e2e = "$env:TEMP\wslcd-update-e2e"; New-Item -ItemType Directory -Force $e2e | Out-Null
Export-Certificate -Cert $cert -FilePath "$e2e\update-test.cer" | Out-Null

# Trust it so Windows will install packages it signs (approve the UAC prompt).
Start-Process cmd -Verb RunAs -ArgumentList "/k certutil -addstore TrustedPeople $e2e\update-test.cer"

# Scratch repository for the fake releases (must be public: the app reads releases anonymously).
gh repo create <owner>/wslcd-update-test --public --add-readme
```

## 2. Build, install and publish

```powershell
$repo = '<owner>/wslcd-update-test'
./tools/update-e2e/Build-UpdateTestPackage.ps1 -Version 1.9.0 -CertThumbprint $cert.Thumbprint -Repository $repo -OutDir "$e2e\out"
./tools/update-e2e/Build-UpdateTestPackage.ps1 -Version 1.9.1 -CertThumbprint $cert.Thumbprint -Repository $repo -OutDir "$e2e\out"

Add-AppxPackage "$e2e\out\WSLContainerDesktop_1.9.0_x64.msix"

# Keep the test copy from restarting containers on launch.
$settings = "$env:LOCALAPPDATA\Packages\WslContainerDesktop.UpdateTest_k3nh39tchq6nr\LocalCache\Local\WslContainerDesktop"
New-Item -ItemType Directory -Force $settings | Out-Null
'{ "RestartRunningContainersOnLaunch": false, "CheckForUpdatesOnLaunch": true }' | Set-Content "$settings\settings.json"

gh release create v1.9.1 "$e2e\out\WSLContainerDesktop_1.9.1_x64.msix" --repo $repo --title "Update test 1.9.1" --notes "Test release."
```

(The package family suffix `k3nh39tchq6nr` is derived from the publisher; check it with
`Get-AppxPackage WslContainerDesktop.UpdateTest`.)

## 3. Run the scenarios

Launch **WSL Container Desktop (update test)** from Start. Its log is under
`%LOCALAPPDATA%\Packages\WslContainerDesktop.UpdateTest_<suffix>\LocalCache\WslContainerDesktop\logs`.

| Scenario | How | Expected |
|---|---|---|
| Update | Click **Update now** in the bar | App closes, reinstalls as the new version and reopens by itself (command line `--updated`); bar says **Update installed**; log says `Previous session installed update …: succeeded` |
| Early click | Restart the app and click **Update now** within a few seconds | Bar shows *Getting ready to install* until the app has run ~65 s, then as above |
| Wrong signer | Build the next version with a *second* throwaway certificate, publish it, restart the app, click **Update now** | Nothing installed; bar says **Update failed** with the different-certificate explanation |

Each scenario needs a newer published version (`1.9.2`, `1.9.3`, …); the newest non-prerelease
release is the one offered.

## 4. Clean up

```powershell
Get-AppxPackage WslContainerDesktop.UpdateTest | Remove-AppxPackage
Get-ChildItem Cert:\CurrentUser\My | Where-Object FriendlyName -like 'WSLCD UPDATE TEST*' | Remove-Item
Start-Process cmd -Verb RunAs -ArgumentList "/k certutil -delstore TrustedPeople <test cert thumbprint>"
gh repo delete <owner>/wslcd-update-test --yes   # or keep it for the next run
Remove-Item -Recurse -Force "$env:TEMP\wslcd-update-e2e"
```
