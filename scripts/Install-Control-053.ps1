# Install the tested control-only hotfix after the operator stops the old host.
# This script never terminates processes or changes media/router/audio settings.
[CmdletBinding()]param()
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$package=Join-Path $project 'dist\NDI Machine-0.5.2'
$engine=Join-Path $package 'engine\NDIMachine.Control.exe'
$stage=Join-Path $project 'dist\NDI Machine-engine-0.5.3-staging\NDIMachine.Control.exe'
if(Get-Process -Name 'NDIMachine.Control' -ErrorAction SilentlyContinue){throw 'Stop the verified old control host before installing. No process was terminated.'}
if((Get-Item -LiteralPath $stage).VersionInfo.FileVersion -ne '0.5.3.0'){throw 'Expected tested control 0.5.3 build'}
if(!(Test-Path -LiteralPath (Join-Path $package 'config\local.commissioning.json'))){throw 'Installed package missing commissioning'}
$evidence=Join-Path $project ('reports\private\control-053-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $evidence | Out-Null
Copy-Item -LiteralPath $engine -Destination (Join-Path $evidence 'NDIMachine.Control-previous.exe')
Copy-Item -LiteralPath $stage -Destination $engine -Force
if((Get-FileHash -LiteralPath $stage).Hash -ne (Get-FileHash -LiteralPath $engine).Hash){throw 'Installed engine hash differs from staged build'}
Start-Process -FilePath $engine -ArgumentList @('--root',('"'+$package+'"')) -WorkingDirectory $package -WindowStyle Hidden
$state=$null
for($attempt=0;$attempt -lt 20;$attempt++){
  try{$state=Invoke-RestMethod ('http://127.0.0.1:9860/api/v1/state?nonce='+[guid]::NewGuid()) -TimeoutSec 2;break}catch{Start-Sleep -Milliseconds 250}
}
if(!$state){throw 'Engine started but local API not confirmed; inspect before retrying'}
$state | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $evidence 'after.json') -Encoding utf8
Write-Output ('Control 0.5.3 active: '+$state.instanceId)
Write-Output ('No media preparation or route change requested. Evidence: '+$evidence)
