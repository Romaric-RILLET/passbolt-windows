<#
.SYNOPSIS
    Build a signed sideload package of the Windows Hello fork, ready to be shared.

.DESCRIPTION
    - Creates (once) a self-signed code signing certificate in Cert:\CurrentUser\My.
    - Temporarily applies the fork identity to Package.appxmanifest (restored afterwards) so the
      application coexists with the official Passbolt application from the Microsoft Store.
    - Builds the Release x64 msix signed with the certificate.
    - Gathers the msix, its dependencies, the public certificate and the install scripts in a distribution folder.

.EXAMPLE
    .\tools\package\Build-Package.ps1
#>
[CmdletBinding()]
param(
    [string]$IdentityName = "RomaricRillet.PassboltWindowsHello",
    [string]$Publisher = "CN=Romaric RILLET",
    [string]$PublisherDisplayName = "Romaric RILLET",
    [string]$DisplayName = "Passbolt (With Windows Hello By Romaric)",
    [string]$Platform = "x64",
    [string]$TargetPlatformVersion = "10.0.22621.0"
)

$ErrorActionPreference = "Stop"
$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..\..")
$projectDir = Join-Path $repoRoot "passbolt"
$manifestPath = Join-Path $projectDir "Package.appxmanifest"
$appPackagesDir = Join-Path $projectDir "AppPackages"
$distributionRoot = Join-Path $appPackagesDir "Distribution"

# --- Signing certificate --------------------------------------------------------------------------
$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $Publisher -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddDays(30) } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $certificate) {
    Write-Host "Creating the self-signed certificate '$Publisher'..."
    $certificate = New-SelfSignedCertificate -Type Custom -Subject $Publisher `
        -KeyUsage DigitalSignature -FriendlyName "Passbolt Windows Hello package signing" `
        -CertStoreLocation "Cert:\CurrentUser\My" -NotAfter (Get-Date).AddYears(3) `
        -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
}
Write-Host "Signing certificate: $($certificate.Thumbprint) (expires $($certificate.NotAfter.ToString('yyyy-MM-dd')))"

# --- Temporary manifest identity ------------------------------------------------------------------
$originalManifest = [System.IO.File]::ReadAllBytes($manifestPath)
try {
    [xml]$manifest = [System.Text.Encoding]::UTF8.GetString($originalManifest).TrimStart([char]0xFEFF)
    $ns = New-Object System.Xml.XmlNamespaceManager($manifest.NameTable)
    $ns.AddNamespace("m", "http://schemas.microsoft.com/appx/manifest/foundation/windows10")
    $ns.AddNamespace("uap", "http://schemas.microsoft.com/appx/manifest/uap/windows10")

    $identity = $manifest.SelectSingleNode("/m:Package/m:Identity", $ns)
    $identity.SetAttribute("Name", $IdentityName)
    $identity.SetAttribute("Publisher", $Publisher)
    $version = $identity.GetAttribute("Version")
    $manifest.SelectSingleNode("/m:Package/m:Properties/m:DisplayName", $ns).InnerText = $DisplayName
    $manifest.SelectSingleNode("/m:Package/m:Properties/m:PublisherDisplayName", $ns).InnerText = $PublisherDisplayName
    foreach ($visualElements in $manifest.SelectNodes("//uap:VisualElements", $ns)) {
        $visualElements.SetAttribute("DisplayName", $DisplayName)
    }
    $manifest.Save($manifestPath)

    # --- Build ------------------------------------------------------------------------------------
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    $msbuild = & $vswhere -latest -find "MSBuild\**\Bin\MSBuild.exe" | Select-Object -First 1
    $packageDir = Join-Path $appPackagesDir "Release_$Platform\"
    if (Test-Path $packageDir) { Remove-Item $packageDir -Recurse -Force }

    & $msbuild (Join-Path $projectDir "passbolt-windows.csproj") /restore /m /v:minimal /nologo `
        /p:Configuration=Release /p:Platform=$Platform `
        /p:TargetPlatformVersion=$TargetPlatformVersion `
        /p:AppxBundle=Never /p:UapAppxPackageBuildMode=SideloadOnly `
        /p:AppxPackageSigningEnabled=true /p:PackageCertificateThumbprint=$($certificate.Thumbprint) `
        /p:GenerateTemporaryStoreCertificate=false `
        /p:AppxPackageDir=$packageDir
    if ($LASTEXITCODE -ne 0) { throw "The build failed (exit code $LASTEXITCODE)." }
}
finally {
    [System.IO.File]::WriteAllBytes($manifestPath, $originalManifest)
}

# --- Distribution folder --------------------------------------------------------------------------
$msix = Get-ChildItem $packageDir -Recurse -Filter "*.msix" | Where-Object { $_.Directory.Name -notlike "Dependencies" -and $_.FullName -notmatch "\\Dependencies\\" } | Select-Object -First 1
if (-not $msix) { throw "No msix found in $packageDir" }

$distributionDir = Join-Path $distributionRoot "PassboltWindowsHello_$($version)_$Platform"
if (Test-Path $distributionDir) { Remove-Item $distributionDir -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $distributionDir "Dependencies") | Out-Null

Copy-Item $msix.FullName (Join-Path $distributionDir "PassboltWindowsHello.msix")
$dependencies = Join-Path $msix.Directory.FullName "Dependencies\$Platform"
if (Test-Path $dependencies) {
    Copy-Item (Join-Path $dependencies "*") (Join-Path $distributionDir "Dependencies")
}
Export-Certificate -Cert $certificate -FilePath (Join-Path $distributionDir "PassboltWindowsHello.cer") | Out-Null
Copy-Item (Join-Path $PSScriptRoot "Install.ps1"), (Join-Path $PSScriptRoot "Install.cmd"), (Join-Path $PSScriptRoot "LISEZMOI.txt") $distributionDir

$zip = "$distributionDir.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $distributionDir "*") -DestinationPath $zip

Write-Host ""
Write-Host "Package ready: $zip"
