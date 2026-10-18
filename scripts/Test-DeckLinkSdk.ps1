param([string]$SdkRoot)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!$SdkRoot){$SdkRoot=Join-Path $root '.deps\Blackmagic DeckLink SDK 16.0'}
if(!(Test-Path -LiteralPath "$SdkRoot\Win\include\DeckLinkAPI.idl")){throw "DeckLink SDK not found: $SdkRoot"}
$vs=& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -property installationPath
Import-Module "$vs\Common7\Tools\Microsoft.VisualStudio.DevShell.dll"
Enter-VsDevShell -VsInstallPath $vs -SkipAutomaticLocation -DevCmdArguments '-arch=x64 -host_arch=x64'
$build=Join-Path $root 'build\sdk-check'
New-Item -ItemType Directory -Force -Path $build | Out-Null
Push-Location $build
try {
  & midl /nologo /env x64 /I "$SdkRoot\Win\include" /h DeckLinkAPI.h /iid DeckLinkAPI_i.c /out $build "$SdkRoot\Win\include\DeckLinkAPI.idl"
  if($LASTEXITCODE){throw 'SDK MIDL generation failed'}
  & cl /nologo /std:c++20 /EHsc /W4 /utf-8 /I $build /I "$SdkRoot\Win\include" "$root\native\sdk_check.cpp" DeckLinkAPI_i.c /Fe:decklink-sdk-check.exe /link ole32.lib oleaut32.lib
  if($LASTEXITCODE){throw 'SDK native compile/link failed'}
  $raw=& '.\decklink-sdk-check.exe'
  if($LASTEXITCODE){throw 'SDK runtime check failed'}
  $result=$raw | ConvertFrom-Json
  if($result.compiledSdk -ne '16.0'){throw 'Unexpected SDK version'}
  $raw | Set-Content -LiteralPath "$root\reports\decklink-sdk-inventory.json" -Encoding utf8
  $result | Select-Object compiledSdk,runtimeApi,deviceCount,readOnly,physicalOutputTest | Format-List
  $result.devices | Select-Object displayName,model,persistentId,deviceGroupId,subdeviceIndex,busyFlags,supports1080p2997Uyvy | Format-Table -AutoSize
} finally {Pop-Location}
