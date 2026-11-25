# 앱·설치 파일·트레이 아이콘(build\darkroom.ico)을 build\mark.svg(헌팅캡·콧수염·턱수염 아저씨)로 만든다.
# Microsoft Edge(헤드리스)로 SVG를 크기별로 그려 PNG를 담은 ICO 하나로 묶는다.
#   .\build\make-icon.ps1
$ErrorActionPreference = "Stop"
Add-Type -AssemblyName System.Drawing
$svg = Join-Path $PSScriptRoot "mark.svg"
$out = Join-Path $PSScriptRoot "darkroom.ico"
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe", "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw "Microsoft Edge가 필요합니다" }

# 크기별 그림을 한 장에 정해진 자리로 놓고(투명 배경) 찍은 뒤 잘라 낸다
$work = Join-Path ([IO.Path]::GetTempPath()) ("darkroom-icon-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory $work | Out-Null
try {
    $b64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($svg))
    $x = 0; $slots = @()
    $imgs = foreach ($s in $sizes) { $slots += , @($s, $x); "<img style='position:absolute;left:${x}px;top:0' width='$s' height='$s' src='data:image/svg+xml;base64,$b64'>"; $x += $s + 8 }
    $html = "<!doctype html><html><body style='margin:0;background:transparent'>" + ($imgs -join '') + "</body></html>"
    [IO.File]::WriteAllText("$work\icon.html", $html, (New-Object Text.UTF8Encoding $false))
    $shot = "$work\icon.png"
    # Edge는 "저장했다"는 안내를 오류 출력으로 내보내므로 Start-Process로 돌린다
    $args = @("--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1", "--default-background-color=00000000",
        "--user-data-dir=`"$work\profile`"", "--window-size=$x,260", "--screenshot=`"$shot`"", "file:///$($work -replace '\\','/')/icon.html")
    Start-Process -FilePath $edge -ArgumentList $args -Wait -WindowStyle Hidden
    for ($i = 0; $i -lt 50 -and -not (Test-Path $shot); $i++) { Start-Sleep -Milliseconds 200 }
    if (-not (Test-Path $shot)) { throw "Edge로 그리지 못했습니다" }
    $sheet = [Drawing.Image]::FromFile($shot)
    $pngs = foreach ($slot in $slots) {
        $s = $slot[0]; $bmp = New-Object Drawing.Bitmap $s, $s
        $g = [Drawing.Graphics]::FromImage($bmp)
        $g.DrawImage($sheet, (New-Object Drawing.Rectangle 0, 0, $s, $s), (New-Object Drawing.Rectangle $slot[1], 0, $s, $s), [Drawing.GraphicsUnit]::Pixel)
        $g.Dispose()
        $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
        , $ms.ToArray()
    }
    $sheet.Dispose()
}
finally { Start-Sleep -Milliseconds 300; Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue }

$fs = [IO.File]::Create($out); $w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $d = $pngs[$i]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$d.Length); $w.Write([uint32]$offset)
    $offset += $d.Length
}
foreach ($d in $pngs) { $w.Write($d) }
$w.Close()
"{0} ({1:N0} bytes, {2})" -f $out, (Get-Item $out).Length, ($sizes -join ",")
