<#
.SYNOPSIS
    Install Passbolt (With Windows Hello By Romaric).

.DESCRIPTION
    Trusts the package signing certificate (Local Machine > Trusted People, requires administrator rights)
    then installs the application and its dependencies for the current user.
#>
$ErrorActionPreference = "Stop"
$here = $PSScriptRoot
$certificatePath = Join-Path $here "PassboltWindowsHello.cer"
$packagePath = Join-Path $here "PassboltWindowsHello.msix"

$certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certificatePath)
$trusted = Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object { $_.Thumbprint -eq $certificate.Thumbprint }

if (-not $trusted) {
    $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $isAdmin) {
        Write-Host "Approbation du certificat : une fenêtre administrateur va s'ouvrir..."
        $process = Start-Process powershell -Verb RunAs -Wait -PassThru -ArgumentList @(
            "-NoProfile", "-ExecutionPolicy", "Bypass", "-Command",
            "Import-Certificate -FilePath '$certificatePath' -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null"
        )
        if ($process.ExitCode -ne 0) { throw "Le certificat n'a pas pu être approuvé." }
    }
    else {
        Import-Certificate -FilePath $certificatePath -CertStoreLocation Cert:\LocalMachine\TrustedPeople | Out-Null
    }
    Write-Host "Certificat approuvé."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem

# Read the identity of a package from its manifest
function Get-PackageIdentity([string]$path) {
    $zip = [System.IO.Compression.ZipFile]::OpenRead($path)
    try {
        $reader = New-Object System.IO.StreamReader($zip.GetEntry("AppxManifest.xml").Open())
        [xml]$manifest = $reader.ReadToEnd()
        $reader.Close()
        return $manifest.Package.Identity
    }
    finally {
        $zip.Dispose()
    }
}

# Only the missing or outdated framework dependencies are installed: reinstalling a framework already
# used by running applications fails (0x80073D02).
$dependencies = @()
foreach ($file in Get-ChildItem (Join-Path $here "Dependencies") -Include "*.appx", "*.msix" -Recurse -ErrorAction SilentlyContinue) {
    $identity = Get-PackageIdentity $file.FullName
    $installed = Get-AppxPackage -Name $identity.Name |
        Where-Object { $_.Architecture.ToString() -eq $identity.ProcessorArchitecture -and [version]$_.Version -ge [version]$identity.Version }
    if (-not $installed) {
        $dependencies += $file.FullName
    }
}

Write-Host "Installation de l'application..."
if ($dependencies.Count -gt 0) {
    Add-AppxPackage -Path $packagePath -DependencyPath $dependencies
}
else {
    Add-AppxPackage -Path $packagePath
}
Write-Host "Installation terminée. Lancez 'Passbolt (With Windows Hello By Romaric)' depuis le menu Démarrer."
