# Run explicitly by the operator. Does not stop media workers or write routes.
[CmdletBinding()]param()
$ErrorActionPreference='Stop'
$project=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$stage=Join-Path $project 'dist\NDI Machine-0.5.0-staging'
$destination=Join-Path $project 'dist\NDI Machine-0.5.0'
$old=Join-Path $project 'dist\NDI Machine'
foreach($directory in @($stage,$destination,$old)) {
  if(![IO.Path]::GetFullPath($directory).StartsWith($project+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Package path escaped project'}
}
foreach($name in @('NDI Machine.exe','engine\NDIMachine.Control.exe','launcher\NDIMachine.Launcher.exe','build\Release\atem-remote.exe','config\local.commissioning.json')) {
  if(!(Test-Path -LiteralPath (Join-Path $stage $name))){throw ('Incomplete build: '+$name)}
}
if((Get-Item -LiteralPath (Join-Path $stage 'NDI Machine.exe')).VersionInfo.FileVersion -ne '0.5.0.0'){throw 'Unexpected desktop build'}
$backup=Join-Path $project ('reports\private\activation-050-'+(Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Path $backup | Out-Null
$before=Invoke-RestMethod ('http://127.0.0.1:9860/api/v1/state?nonce='+[Guid]::NewGuid()) -TimeoutSec 5
$before | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $backup 'before.json') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $old 'config') -Destination (Join-Path $backup 'config') -Recurse
$allowed=@($old,$stage,$destination)
$copiedGui='A:\NDI Machine.exe'
$processes=Get-CimInstance Win32_Process | Where-Object {$_.Name -in @('NDIMachine.Control.exe','NDIMachine.Launcher.exe','NDI Machine.exe')}
foreach($process in $processes) {
  $exe=$process.ExecutablePath
  $ours=$exe -and ($allowed | Where-Object {$exe.StartsWith($_+'\',[StringComparison]::OrdinalIgnoreCase)})
  if(!$ours -and $exe -eq $copiedGui -and (Test-Path -LiteralPath (Join-Path $old 'NDI Machine.exe'))) {
    $ours=(Get-FileHash -LiteralPath $exe).Hash -eq (Get-FileHash -LiteralPath (Join-Path $old 'NDI Machine.exe')).Hash
  }
  if(!$ours){throw ('Unrecognized NDI process. Close it manually: '+$process.ProcessId+' '+$exe)}
  $actual=Get-Process -Id $process.ProcessId -ErrorAction Stop
  if($actual.Path -ne $exe){throw 'Process changed; activation cancelled'}
  Stop-Process -Id $actual.Id -ErrorAction Stop
  Wait-Process -Id $actual.Id -Timeout 8 -ErrorAction SilentlyContinue
}
# Side-by-side package: loaded worker binaries in the old package remain intact.
New-Item -ItemType Directory -Force -Path $destination | Out-Null
Get-ChildItem -LiteralPath $stage | Copy-Item -Destination $destination -Recurse -Force
foreach($name in @('local.supervisor.json','local.supervisor.json.lkg','local.discovery.json')) {
  $source=Join-Path $old ('config\'+$name)
  if(Test-Path -LiteralPath $source){Copy-Item -LiteralPath $source -Destination (Join-Path $destination 'config') -Force}
}
if(Test-Path -LiteralPath $copiedGui){Copy-Item -LiteralPath $copiedGui -Destination (Join-Path $backup 'NDI Machine-old.exe')}
Copy-Item -LiteralPath (Join-Path $destination 'NDI Machine.exe') -Destination $copiedGui -Force
$launcher=Join-Path $destination 'launcher\NDIMachine.Launcher.exe'
Set-ItemProperty -LiteralPath 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'NDI Machine Launcher' -Value ('"'+$launcher+'"')
Start-Process -FilePath $launcher -WorkingDirectory $destination -WindowStyle Hidden
Start-Process -FilePath (Join-Path $destination 'engine\NDIMachine.Control.exe') -ArgumentList @('--root',('"'+$destination+'"')) -WorkingDirectory $destination -WindowStyle Hidden
$shortcut=(New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path ([Environment]::GetFolderPath('Desktop')) 'NDI Machine.lnk'))
$shortcut.TargetPath=Join-Path $destination 'NDI Machine.exe';$shortcut.WorkingDirectory=$destination;$shortcut.Save()
Start-Process -FilePath $shortcut.TargetPath -WorkingDirectory $destination
Start-Sleep -Seconds 3
$after=Invoke-RestMethod ('http://127.0.0.1:9860/api/v1/state?nonce='+[Guid]::NewGuid()) -TimeoutSec 5
$after | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath (Join-Path $backup 'after.json') -Encoding utf8
if($after.instanceId -eq $before.instanceId){throw 'New engine not confirmed'}
if($after.capabilities -notcontains 'explicit-start'){throw 'New engine capabilities missing'}
foreach($worker in $before.outputs | Where-Object running) {
  $observed=$after.outputs | Where-Object slot -eq $worker.slot
  if($observed.workerInstanceId -ne $worker.workerInstanceId){Write-Warning ('Output '+$worker.slot+' identity changed; inspect before routing')}
}
Write-Host 'NDI Machine 0.5.0 activated. Existing media workers preserved. Missing receivers stay idle.' -ForegroundColor Green
Write-Host 'Use Stop source or Engine controls when you want to stop streams. PRIME applies the new Input 1 audio preset.'
Write-Host ('Evidence: '+$backup)
