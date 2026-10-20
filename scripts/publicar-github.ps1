param([string]$Repository = "placas-promocionais")
$ErrorActionPreference = "Stop"
Set-Location (Split-Path $PSScriptRoot -Parent)
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "Instale GitHub CLI: winget install GitHub.cli" }
gh auth status
if ($LASTEXITCODE -ne 0) { gh auth login; if ($LASTEXITCODE -ne 0) { throw "Login não concluído." } }
if (-not (Test-Path .git)) { git init -b main; git add .; git commit -m "Implementar aplicativo modular de placas promocionais" }
gh repo create $Repository --private --source . --remote origin --push
if ($LASTEXITCODE -ne 0) { throw "Publicação não concluída. Confira se o nome já existe ou se há um remote origin." }
