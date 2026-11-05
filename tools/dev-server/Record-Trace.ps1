<#
.SYNOPSIS
    Start or stop a Windows Performance Recorder trace (CPU, GPU, desktop composition, Edge/WebView2).
    Self-elevates (UAC prompt).

.EXAMPLE
    .\Record-Trace.ps1 -Action Start
    # ... reproduce the issue ...
    .\Record-Trace.ps1 -Action Stop -OutputPath C:\temp\hello.etl
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet("Start", "Stop")][string]$Action,
    [string]$OutputPath
)

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    $arguments = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", "`"$PSCommandPath`"", "-Action", $Action)
    if ($OutputPath) { $arguments += @("-OutputPath", "`"$OutputPath`"") }
    $process = Start-Process (Get-Process -Id $PID).Path -Verb RunAs -Wait -PassThru -WindowStyle Hidden -ArgumentList $arguments
    if ($process.ExitCode -eq 0) { Write-Host "$Action OK" } else { Write-Host "$Action failed ($($process.ExitCode))" }
    exit $process.ExitCode
}

if ($Action -eq "Start") {
    wpr.exe -cancel 2>$null | Out-Null
    wpr.exe -start GeneralProfile -start GPU -start DesktopComposition -start EdgeBrowser -filemode
}
else {
    if (-not $OutputPath) { throw "-OutputPath is required to stop" }
    wpr.exe -stop "$OutputPath"
}
exit $LASTEXITCODE
