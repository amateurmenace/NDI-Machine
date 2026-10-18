param([string]$ConfigurationPath)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(!$ConfigurationPath){$ConfigurationPath=Join-Path $root 'config\local.commissioning.json'}
$config=(Resolve-Path -LiteralPath $ConfigurationPath).Path
$exe=Join-Path $root 'control\bin\Release\net8.0-windows\win-x64\NDIMachine.Control.exe'
if(!(Test-Path -LiteralPath $exe)){throw 'Build control\NDIMachine.Control.csproj using x64 .NET 8.0.301 first'}
try {
  $state=Invoke-RestMethod ('http://127.0.0.1:9860/api/v1/state?nonce='+[Guid]::NewGuid()) -TimeoutSec 3
  if($state.schemaVersion -eq 1 -and $state.instanceId){Write-Output "Control host already running: $($state.instanceId)"; return}
} catch { }
$private=Join-Path $root 'reports\private'
New-Item -ItemType Directory -Path $private -Force | Out-Null
$stamp=Get-Date -Format yyyyMMdd-HHmmss
$process=Start-Process -FilePath $exe -ArgumentList @('--root', ('"'+$root+'"'), '--commission', ('"'+$config+'"')) -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardOutput "$private\control-$stamp.out.log" -RedirectStandardError "$private\control-$stamp.err.log"
for($attempt=0;$attempt -lt 30;$attempt++) {
  if($process.HasExited){throw "Control host exited $($process.ExitCode); inspect $private\control-$stamp.err.log"}
  try {
    $state=Invoke-RestMethod ('http://127.0.0.1:9860/api/v1/state?nonce='+[Guid]::NewGuid()) -TimeoutSec 3
    if($state.schemaVersion -eq 1 -and $state.instanceId){Write-Output "Control host PID $($process.Id), instance $($state.instanceId). Existing media survives control-host exit. Use Prepare Backups for first start.";return}
  } catch { }
  Start-Sleep -Milliseconds 100
}
throw 'Control host readiness deadline exceeded'
