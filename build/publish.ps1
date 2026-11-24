# 배포용 단일 exe 만들기: .NET 런타임·위젯·테마·글꼴·Manager 화면이 모두 들어간 Darkroom.exe 하나.
#   .\build\publish.ps1            → dist\Darkroom.exe
#   .\build\publish.ps1 -Out D:\x  → D:\x\Darkroom.exe
# 압축하지 않는다: 압축하면 실행할 때 라이브러리를 모두 메모리에 풀어 둬서 메모리를 200MB 넘게 더 쓴다(설치 파일은 따로 압축됨).
# 새 PC에서는 이 exe만 복사해 실행하면 된다(Windows 10/11 x64, WebView2 런타임 필요 — Windows 11은 기본 포함).
param([string]$Out = "")

$ErrorActionPreference = "Stop"
$repo = Split-Path $PSScriptRoot -Parent
if (-not $Out) { $Out = Join-Path $repo "dist" }

dotnet publish (Join-Path $repo "src\Darkroom.App\Darkroom.App.csproj") `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=false `
    -p:DebugType=none -p:DebugSymbols=false `
    -o $Out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish 실패" }

$exe = Join-Path $Out "Darkroom.exe"
$others = Get-ChildItem $Out -File | Where-Object Name -ne "Darkroom.exe"
"{0} ({1:N1} MB)" -f $exe, ((Get-Item $exe).Length / 1MB)
if ($others) { "exe 옆에 함께 생긴 파일: " + (($others | ForEach-Object Name) -join ", ") }
