$ErrorActionPreference = 'Stop'

$clientProject = Join-Path $PSScriptRoot 'Client\roundTableMeeting'
if (-not (Test-Path -LiteralPath (Join-Path $clientProject 'package.json'))) {
    throw "Client project was not found: $clientProject"
}

Set-Location -LiteralPath $clientProject
& npm run dev -- $args
exit $LASTEXITCODE
