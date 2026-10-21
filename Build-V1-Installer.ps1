$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$licensePath = Join-Path $root "LICENSE"
if (-not (Test-Path $licensePath)) {
  throw "GPLv3 LICENSE file was not found at: $licensePath. Refusing to build a distributable installer without the license."
}
Write-Host "GPLv3 license OK: $licensePath"

Write-Host "Publishing MyMusic v1.0.0 (Release, win-x64, self-contained)..."
if (Test-Path "$root\publish") { Remove-Item "$root\publish" -Recurse -Force }
dotnet restore "$root\MyMusic.csproj"

# Publish the self-contained unpackaged WinUI app. MyMusic.csproj contains a
# CopyAppPriToPublish target that copies the generated MyMusic.pri to the publish
# root as resources.pri. That file is required for Application.LoadComponent to
# resolve MainWindow.xaml after installation.
dotnet publish "$root\MyMusic.csproj" -c Release -r win-x64 --self-contained true -p:Platform=x64 -p:WindowsAppSDKSelfContained=true -p:PublishTrimmed=false -p:PublishReadyToRun=false -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw "MyMusic publish failed with exit code $LASTEXITCODE" }

if (-not (Test-Path "$root\publish\MyMusic.exe")) {
  throw "Publish completed but MyMusic.exe was not found in: $root\publish"
}
if (-not (Test-Path "$root\publish\resources.pri")) {
  $generatedPri = Get-ChildItem -Path "$root\bin" -Filter "MyMusic.pri" -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1
  if ($generatedPri) {
    Write-Host "Publish target did not copy resources.pri; copying generated PRI from $($generatedPri.FullName)..."
    Copy-Item $generatedPri.FullName "$root\publish\resources.pri" -Force
  }
}
if (-not (Test-Path "$root\publish\resources.pri")) {
  throw "WinUI generated no app PRI. Expected MyMusic.pri under bin or resources.pri in publish. Cannot build a working installer."
}
Write-Host "WinUI resource index OK: $root\publish\resources.pri"

$isccCandidates = @(
  "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
  "C:\Program Files\Inno Setup 6\ISCC.exe",
  "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
  "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
  $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
  if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) {
  throw "Inno Setup 6 was not found. Install Inno Setup 6, then run this script again."
}

Write-Host "Building installer..."
& $iscc "$root\Installer\MyMusic.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE" }

$out = "$root\Installer\Output\MyMusic-Setup-v1.0.0.exe"
if (-not (Test-Path $out)) { throw "Installer build completed but expected output was not found: $out" }
Write-Host ""
Write-Host "DONE: $out"
