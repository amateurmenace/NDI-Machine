param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)
$root=Split-Path $PSScriptRoot -Parent
& "$root\build\Release\decklink-sdk-check.exe" @Arguments
exit $LASTEXITCODE
