param(
  [Parameter(Mandatory=$true)][string]$ConfigurationPath,
  [ValidateRange(1,300)][int]$Seconds=20,
  [switch]$FourOutputs
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$configuration=Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
if($configuration.audio.backend -ne 'wasapi' -or !$configuration.audio.endpointId){throw 'An explicit WASAPI capture endpoint is required'}
if(($configuration.audio.channels -join ',') -ne '1,2'){throw 'This diagnostic supports receive channels 1/2 only'}
$duration=if($FourOutputs){$Seconds+15}else{$Seconds}
$start=[Diagnostics.ProcessStartInfo]::new((Join-Path $root 'build\Release\audio-host.exe'))
$start.UseShellExecute=$false
$start.CreateNoWindow=$true
$start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$start.RedirectStandardOutput=$true
$start.RedirectStandardError=$true
foreach($arg in @('--endpoint',"$($configuration.audio.endpointId)",'--seconds',"$duration")){$start.ArgumentList.Add($arg)}
$child=[Diagnostics.Process]::Start($start)
$stdout=$child.StandardOutput.ReadToEndAsync()
$stderr=$child.StandardError.ReadToEndAsync()
try {
  if($FourOutputs){
    Start-Sleep -Milliseconds 300
    if($child.HasExited){throw "Capture host exited before output preparation: $($stderr.GetAwaiter().GetResult())"}
    & "$PSScriptRoot\Test-FourOutputs.ps1" -ConfigurationPath $ConfigurationPath -Seconds $Seconds -UsbAudio
  }
  if(!$child.WaitForExit(($duration+5)*1000)){throw 'Audio host exceeded bounded capture duration'}
  $result=$stdout.GetAwaiter().GetResult()
  $errors=$stderr.GetAwaiter().GetResult()
  $folder=Join-Path $root 'reports\private'
  New-Item -ItemType Directory -Force -Path $folder | Out-Null
  $path=Join-Path $folder ('usb-audio-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.json')
  $result | Set-Content -LiteralPath $path -Encoding utf8
  if($child.ExitCode){throw "Audio capture failed: $errors"}
  Write-Output $result
  Write-Output "Capture evidence: $path"
} finally {
  if(!$child.HasExited){$child.Kill()}
  $child.Dispose()
}
