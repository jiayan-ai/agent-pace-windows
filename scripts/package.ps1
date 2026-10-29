param([string]$Runtime = 'win-x64')
$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
if ($Runtime -notin @('win-x64','win-arm64')) { throw 'Unsupported runtime' }
$output = Join-Path $PWD "dist/$Runtime"
dotnet publish src/AgentPace.Windows/AgentPace.Windows.csproj -c Release -r $Runtime --self-contained true -p:PublishSingleFile=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Copy-Item README.md "$output/README.md"
Copy-Item README.zh-CN.md "$output/README.zh-CN.md"
$zip = Join-Path $PWD "dist/Agent-Pace-1.0.0-beta.5-$Runtime.zip"
Compress-Archive -Path "$output/*" -DestinationPath $zip -Force
(Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + (Split-Path $zip -Leaf) | Set-Content "$zip.sha256"
Write-Output $zip
