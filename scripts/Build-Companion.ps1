param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$node26='C:\Program Files\Companion\resources\node-runtimes\node26\node.exe'
$node22='C:\Program Files\Companion\resources\node-runtimes\node22\node.exe'
Push-Location (Join-Path $root 'companion')
try {
  if(!(Test-Path -LiteralPath 'node_modules\typescript\bin\tsc')){throw 'Run npm ci in companion using a supported Node 22/26 runtime first'}
  & $node26 'node_modules\typescript\bin\tsc' -p tsconfig.json
  if($LASTEXITCODE){throw 'Companion TypeScript build failed'}
  & $node26 --test 'dist/test/*.test.js'
  if($LASTEXITCODE){throw 'Companion tests failed'}
  & $node22 'node_modules\@companion-module\tools\dist\scripts\build-connection.js' --dev
  if($LASTEXITCODE){throw 'Companion package failed'}
} finally {Pop-Location}
