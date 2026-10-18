param(
  [Parameter(Mandatory=$true)][ValidateRange(1,4)][int]$Slot,
  [Parameter(Mandatory=$true)][ValidatePattern('^0x[0-9a-fA-F]+$')][string]$Device,
  [string]$Source,
  [switch]$Bars,
  [switch]$UsbAudio,
  [string]$AudioEndpoint,
  [ValidateRange(1,3600)][int]$Seconds=10
)
$ErrorActionPreference='Stop'
if($Bars.IsPresent -eq [bool]$Source){throw 'Select exactly one of -Bars or -Source'}
$root=Split-Path $PSScriptRoot -Parent
$env:PATH="C:\Program Files\NDI\NDI 6 Runtime\v6;$env:PATH"
$arguments=@('--slot',"$Slot",'--device',$Device,'--seconds',"$Seconds")
if($Bars){$arguments+='--bars'}else{$arguments+=@('--source',$Source)}
if($UsbAudio){if(!$AudioEndpoint){throw 'Exact audio endpoint ID required'};$arguments+=@('--audio-usb','--audio-endpoint',$AudioEndpoint)}
& "$root\build\Release\ndi-sdi-proof.exe" @arguments
exit $LASTEXITCODE
