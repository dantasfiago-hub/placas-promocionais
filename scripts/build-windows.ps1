$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)
dotnet test tests/Placas.Tests/Placas.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "Testes falharam." }
$publishDirectory = "artifacts/windows"
if (Test-Path $publishDirectory) { Remove-Item $publishDirectory -Recurse -Force }
dotnet publish src/Placas.Desktop/Placas.Desktop.csproj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -p:SatelliteResourceLanguages=pt-BR -p:DebugType=None `
    -o $publishDirectory
if ($LASTEXITCODE -ne 0) { throw "Compilação falhou." }
Compress-Archive -Path "$publishDirectory/*" -DestinationPath artifacts/PlacasPromocionais-windows-x64.zip -Force
