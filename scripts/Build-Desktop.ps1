param([switch]$IncludeLocalConfiguration, [string]$NativeBuildDirectory='build\Release')
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$dotnet='C:\Program Files\dotnet\dotnet.exe'
$runtime='8.0.32'
$package=Join-Path $root 'dist\NDI Machine'
if(!$IncludeLocalConfiguration -and (Get-ChildItem -LiteralPath (Join-Path $package 'config') -Filter 'local.*' -ErrorAction SilentlyContinue)) {
  throw 'Existing package contains private local configuration. Use a fresh checkout/output directory for a public package, or explicitly request -IncludeLocalConfiguration.'
}
Push-Location $root
try {
  & $dotnet build tests\Desktop.Tests\NDIMachine.Desktop.Tests.csproj -c Release -o build\desktop-tests --nologo
  if($LASTEXITCODE){throw 'Desktop/control test build failed'}
  & '.\build\desktop-tests\NDIMachine.Desktop.Tests.exe'
  if($LASTEXITCODE){throw 'Desktop/control tests failed'}
  $publish=@('-c','Release','-r','win-x64','--self-contained','true',('-p:RuntimeFrameworkVersion='+$runtime),'-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true','-p:EnableCompressionInSingleFile=true','-p:DebugType=None','-p:DebugSymbols=false','--nologo')
  & $dotnet publish desktop\NDIMachine.Desktop.csproj @publish ('-p:OutputPath='+$root+'\build\desktop-package\') -o $package
  if($LASTEXITCODE){throw 'Desktop publish failed'}
  & $dotnet publish control\NDIMachine.Control.csproj @publish ('-p:OutputPath='+$root+'\build\control-package\') -o (Join-Path $package 'engine')
  if($LASTEXITCODE){throw 'Control host publish failed'}
  $native=Join-Path $package 'build\Release'
  New-Item -ItemType Directory -Force -Path $native,(Join-Path $package 'config'),(Join-Path $package 'notices') | Out-Null
  foreach($name in @('audio-host.exe','media-worker.exe','ndi-probe.exe','decklink-sdk-check.exe')) {
    $source=Join-Path (Join-Path $root $NativeBuildDirectory) $name
    if(!(Test-Path -LiteralPath $source)){throw ('Build native prerequisites first: '+$source)}
    Copy-Item -LiteralPath $source -Destination $native
  }
  Copy-Item -LiteralPath 'config\commissioning.example.json' -Destination (Join-Path $package 'config')
  Copy-Item -LiteralPath 'LICENSE','docs\DESKTOP.md','docs\DEPENDENCIES-LICENSES.md' -Destination $package
  Copy-Item -LiteralPath 'build\_deps\json-src\LICENSE.MIT' -Destination (Join-Path $package 'notices\nlohmann-json-MIT.txt')
  # .NET runtime packs carry their redistribution notices. Vendor SDKs/runtimes
  # are deliberately not copied; the installed NDI and Desktop Video are used.
  $nugetRoot=Join-Path $env:USERPROFILE '.nuget\packages'
  foreach($pack in @('microsoft.netcore.app.runtime.win-x64','microsoft.windowsdesktop.app.runtime.win-x64','microsoft.aspnetcore.app.runtime.win-x64')) {
    $packRoot=Join-Path $nugetRoot ($pack+'\'+$runtime)
    foreach($notice in @('LICENSE.TXT','THIRD-PARTY-NOTICES.TXT')) {
      $source=Join-Path $packRoot $notice
      if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination (Join-Path $package ('notices\'+$pack+'-'+$notice))}
    }
  }
  if($IncludeLocalConfiguration) {
    Copy-Item -LiteralPath 'config\local.commissioning.json' -Destination (Join-Path $package 'config')
    foreach($name in @('local.supervisor.json','local.supervisor.json.lkg','local.discovery.json')) {
      $source=Join-Path $root ('config\'+$name)
      if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination (Join-Path $package 'config')}
    }
    Write-Output 'Package contains private commissioning. Keep this local package out of public Git/releases.'
  }
  Get-FileHash -LiteralPath (Join-Path $package 'NDI Machine.exe') -Algorithm SHA256 | Format-List
  Write-Output ('Ready: '+$package+'\NDI Machine.exe')
} finally {Pop-Location}
