param([string]$Source,[int]$Seconds=10,[string]$Groups,[string]$ExtraIps)
$root=Split-Path $PSScriptRoot -Parent
$env:PATH="C:\Program Files\NDI\NDI 6 Runtime\v6;$env:PATH"
$arguments=@('--seconds',"$Seconds")
if($Source){$arguments+=@('--source',$Source)}
if($Groups){$arguments+=@('--groups',$Groups)}
if($ExtraIps){$arguments+=@('--extra-ips',$ExtraIps)}
& "$root\build\Release\ndi-probe.exe" @arguments
exit $LASTEXITCODE
