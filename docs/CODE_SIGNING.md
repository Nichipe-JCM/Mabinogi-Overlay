# Code Signing

Mabinogi Overlay release executables are Authenticode-signed after `dotnet publish` and before the portable ZIP is created.

## Current temporary certificate

- Subject: `CN=Mabinogi Overlay`
- SHA-1 thumbprint: `5E742A65875200203E2C7279B1DDE1331B5B617B`
- Valid until: `2029-07-01`
- Key algorithm: RSA 3072
- File digest: SHA-256
- Timestamp service: DigiCert
- Trust model: self-signed, temporary

The public certificate is stored at `certificates/MabinogiOverlay-selfsigned.cer`. The private key remains in the maintainer's Windows `Cert:\CurrentUser\My` certificate store and must never be committed.

Because this certificate is self-signed, Windows can report an untrusted root even when the executable signature and timestamp are intact. The certificate is an interim measure until the project uses a publicly trusted code-signing service.

## Signing a release

Publish the executable first:

```powershell
dotnet publish src/TestOverlay.App/TestOverlay.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=true `
  -p:PublishReadyToRun=true `
  -p:PublishTrimmed=false `
  -o <publish-directory>
```

Sign the published executable:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\sign-release.ps1 `
  -FilePath "<publish-directory>\Mabinogi Overlay.exe"
```

To require a specific certificate, set its thumbprint for the process:

```powershell
$env:MABINOGI_OVERLAY_CERT_THUMBPRINT = "5E742A65875200203E2C7279B1DDE1331B5B617B"
```

Create the portable ZIP only after signing. Any modification to the executable after signing invalidates its signature.

## Verification

```powershell
Get-AuthenticodeSignature "<publish-directory>\Mabinogi Overlay.exe" |
  Select-Object Status, StatusMessage, SignerCertificate, TimeStamperCertificate
```

For the temporary self-signed certificate, `UnknownError` with an untrusted-root message is expected on a computer that does not trust the certificate. A release is acceptable only when the signer thumbprint matches the expected certificate and a timestamp certificate is present.

## Secret handling

- Never commit `.pfx`, `.p12`, `.pvk`, passwords, token PINs, or private-key exports.
- Do not place private material in the packaging directory.
- Store any future PFX backup outside the repository with a strong password.
- Replace the public `.cer`, documented thumbprint, and release secret when the signing identity changes.
