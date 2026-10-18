param()
$ErrorActionPreference='Stop'
$base='http://127.0.0.1:9860/api/v1'
$plan=Invoke-RestMethod "$base/commission" -TimeoutSec 3
$id=[Guid]::NewGuid().ToString()
$body=@{schemaVersion=1;plan=$plan} | ConvertTo-Json -Depth 20 -Compress
$accepted=Invoke-RestMethod "$base/emergency/prepare" -Method Post -ContentType application/json -Headers @{'Idempotency-Key'=$id} -Body $body -TimeoutSec 3
Write-Output "Prepare accepted: $($accepted.operationId). This does not change Videohub routes."
for($attempt=0;$attempt -lt 80;$attempt++) {
  $operation=Invoke-RestMethod "$base/operations/$id" -TimeoutSec 3
  if($operation.state -eq 'failed'){throw $operation.error}
  if($operation.state -eq 'committed'){
    $state=Invoke-RestMethod ("$base/state?nonce="+[Guid]::NewGuid()) -TimeoutSec 3
    $state.outputs | Select-Object slot,source,health,configRevision,videoAgeMs,scheduleAgeMs
    Write-Output 'Preparation committed at the SDK scheduling layer; independently observed SDI picture/audio remains Unknown.'
    return
  }
  Start-Sleep -Milliseconds 500
}
throw 'Preparation status deadline exceeded; inspect actual state before retrying'
