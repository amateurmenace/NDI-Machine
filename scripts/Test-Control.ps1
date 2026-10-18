param()
$ErrorActionPreference='Stop'
$base='http://127.0.0.1:9860/api/v1'
$nonce=[Guid]::NewGuid().ToString()
$before=Invoke-RestMethod "$base/state?nonce=$nonce" -TimeoutSec 3
if($before.schemaVersion -ne 1 -or $before.nonce -ne $nonce){throw 'Fresh challenge response failed'}
$plan=Invoke-RestMethod "$base/commission" -TimeoutSec 3
$id=[Guid]::NewGuid().ToString()
$body=@{schemaVersion=1;plan=$plan} | ConvertTo-Json -Depth 20 -Compress
# This requests only the already-commissioned media preparation. It never routes
# Videohub or stops an output. Run after Prepare-Backups has reached ready.
if(@($before.outputs | Where-Object health -ne 'healthy').Count){throw 'Prepare all backups before this test'}
$accepted=Invoke-RestMethod "$base/emergency/prepare" -Method Post -ContentType application/json -Headers @{'Idempotency-Key'=$id} -Body $body
$repeated=Invoke-RestMethod "$base/emergency/prepare" -Method Post -ContentType application/json -Headers @{'Idempotency-Key'=$id} -Body $body
if($accepted.operationId -ne $repeated.operationId){throw 'Idempotent retry failed'}
function Assert-HttpStatus([string]$uri,[hashtable]$headers,[int]$expected,[string]$payload='') {
  $parameters=@{Uri=$uri;Headers=$headers;SkipHttpErrorCheck=$true;TimeoutSec=3}
  if($payload){$parameters.Method='Post';$parameters.ContentType='application/json';$parameters.Body=$payload}
  $reply=Invoke-WebRequest @parameters
  if([int]$reply.StatusCode -ne $expected){throw "Expected HTTP $expected, received $($reply.StatusCode)"}
}
Assert-HttpStatus "$base/state?nonce=origin-test" @{Origin='https://untrusted.invalid'} 403
Assert-HttpStatus "$base/state?nonce=host-test" @{Host='untrusted.invalid:9860'} 403
Assert-HttpStatus "$base/state" @{} 400
$plan.paths[0].device='0xdeadbeef'
$invalid=@{schemaVersion=1;plan=$plan} | ConvertTo-Json -Depth 20 -Compress
Assert-HttpStatus "$base/emergency/prepare" @{'Idempotency-Key'=$id} 400 $invalid
$socket=[Net.WebSockets.ClientWebSocket]::new()
$cancel=[Threading.CancellationTokenSource]::new(4000)
try {
  $socket.ConnectAsync([Uri]'ws://127.0.0.1:9860/api/v1/events',$cancel.Token).GetAwaiter().GetResult()
  $buffer=[byte[]]::new(65536); $offset=0
  do {
    $chunk=$socket.ReceiveAsync([ArraySegment[byte]]::new($buffer,$offset,$buffer.Length-$offset),$cancel.Token).GetAwaiter().GetResult()
    $offset+=$chunk.Count
    if($offset -ge $buffer.Length){throw 'WebSocket state exceeds bound'}
  } while(!$chunk.EndOfMessage)
  $event=[Text.Encoding]::UTF8.GetString($buffer,0,$offset) | ConvertFrom-Json -Depth 20
  if($event.instanceId -ne $before.instanceId -or $event.outputs.Count -ne $before.outputs.Count){throw 'WebSocket full-state resynchronization failed'}
} finally {$socket.Abort();$socket.Dispose();$cancel.Dispose()}
$after=Invoke-RestMethod ("$base/state?nonce="+[Guid]::NewGuid())
foreach($old in $before.outputs){
  $current=$after.outputs | Where-Object slot -eq $old.slot
  if($current.workerInstanceId -ne $old.workerInstanceId -or $current.configRevision -ne $old.configRevision){throw 'Validation or retry changed an existing output'}
}
$root=Split-Path $PSScriptRoot -Parent
@{schemaVersion=1;result='Passed';tests=@('fresh nonce','idempotent prepare retry','uncommissioned plan rejected','cross-origin rejected','foreign Host rejected','nonce required','WebSocket full state','worker identity and revision unchanged');before=$before;after=$after;physicalSignalVerified='Unknown'} | ConvertTo-Json -Depth 30 | Set-Content (Join-Path $root 'reports\private\control-api-validation.json')
Write-Output 'PASS: API validation, retries, WebSocket state and unchanged worker identity/revision. No Videohub writes.'
