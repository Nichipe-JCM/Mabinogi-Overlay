[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateScript({ Test-Path -LiteralPath $_ -PathType Leaf })]
    [string]$FilePath,

    [string]$CertificateThumbprint = $env:MABINOGI_OVERLAY_CERT_THUMBPRINT,

    [string]$TimestampServer = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
$codeSigningOid = "1.3.6.1.5.5.7.3.3"
$resolvedFile = (Resolve-Path -LiteralPath $FilePath).Path

$certificates = Get-ChildItem Cert:\CurrentUser\My | Where-Object {
    $_.HasPrivateKey -and
    $_.NotBefore -le (Get-Date) -and
    $_.NotAfter -gt (Get-Date) -and
    ($_.EnhancedKeyUsageList.ObjectId -contains $codeSigningOid)
}

if ([string]::IsNullOrWhiteSpace($CertificateThumbprint)) {
    $certificate = $certificates |
        Where-Object { $_.Subject -eq "CN=Mabinogi Overlay" } |
        Sort-Object NotAfter -Descending |
        Select-Object -First 1
} else {
    $normalizedThumbprint = $CertificateThumbprint.Replace(" ", "").ToUpperInvariant()
    $certificate = $certificates |
        Where-Object { $_.Thumbprint -eq $normalizedThumbprint } |
        Select-Object -First 1
}

if ($null -eq $certificate) {
    throw "No usable Mabinogi Overlay code-signing certificate was found in Cert:\CurrentUser\My."
}

$signature = Set-AuthenticodeSignature `
    -LiteralPath $resolvedFile `
    -Certificate $certificate `
    -HashAlgorithm SHA256 `
    -TimestampServer $TimestampServer

if ($null -eq $signature.SignerCertificate -or
    $signature.SignerCertificate.Thumbprint -ne $certificate.Thumbprint) {
    throw "The Authenticode signer does not match the selected certificate."
}

if ($null -eq $signature.TimeStamperCertificate) {
    throw "The signature was added without a timestamp. Do not publish this file."
}

if ($signature.Status -in @("NotSigned", "HashMismatch", "NotSupported")) {
    throw "Authenticode signing failed: $($signature.StatusMessage)"
}

[pscustomobject]@{
    File = $resolvedFile
    Status = $signature.Status
    StatusMessage = $signature.StatusMessage
    Signer = $signature.SignerCertificate.Subject
    Thumbprint = $signature.SignerCertificate.Thumbprint
    CertificateExpires = $signature.SignerCertificate.NotAfter
    TimeStamper = $signature.TimeStamperCertificate.Subject
    Sha256 = (Get-FileHash -LiteralPath $resolvedFile -Algorithm SHA256).Hash
}
