# CorpVPN (.NET 8 / xray-core)

Windows client with simplified UI and VLESS subscription pool.

## Editable subscriptions source

Edit this file manually:
- `subscriptions/subscriptions.txt`

Format:
- one subscription URL per line
- `#` for comments

At build/publish time this file is encrypted (AES-GCM) and embedded into the final binary as an internal resource.

## Build

```powershell
powershell -ExecutionPolicy Bypass -File .\build\Publish.ps1
```

Output:
- `artifacts/publish/CorpVPN.Client.exe` (single-file, self-contained)

## Installer (Inno Setup)

```powershell
& "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" .\installer\CorpVPN.iss
```

Output:
- `artifacts/installer/CorpVPN-Setup.exe`

## Code signing (minimum)

Sign EXE:
```powershell
powershell -ExecutionPolicy Bypass -File .\build\Sign.ps1 -File .\artifacts\publish\CorpVPN.Client.exe -PfxPath C:\certs\code.pfx -PfxPassword "your-password"
```

Sign installer:
```powershell
powershell -ExecutionPolicy Bypass -File .\build\Sign.ps1 -File .\artifacts\installer\CorpVPN-Setup.exe -PfxPath C:\certs\code.pfx -PfxPassword "your-password"
```

## GitHub Actions CI/CD

Workflow file:
- `.github/workflows/release.yml`

Triggers:
- push tag `v*` (creates draft GitHub Release)
- manual run (`workflow_dispatch`)

Repository secrets for signing (optional):
- `SIGN_PFX_BASE64`
- `SIGN_PFX_PASSWORD`
- `TIMESTAMP_URL` (optional; defaults to DigiCert timestamp URL)

If signing secrets are not set:
- build and installer jobs still run
- release uses unsigned installer artifact

## Runtime prerequisites

- Put xray core binary here: `artifacts/publish/core/xray.exe` for portable tests.
- For installer flow, include `core/xray.exe` into publish directory before running ISCC.

## Current functional scope

- 250x500 borderless dark window
- Minimize to tray (`_`) and close (`X`)
- Power button red/green
- TUN toggle with restart apply
- Status + current Kbps
- Autostart task after first successful connect
- Graceful close: disable system proxy, stop xray/TUN, then exit
- Subscription pool load (from encrypted embedded resource)
- leastDelay selection + fallback for TCP/TLS nodes

## Security note

Encryption key defaults in project file for bootstrap. Change `SubscriptionsKey` in `src/CorpVPN.Client/CorpVPN.Client.csproj` before release and keep it private.
