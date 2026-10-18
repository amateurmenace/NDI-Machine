param([string]$DeckLinkArchive=(Join-Path $env:USERPROFILE 'Downloads\Blackmagic_DeckLink_SDK_16.0.zip'))
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$destination=Join-Path $root '.deps'
$sdk=Join-Path $destination 'Blackmagic DeckLink SDK 16.0'
if(!(Test-Path -LiteralPath $sdk)){
  if(!(Test-Path -LiteralPath $DeckLinkArchive)){throw 'Download Desktop Video SDK 16.0 from https://www.blackmagicdesign.com/developer/products/capture-and-playback/sdk-and-software then pass -DeckLinkArchive with its ZIP path.'}
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $zip=[IO.Compression.ZipFile]::OpenRead($DeckLinkArchive)
  try {
    foreach($entry in $zip.Entries){if($entry.FullName -match '(^[/\\]|^[A-Za-z]:|(^|[/\\])\.\.([/\\]|$))'){throw 'Unsafe archive path'}}
    if(!($zip.Entries | Where-Object {$_.FullName -eq 'Blackmagic DeckLink SDK 16.0/Win/include/DeckLinkAPIVersion.h'})){throw 'Expected SDK 16.0 archive layout missing'}
  }finally{$zip.Dispose()}
  New-Item -ItemType Directory -Force -Path $destination | Out-Null
  Expand-Archive -LiteralPath $DeckLinkArchive -DestinationPath $destination
}
$version=Get-Content -LiteralPath "$sdk\Win\include\DeckLinkAPIVersion.h" -Raw
if($version -notmatch 'BLACKMAGIC_DECKLINK_API_VERSION_STRING\s+"16\.0"'){throw 'SDK version mismatch'}
foreach($path in @('C:\Program Files\NDI\NDI 6 SDK\Include\Processing.NDI.Lib.h','C:\Program Files\NDI\NDI 6 Runtime\v6\Processing.NDI.Lib.x64.dll')){
  if(!(Test-Path -LiteralPath $path)){throw "Missing prerequisite: $path. Obtain the standard SDK/runtime from https://ndi.video/for-developers/ndi-sdk/"}
}
Write-Output 'Local SDK prerequisites found. Driver/license readiness is separate; see docs/DEPENDENCIES-LICENSES.md.'
