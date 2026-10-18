param(
  [Parameter(Mandatory=$true)][string]$ConfigurationPath,
  [ValidateRange(1,3600)][int]$Seconds=15,
  [switch]$UsbAudio
)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$configuration=Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
$outputs=@($configuration.outputs)
if($outputs.Count -ne 4){throw 'Exactly four explicitly commissioned assignments are required'}
if(@($outputs.slot | Sort-Object -Unique).Count -ne 4 -or @($outputs.device | Sort-Object -Unique).Count -ne 4){throw 'Duplicate slot or device assignment'}
foreach($o in $outputs){if($o.slot -lt 1 -or $o.slot -gt 4 -or $o.device -notmatch '^0x[0-9a-fA-F]+$' -or !$o.source){throw 'Invalid slot, stable device ID, or source'}}
$native=Join-Path $root 'build\Release\ndi-sdi-proof.exe'
$runName='four-outputs-'+(Get-Date -Format 'yyyyMMdd-HHmmss')
$runFolder=Join-Path $root "reports\private\$runName"
New-Item -ItemType Directory -Force -Path $runFolder | Out-Null
$children=@()
try {
  foreach($o in $outputs){
    $start=[Diagnostics.ProcessStartInfo]::new($native)
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $start.Environment['PATH']="C:\Program Files\NDI\NDI 6 Runtime\v6;$env:PATH"
    foreach($arg in @('--slot',"$($o.slot)",'--device',"$($o.device)",'--source',"$($o.source)",'--seconds',"$Seconds")){$start.ArgumentList.Add($arg)}
    if($UsbAudio){
      if(!$configuration.audio.endpointId){throw 'Exact audio endpoint ID required'}
      foreach($arg in @('--audio-usb','--audio-endpoint',"$($configuration.audio.endpointId)")){$start.ArgumentList.Add($arg)}
    }
    $process=[Diagnostics.Process]::Start($start)
    $children+=@{Process=$process;Slot=$o.slot;Output=$process.StandardOutput.ReadToEndAsync();Error=$process.StandardError.ReadToEndAsync()}
  }
  $failed=$false
  foreach($child in $children){
    if(!$child.Process.WaitForExit(($Seconds+20)*1000)){throw "Output $($child.Slot) exceeded bounded qualification duration"}
    $stdout=$child.Output.GetAwaiter().GetResult()
    $stderr=$child.Error.GetAwaiter().GetResult()
    $stdout | Set-Content -LiteralPath (Join-Path $runFolder "output-$($child.Slot).json") -Encoding utf8
    $stderr | Set-Content -LiteralPath (Join-Path $runFolder "output-$($child.Slot).stderr.txt") -Encoding utf8
    if($child.Process.ExitCode){$failed=$true;Write-Output "Output $($child.Slot): FAILED ($($child.Process.ExitCode)): $stderr"}else{Write-Output "Output $($child.Slot): $stdout"}
  }
  Write-Output "Evidence: $runFolder"
  if($failed){throw 'At least one output diagnostic failed; inspect all evidence'}
} finally {
  foreach($child in $children){if(!$child.Process.HasExited){$child.Process.Kill()};$child.Process.Dispose()}
}
