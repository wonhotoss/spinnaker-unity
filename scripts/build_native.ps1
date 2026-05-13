param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string]$SpinnakerSdkDir = "C:\Program Files\Teledyne\Spinnaker",

    [ValidateSet("vs2015", "vs2017")]
    [string]$SpinnakerLibFlavor = "vs2015"
)

$ErrorActionPreference = "Stop"

$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$solution = Join-Path $root "native\SpinnakerUnityBridge\SpinnakerUnityBridge.sln"
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"

if (-not (Test-Path $solution)) {
    throw "Solution not found: $solution"
}

if (-not (Test-Path $SpinnakerSdkDir)) {
    throw "Spinnaker SDK not found: $SpinnakerSdkDir"
}

if (-not (Test-Path $vswhere)) {
    throw "vswhere not found: $vswhere"
}

$msbuild = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find "MSBuild\**\Bin\amd64\MSBuild.exe" | Select-Object -First 1
if (-not $msbuild) {
    throw "MSBuild x64 not found. Install Visual Studio 2022 with C++ build tools."
}

& $msbuild $solution `
    /m `
    /p:Configuration=$Configuration `
    /p:Platform=x64 `
    /p:SpinnakerSdkDir="$($SpinnakerSdkDir.TrimEnd('\'))" `
    /p:SpinnakerLibFlavor=$SpinnakerLibFlavor

if ($LASTEXITCODE -ne 0) {
    throw "Native build failed with exit code $LASTEXITCODE"
}

$plugin = Join-Path $root "unity\Assets\Plugins\x86_64\SpinnakerUnityBridge.dll"
if (-not (Test-Path $plugin)) {
    throw "Build finished, but Unity plugin was not copied: $plugin"
}

Write-Host "Built and copied: $plugin"
