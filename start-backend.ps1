$ErrorActionPreference = 'Stop'

$serverProject = Join-Path $PSScriptRoot 'Server\RoundTableMeeting\RoundTableMeeting.csproj'
if (-not (Test-Path -LiteralPath $serverProject)) {
    throw "Server project was not found: $serverProject"
}

& dotnet build $serverProject
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& dotnet run --project $serverProject --no-build $args
exit $LASTEXITCODE
