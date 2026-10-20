$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)
dotnet test tests/Placas.Tests/Placas.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Testes falharam." }
dotnet publish src/Placas.Desktop/Placas.Desktop.csproj -c Release -r win-x64 --self-contained true -o artifacts/windows
if ($LASTEXITCODE -ne 0) { throw "Compilação falhou." }
Compress-Archive -Path artifacts/windows/* -DestinationPath artifacts/PlacasPromocionais-windows-x64.zip -Force
