<#
.SYNOPSIS
    One-time host setup for the local Passbolt server (requires administrator rights, self-elevates).

.DESCRIPTION
    - Generates the TLS certificate of passbolt.test in .\certs (if missing).
    - Trusts it (Local Machine > Trusted Root Certification Authorities) for the desktop application and the browser.
    - Maps passbolt.test to 127.0.0.1 in the hosts file.
    - Exempts the installed passbolt desktop packages from the UWP loopback isolation.
    Run it again after installing a new package identity to exempt it too.
#>
param([switch]$Elevated)

$ErrorActionPreference = "Stop"
$hostname = "passbolt.test"
$certsDir = Join-Path $PSScriptRoot "certs"
$certificatePath = Join-Path $certsDir "certificate.crt"
$keyPath = Join-Path $certsDir "certificate.key"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $shell = (Get-Process -Id $PID).Path
    $process = Start-Process $shell -Verb RunAs -Wait -PassThru -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-Elevated")
    exit $process.ExitCode
}

try {
    # --- TLS certificate (PowerShell 7+: PEM export) ------------------------------------------
    if (-not (Test-Path $certificatePath) -or -not (Test-Path $keyPath)) {
        New-Item -ItemType Directory -Force $certsDir | Out-Null
        $rsa = [System.Security.Cryptography.RSA]::Create(2048)
        $request = [System.Security.Cryptography.X509Certificates.CertificateRequest]::new(
            "CN=$hostname", $rsa, [System.Security.Cryptography.HashAlgorithmName]::SHA256,
            [System.Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $san = [System.Security.Cryptography.X509Certificates.SubjectAlternativeNameBuilder]::new()
        $san.AddDnsName($hostname)
        $request.CertificateExtensions.Add($san.Build())
        $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509BasicConstraintsExtension]::new($false, $false, 0, $true))
        $usages = [System.Security.Cryptography.OidCollection]::new()
        [void]$usages.Add([System.Security.Cryptography.Oid]::new("1.3.6.1.5.5.7.3.1"))
        $request.CertificateExtensions.Add([System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::new($usages, $false))
        $certificate = $request.CreateSelfSigned([DateTimeOffset]::Now.AddDays(-1), [DateTimeOffset]::Now.AddYears(2))
        Set-Content -Path $certificatePath -Value $certificate.ExportCertificatePem() -NoNewline
        Set-Content -Path $keyPath -Value $rsa.ExportPkcs8PrivateKeyPem() -NoNewline
        Write-Host "Certificate generated in $certsDir"
    }

    # --- Trust the certificate ---------------------------------------------------------------
    $certificate = [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($certificatePath)
    if (-not (Get-ChildItem Cert:\LocalMachine\Root | Where-Object Thumbprint -eq $certificate.Thumbprint)) {
        Import-Certificate -FilePath $certificatePath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
        Write-Host "Certificate trusted ($($certificate.Thumbprint))"
    }

    # --- Hosts file --------------------------------------------------------------------------
    $hostsPath = Join-Path $env:SystemRoot "System32\drivers\etc\hosts"
    if (-not (Select-String -Path $hostsPath -Pattern "^\s*127\.0\.0\.1\s+$([regex]::Escape($hostname))\s*$" -Quiet)) {
        Add-Content -Path $hostsPath -Value "`r`n127.0.0.1 $hostname"
        Write-Host "hosts: $hostname -> 127.0.0.1"
    }

    # --- UWP loopback exemption --------------------------------------------------------------
    foreach ($package in Get-AppxPackage | Where-Object { $_.Name -eq "Passbolt.Passbolt" -or $_.Name -like "RomaricRillet.PassboltWindowsHello*" }) {
        CheckNetIsolation.exe LoopbackExempt -a "-n=$($package.PackageFamilyName)" | Out-Null
        Write-Host "Loopback exemption: $($package.PackageFamilyName)"
    }
    Write-Host "Done."
}
catch {
    Write-Error $_
    if ($Elevated) { Read-Host "Press Enter to close" }
    exit 1
}
if ($Elevated) { Start-Sleep -Seconds 3 }
