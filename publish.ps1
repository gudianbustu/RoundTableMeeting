[CmdletBinding()]
param([string]$Configuration='Release',[string]$OutputRoot=(Join-Path $PSScriptRoot 'publish'))
$ErrorActionPreference='Stop'
$project=Join-Path $PSScriptRoot 'Server\RoundTableMeeting\RoundTableMeeting.csproj'
$client=Join-Path $PSScriptRoot 'Client\roundTableMeeting'
$OutputRoot=[IO.Path]::GetFullPath($OutputRoot);$publishDir=Join-Path $OutputRoot 'RoundTableMeeting';$zipPath=Join-Path $OutputRoot ('RoundTableMeeting{0}.zip' -f (Get-Date -Format 'yyyyMMddHHmm'))
if(!(Test-Path $project)){throw "Server project was not found: $project"}
New-Item -ItemType Directory -Force $OutputRoot|Out-Null
if(Test-Path $publishDir){Remove-Item $publishDir -Recurse -Force}
if(Test-Path $zipPath){Remove-Item $zipPath -Force}
Push-Location $client
try { & volta run --node 24.20.0 --pnpm 11.24.0 pnpm install --frozen-lockfile; if($LASTEXITCODE){throw 'Frontend dependency installation failed.'}; & volta run --node 24.20.0 --pnpm 11.24.0 pnpm build; if($LASTEXITCODE){throw 'Frontend build failed.'} } finally { Pop-Location }
& dotnet publish $project --configuration $Configuration --output $publishDir --no-restore -p:PublishClient=false
$webRoot=Join-Path $publishDir 'wwwroot';New-Item -ItemType Directory -Force $webRoot|Out-Null
Copy-Item (Join-Path $client 'dist\*') $webRoot -Recurse -Force
if(!(Test-Path (Join-Path $webRoot 'index.html'))){throw 'Published frontend index.html is missing.'}
Compress-Archive -Path $publishDir -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "Created archive: $zipPath"




