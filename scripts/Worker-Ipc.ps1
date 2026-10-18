function Invoke-WorkerIpc {
  param([Parameter(Mandatory=$true)][string]$Pipe,[Parameter(Mandatory=$true)][hashtable]$Request,[int]$TimeoutMs=3000)
  $Request.schemaVersion=1
  if(!$Request.id){$Request.id=[Guid]::NewGuid().ToString()}
  $client=[IO.Pipes.NamedPipeClientStream]::new('.', $Pipe, [IO.Pipes.PipeDirection]::InOut, [IO.Pipes.PipeOptions]::Asynchronous)
  $cancel=[Threading.CancellationTokenSource]::new($TimeoutMs)
  try {
    $client.ConnectAsync($cancel.Token).GetAwaiter().GetResult()
    $bytes=[Text.Encoding]::UTF8.GetBytes(($Request | ConvertTo-Json -Compress -Depth 20))
    $size=[BitConverter]::GetBytes([uint32]$bytes.Length)
    $client.WriteAsync($size,0,4,$cancel.Token).GetAwaiter().GetResult()
    $client.WriteAsync($bytes,0,$bytes.Length,$cancel.Token).GetAwaiter().GetResult()
    $client.FlushAsync($cancel.Token).GetAwaiter().GetResult()
    $header=[byte[]]::new(4); $offset=0
    while($offset -lt 4){$read=$client.ReadAsync($header,$offset,4-$offset,$cancel.Token).GetAwaiter().GetResult();if(!$read){throw 'Worker closed without a response'};$offset+=$read}
    $length=[BitConverter]::ToUInt32($header,0)
    if(!$length -or $length -gt 65536){throw 'Invalid IPC response length'}
    $body=[byte[]]::new($length);$offset=0
    while($offset -lt $length){$read=$client.ReadAsync($body,$offset,$length-$offset,$cancel.Token).GetAwaiter().GetResult();if(!$read){throw 'Incomplete IPC response'};$offset+=$read}
    $receipt=[byte[]]@(1);$client.WriteAsync($receipt,0,1,$cancel.Token).GetAwaiter().GetResult()
    $reply=[Text.Encoding]::UTF8.GetString($body) | ConvertFrom-Json -Depth 30
    if($reply.schemaVersion -ne 1 -or $reply.id -ne $Request.id){throw 'IPC schema or response ID mismatch'}
    return $reply
  } finally {$client.Dispose();$cancel.Dispose()}
}
