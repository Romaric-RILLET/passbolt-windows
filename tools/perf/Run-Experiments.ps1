<#
.SYNOPSIS
    Launch a test build several times with different WebView2 browser arguments and measure what its window displays.

.DESCRIPTION
    The test build reads LocalState\webview2-args.txt at startup (WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS).
    For each variant the app is launched cold through WindowCapture and the presented frames are recorded.
#>
param(
    [string]$PackageName = "RomaricRillet.PassboltWindowsHello.Upstream",
    [string]$WindowTitle = "Passbolt UPSTREAM test",
    [Parameter(Mandatory = $true)][string]$OutputRoot,
    [int]$Seconds = 25,
    [System.Collections.Specialized.OrderedDictionary]$Variants
)

$ErrorActionPreference = "Stop"
$package = Get-AppxPackage -Name $PackageName
$argsFile = Join-Path $env:LOCALAPPDATA "Packages\$($package.PackageFamilyName)\LocalState\webview2-args.txt"
$tool = Join-Path $PSScriptRoot "WindowCapture\bin\Release\net10.0-windows10.0.22621.0\WindowCapture.exe"

$results = foreach ($name in $Variants.Keys) {
    if ($Variants[$name]) {
        [System.IO.File]::WriteAllText($argsFile, $Variants[$name])
    }
    elseif (Test-Path $argsFile) {
        [System.IO.File]::Delete($argsFile)
    }
    $output = Join-Path $OutputRoot $name
    & $tool "$($package.PackageFamilyName)!App" $WindowTitle $output $Seconds | Out-Null
    Get-Process passbolt -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "*\$($package.PackageFamilyName.Split('_')[0])_*" } | Stop-Process -Force
    Start-Sleep -Seconds 2
    $timeline = Import-Csv (Join-Path $output "timeline.csv")
    [pscustomobject]@{
        variant      = $name
        frames       = $timeline.Count
        first_late   = ($timeline | Where-Object { [int]$_.ms_since_launch -gt 1500 } | Select-Object -First 1).ms_since_launch
        last         = ($timeline | Select-Object -Last 1).ms_since_launch
        all          = ($timeline | ForEach-Object ms_since_launch) -join ' '
    }
}
if (Test-Path $argsFile) { [System.IO.File]::Delete($argsFile) }
$results
