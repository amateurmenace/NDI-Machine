param([switch]$NativeOnly)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$vs=& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath
if(!$vs){throw 'Install Visual Studio C++ desktop workload'}
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64'
cmake -S $root -B "$root\build" -G 'Visual Studio 18 2026' -A x64
if($LASTEXITCODE){throw 'CMake configure failed'}
cmake --build "$root\build" --config Release --parallel
if($LASTEXITCODE){throw 'Native build failed'}
ctest --test-dir "$root\build" -C Release --output-on-failure
if($LASTEXITCODE){throw 'Native tests failed'}
if(!$NativeOnly){
  Push-Location $root
  try {
    & 'C:\Program Files\dotnet\dotnet.exe' build "$root\control\NDIMachine.Control.csproj" -c Release --nologo
    if($LASTEXITCODE){throw 'Control host build failed'}
  } finally {Pop-Location}
}
Write-Output 'Native workers, USB audio host and tests built. Control host included unless -NativeOnly. Build Companion separately with Build-Companion.ps1. See release status for unfinished capabilities.'
