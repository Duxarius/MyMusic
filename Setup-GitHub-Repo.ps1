$ErrorActionPreference = 'Stop'
Set-Location (Split-Path -Parent $MyInvocation.MyCommand.Path)

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    throw 'Git was not found. Install Git for Windows, then run this script again.'
}

if (-not (Test-Path '.git')) {
    git init -b main
    if ($LASTEXITCODE -ne 0) { throw 'git init failed.' }
}

git add .
if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }

Write-Host ''
Write-Host 'Repository initialized and files staged.'
Write-Host 'Review with: git status'
Write-Host 'Then commit with: git commit -m "Initial MyMusic V1 repository"'
Write-Host 'After creating the PRIVATE GitHub repository, connect it with:'
Write-Host '  git remote add origin <YOUR-REPOSITORY-URL>'
Write-Host '  git push -u origin main'
