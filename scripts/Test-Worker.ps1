param([Parameter(Mandatory=$true)][string]$ConfigurationPath)
$ErrorActionPreference='Stop'
. "$PSScriptRoot\Worker-Ipc.ps1"
$root=Split-Path $PSScriptRoot -Parent
$configuration=Get-Content -LiteralPath $ConfigurationPath -Raw | ConvertFrom-Json
$first=$configuration.outputs | Where-Object slot -eq 1
$second=$configuration.outputs | Where-Object slot -eq 2
if(!$first -or !$second -or !$configuration.audio.endpointId){throw 'Slots 1/2 and exact audio endpoint must be commissioned'}
$children=@()
$evidence=[ordered]@{schemaVersion=1;physicalSignalVerified='Unknown'}
function Start-ProofChild([string]$name,[string[]]$arguments){
  $start=[Diagnostics.ProcessStartInfo]::new((Join-Path $root "build\Release\$name.exe"))
  $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WindowStyle='Hidden'
  $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
  $start.Environment['PATH']="C:\Program Files\NDI\NDI 6 Runtime\v6;$env:PATH"
  foreach($argument in $arguments){$start.ArgumentList.Add($argument)}
  $process=[Diagnostics.Process]::Start($start)
  return @{Process=$process;Output=$process.StandardOutput.ReadToEndAsync();Error=$process.StandardError.ReadToEndAsync();Name=$name}
}
function Wait-ProofState([string]$source,[string]$operation=''){
  $deadline=[DateTime]::UtcNow.AddSeconds(15)
  do {
    try {
      $reply=Invoke-WorkerIpc -Pipe 'NDIMachine.output.1.v1' -Request @{method='state'} -TimeoutMs 1000
      if($reply.ok -and $reply.state.source -eq $source -and $reply.state.health -eq 'healthy' -and (!$operation -or $reply.state.committedOperationId -eq $operation)){return $reply.state}
    } catch { }
    Start-Sleep -Milliseconds 100
  } while([DateTime]::UtcNow -lt $deadline)
  throw "Worker did not become healthy on expected source: $source"
}
try {
  $children+=Start-ProofChild 'audio-host' @('--endpoint',$configuration.audio.endpointId,'--daemon')
  $deadline=[DateTime]::UtcNow.AddSeconds(5)
  do {Start-Sleep -Milliseconds 100;try{$audio=Invoke-WorkerIpc -Pipe 'NDIMachine.audio.v1' -Request @{method='state'} -TimeoutMs 500}catch{$audio=$null}}until(($audio -and $audio.fresh -and $audio.frames -gt 10000) -or [DateTime]::UtcNow -gt $deadline)
  if(!$audio.fresh){throw 'Persistent audio host not ready'}
  $children+=Start-ProofChild 'media-worker' @('--slot','1','--device',$first.device,'--source',$first.source,'--audio-endpoint',$configuration.audio.endpointId)
  $before=Wait-ProofState $first.source
  $evidence.initial=$before
  $bad=Invoke-WorkerIpc -Pipe 'NDIMachine.output.1.v1' -Request @{method='prepare';source='NDI Machine intentionally missing qualification source 20261018'} -TimeoutMs 15000
  if($bad.ok){throw 'Missing source preparation unexpectedly succeeded'}
  $after=Wait-ProofState $first.source
  if($after.configRevision -ne $before.configRevision -or $after.counters.completed -le $before.counters.completed){throw 'Failed preparation disturbed the existing route'}
  $evidence.failedPreparation=@{preservedSource=$after.source;completedDuringFailure=$after.counters.completed-$before.counters.completed;error=$bad.error}
  $prepared=Invoke-WorkerIpc -Pipe 'NDIMachine.output.1.v1' -Request @{method='prepare';source=$second.source} -TimeoutMs 15000
  if(!$prepared.ok){throw $prepared.error}
  $commit=@{method='commit';preparedOperationId=$prepared.preparedOperationId;expectedRevision=$after.configRevision;id=[Guid]::NewGuid().ToString()}
  $accepted=Invoke-WorkerIpc -Pipe 'NDIMachine.output.1.v1' -Request $commit
  if(!$accepted.ok){throw $accepted.error}
  $repeated=Invoke-WorkerIpc -Pipe 'NDIMachine.output.1.v1' -Request $commit
  if($repeated.operationId -ne $accepted.operationId){throw 'Idempotent commit returned a different operation'}
  $evidence.switched=Wait-ProofState $second.source $accepted.operationId
  $prepared=Invoke-WorkerIpc -Pipe 'NDIMachine.output.1.v1' -Request @{method='prepare';source=$first.source} -TimeoutMs 15000
  $accepted=Invoke-WorkerIpc -Pipe 'NDIMachine.output.1.v1' -Request @{method='commit';preparedOperationId=$prepared.preparedOperationId;expectedRevision=$evidence.switched.configRevision}
  if(!$accepted.ok){throw $accepted.error}
  $evidence.restored=Wait-ProofState $first.source $accepted.operationId
  Invoke-WorkerIpc -Pipe 'NDIMachine.output.1.v1' -Request @{method='stop'} | Out-Null
  if(!$children[1].Process.WaitForExit(5000)){throw 'Media worker did not stop gracefully'}
  Invoke-WorkerIpc -Pipe 'NDIMachine.audio.v1' -Request @{method='stop'} | Out-Null
  if(!$children[0].Process.WaitForExit(5000)){throw 'Audio host did not stop gracefully'}
  $path=Join-Path $root ('reports\private\worker-switch-'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'.json')
  $evidence | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $path -Encoding utf8
  $evidence | ConvertTo-Json -Depth 30
  Write-Output "Evidence: $path"
} finally {
  foreach($child in $children){
    if(!$child.Process.HasExited){$child.Process.Kill()}
    $errorText=$child.Error.GetAwaiter().GetResult()
    if($errorText){Write-Output "$($child.Name): $errorText"}
    $child.Process.Dispose()
  }
}
