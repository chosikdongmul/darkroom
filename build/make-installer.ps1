# 설치 파일 만들기: 단일 exe(publish.ps1)를 만든 뒤 Inno Setup 6으로 dist\Darkroom-Setup-<버전>.exe를 만든다.
#   .\build\make-installer.ps1
# Inno Setup 6 필요(https://jrsoftware.org/isinfo.php). ISCC.exe를 PATH·기본 설치 위치에서 찾는다.
$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent

$version = ([xml](Get-Content (Join-Path $repo "src\Directory.Build.props") -Raw)).Project.PropertyGroup.Version
if (-not $version) { throw "src\Directory.Build.props에서 Version을 찾지 못함" }

$iscc = @(
    (Get-Command iscc -ErrorAction SilentlyContinue).Source,
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6(ISCC.exe)을 찾지 못함" }

& (Join-Path $PSScriptRoot "publish.ps1")
& $iscc /Q "/DAppVersion=$version" (Join-Path $PSScriptRoot "installer.iss")
if ($LASTEXITCODE -ne 0) { throw "ISCC 실패 ($LASTEXITCODE)" }

$setup = Join-Path $repo "dist\Darkroom-Setup-$version.exe"
"{0} ({1:N1} MB)" -f $setup, ((Get-Item $setup).Length / 1MB)
