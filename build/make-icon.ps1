# 앱·설치 파일 아이콘(build\darkroom.ico)을 트레이 아이콘과 같은 조리개 모양으로 만든다. 여러 크기를 PNG로 담은 ICO.
#   .\build\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$out = Join-Path $PSScriptRoot "darkroom.ico"
$sizes = 16, 24, 32, 48, 64, 128, 256
$pngs = foreach ($s in $sizes) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)
    $k = $s / 32.0
    $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 43, 43, 43))
    $g.FillEllipse($bg, 1 * $k, 1 * $k, 30 * $k, 30 * $k)
    # 작은 크기는 트레이 아이콘과 같은 굵기, 큰 크기(48px~)는 선을 가늘게 해 뭉개 보이지 않게
    $big = $s -ge 48
    $ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(224, 122, 46)), ([math]::Max(1.5, $(if ($big) { 2 } else { 3 }) * $k))
    $g.DrawEllipse($ring, 3 * $k, 3 * $k, 26 * $k, 26 * $k)
    $blade = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(200, 242, 242, 242)), ([math]::Max(1, $(if ($big) { 0.9 } else { 1.6 }) * $k))
    $blade.StartCap = $blade.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    for ($i = 0; $i -lt 6; $i++) {
        $a = $i * [math]::PI / 3
        $g.DrawLine($blade, [float](16 * $k + 11 * $k * [math]::Cos($a)), [float](16 * $k + 11 * $k * [math]::Sin($a)),
                            [float](16 * $k + 5 * $k * [math]::Cos($a + 1.9)), [float](16 * $k + 5 * $k * [math]::Sin($a + 1.9)))
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , $ms.ToArray()
}
$fs = [System.IO.File]::Create($out); $w = New-Object System.IO.BinaryWriter $fs
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
"{0} ({1:N0} bytes)" -f $out, (Get-Item $out).Length
