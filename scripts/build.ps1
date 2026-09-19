$ErrorActionPreference = 'Stop'
Set-Location (Split-Path $PSScriptRoot -Parent)
dotnet publish src/SeekAI -c Release -r win-x64 --self-contained true -o artifacts/SeekAI
if ($LASTEXITCODE -ne 0) { throw 'Publish failed' }
Compress-Archive -Path artifacts/SeekAI/* -DestinationPath artifacts/SeekAI-win-x64.zip -Force
Write-Host 'Ready: artifacts\SeekAI\SeekAI.exe'
