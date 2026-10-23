param([switch]$SkipBrowser, [switch]$BrowserOnly)
. "$PSScriptRoot\Common.ps1"
if ($SkipBrowser -and $BrowserOnly) { throw 'Use -SkipBrowser ou -BrowserOnly, não os dois.' }
$taskDotnet = Get-VistoraDotnet
& "$PSScriptRoot\Build.ps1"
if (-not $BrowserOnly) {
    & $taskDotnet run --project (Join-Path $taskProjectRoot 'tests\Vistora.Tests') -c Release --no-build
    Assert-VistoraExit
    & $taskDotnet run --project (Join-Path $taskProjectRoot 'tests\Vistora.DesktopTests') -c Release --no-build
    Assert-VistoraExit
}
if (-not $SkipBrowser) {
    & $taskDotnet run --project (Join-Path $taskProjectRoot 'tests\Vistora.BrowserTests') -c Release --no-build
    Assert-VistoraExit
}
