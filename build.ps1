# Builds dist\SPEControl.exe (and dist\SPEControl-Setup.exe if Inno Setup is installed).
# Uses only the C# compiler that ships with Windows - no SDK needed.
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$dist = Join-Path $root 'dist'
$obj  = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $dist, $obj | Out-Null
Add-Type -AssemblyName System.Drawing

# --- App icon: a power-meter gauge, saved as a multi-size PNG-based .ico
function New-IconPng([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $r = [int]($s * 0.18)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $r, $r, 180, 90); $path.AddArc($s - $r - 1, 0, $r, $r, 270, 90)
    $path.AddArc($s - $r - 1, $s - $r - 1, $r, $r, 0, 90); $path.AddArc(0, $s - $r - 1, $r, $r, 90, 90)
    $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 23, 27, 33))), $path)
    $w = [single]([Math]::Max(2, $s * 0.11))
    $m = $s * 0.17; $d = $s - 2 * $m; $cy = $m + $s * 0.08
    $track = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 50, 58, 70)), $w
    $track.StartCap = 'Round'; $track.EndCap = 'Round'
    $g.DrawArc($track, [single]$m, [single]$cy, [single]$d, [single]$d, 180, 180)
    $arc = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(255, 58, 160, 255)), $w
    $arc.StartCap = 'Round'; $arc.EndCap = 'Round'
    $g.DrawArc($arc, [single]$m, [single]$cy, [single]$d, [single]$d, 180, 125)
    $cx = $s / 2; $ccy = $cy + $d / 2
    $needle = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), ([single]([Math]::Max(1.5, $s * 0.05)))
    $needle.EndCap = 'Round'
    $a = (180 + 125) * [Math]::PI / 180; $len = $d * 0.42
    $g.DrawLine($needle, [single]$cx, [single]$ccy, [single]($cx + $len * [Math]::Cos($a)), [single]($ccy + $len * [Math]::Sin($a)))
    $dot = $s * 0.12
    $g.FillEllipse([System.Drawing.Brushes]::White, [single]($cx - $dot / 2), [single]($ccy - $dot / 2), [single]$dot, [single]$dot)
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    return ,$ms.ToArray()
}
$sizes = 16, 24, 32, 48, 256
$pngs = $sizes | ForEach-Object { ,(New-IconPng $_) }
$ico = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter $ico
$bw.Write([UInt16]0); $bw.Write([UInt16]1); $bw.Write([UInt16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]; $dim = if ($sz -ge 256) { 0 } else { $sz }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([UInt16]1); $bw.Write([UInt16]32); $bw.Write([UInt32]$pngs[$i].Length); $bw.Write([UInt32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($p in $pngs) { $bw.Write($p) }
$bw.Flush()
$iconPath = Join-Path $obj 'app.ico'
[IO.File]::WriteAllBytes($iconPath, $ico.ToArray())
Copy-Item $iconPath (Join-Path $root 'installer\app.ico') -Force

# --- Compile
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$exe = Join-Path $dist 'SPEControl.exe'
& $csc /nologo /target:winexe /optimize+ "/out:$exe" "/win32icon:$iconPath" `
    "/resource:$(Join-Path $root 'web\index.html'),index.html" `
    /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Management.dll `
    (Join-Path $root 'server\SpeControl.cs')
if ($LASTEXITCODE -ne 0) { throw 'Compile failed' }
Write-Host "Built $exe"

# --- Installer (optional)
$iscc = @("${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe") |
    Where-Object { Test-Path $_ } | Select-Object -First 1
if ($iscc) {
    & $iscc /Q (Join-Path $root 'installer\SPEControl.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer build failed' }
    Write-Host "Built $(Join-Path $dist 'SPEControl-Setup.exe')"
} else {
    Write-Host 'Inno Setup not found - skipped the installer (winget install JRSoftware.InnoSetup).'
}
