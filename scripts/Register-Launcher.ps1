param([switch]$NoStartup)
$ErrorActionPreference='Stop'
$root=Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\NDI Machine'
$exe=Join-Path $root 'launcher\NDIMachine.Launcher.exe'
if(!(Test-Path -LiteralPath $exe)){throw 'Build the complete desktop package first'}
if(!$NoStartup){
  $key='HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
  New-Item -Path $key -Force | Out-Null
  New-ItemProperty -LiteralPath $key -Name 'NDI Machine Launcher' -Value ('"'+$exe+'" --root "'+$root+'"') -PropertyType String -Force | Out-Null
}
Start-Process -FilePath $exe -ArgumentList @('--root',('"'+$root+'"')) -WorkingDirectory $root -WindowStyle Hidden
Write-Output 'Local launcher started. It opens no GUI, media outputs or routes until Prime is pressed.'
