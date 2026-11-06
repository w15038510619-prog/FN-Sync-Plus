Add-Type -AssemblyName System.Drawing

function Draw-ConceptA([System.Drawing.Graphics]$g, [int]$size) {
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $scale = $size / 256.0
    
    # Background soft gradient
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($size, $size)),
        [System.Drawing.Color]::FromArgb(245, 247, 250),
        [System.Drawing.Color]::FromArgb(228, 235, 245)
    )
    $g.FillEllipse($bgBrush, 8*$scale, 8*$scale, 240*$scale, 240*$scale)
    
    # Left: Folder
    $folderBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(37, 99, 235))
    $g.FillRoundedRectangle = $null # standard fill
    $g.FillRectangle($folderBrush, 28*$scale, 70*$scale, 90*$scale, 110*$scale)
    $tabBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(29, 78, 216))
    $g.FillRectangle($tabBrush, 28*$scale, 52*$scale, 45*$scale, 22*$scale)
    
    # Right: NAS Box
    $nasBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(15, 23, 42))
    $g.FillRectangle($nasBrush, 140*$scale, 60*$scale, 88*$scale, 130*$scale)
    # NAS Drive Bays
    $bayBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(51, 65, 85))
    $g.FillRectangle($bayBrush, 150*$scale, 75*$scale, 30*$scale, 95*$scale)
    $g.FillRectangle($bayBrush, 188*$scale, 75*$scale, 30*$scale, 95*$scale)
    # LEDs
    $ledGreen = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(16, 185, 129))
    $g.FillEllipse($ledGreen, 160*$scale, 180*$scale, 8*$scale, 8*$scale)
    $g.FillEllipse($ledGreen, 198*$scale, 180*$scale, 8*$scale, 8*$scale)
    
    # Arrow / Stream
    $arrowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(245, 158, 11), 8*$scale)
    $arrowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::ArrowAnchor
    $g.DrawLine($arrowPen, 95*$scale, 125*$scale, 145*$scale, 125*$scale)
}

function Draw-ConceptB([System.Drawing.Graphics]$g, [int]$size) {
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $scale = $size / 256.0
    
    # Shield background
    $shieldBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF(0, $size)),
        [System.Drawing.Color]::FromArgb(16, 185, 129),
        [System.Drawing.Color]::FromArgb(5, 150, 105)
    )
    $pts = @(
        (New-Object System.Drawing.PointF(128*$scale, 16*$scale)),
        (New-Object System.Drawing.PointF(230*$scale, 50*$scale)),
        (New-Object System.Drawing.PointF(205*$scale, 180*$scale)),
        (New-Object System.Drawing.PointF(128*$scale, 240*$scale)),
        (New-Object System.Drawing.PointF(51*$scale, 180*$scale)),
        (New-Object System.Drawing.PointF(26*$scale, 50*$scale))
    )
    $g.FillPolygon($shieldBrush, $pts)
    
    # White inner Folder
    $innerBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillRectangle($innerBrush, 65*$scale, 95*$scale, 126*$scale, 85*$scale)
    $g.FillRectangle($innerBrush, 65*$scale, 80*$scale, 55*$scale, 20*$scale)
    
    # Single direction sync badge inside folder
    $circlePen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(15, 23, 42), 8*$scale)
    $circlePen.EndCap = [System.Drawing.Drawing2D.LineCap]::ArrowAnchor
    $g.DrawArc($circlePen, 100*$scale, 110*$scale, 55*$scale, 55*$scale, 45, 270)
}

function Draw-ConceptC([System.Drawing.Graphics]$g, [int]$size) {
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $scale = $size / 256.0
    
    # Base rounded backdrop (Deep slate with mica touch)
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF(0, $size)),
        [System.Drawing.Color]::FromArgb(15, 23, 42),
        [System.Drawing.Color]::FromArgb(30, 41, 59)
    )
    
    # Soft rounded rectangle background
    $rect = New-Object System.Drawing.RectangleF(10*$scale, 10*$scale, 236*$scale, 236*$scale)
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $radius = 48*$scale
    $path.AddArc($rect.X, $rect.Y, $radius, $radius, 180, 90)
    $path.AddArc($rect.Right - $radius, $rect.Y, $radius, $radius, 270, 90)
    $path.AddArc($rect.Right - $radius, $rect.Bottom - $radius, $radius, $radius, 0, 90)
    $path.AddArc($rect.X, $rect.Bottom - $radius, $radius, $radius, 90, 90)
    $path.CloseFigure()
    $g.FillPath($bgBrush, $path)

    # Subtly glow border
    $borderPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(70, 255, 255, 255), 2*$scale)
    $g.DrawPath($borderPen, $path)

    # 1. Folder at top-center: Fluent Gold/Amber + Blue accents
    $tabBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 36*$scale)),
        (New-Object System.Drawing.PointF(0, 60*$scale)),
        [System.Drawing.Color]::FromArgb(251, 191, 36),
        [System.Drawing.Color]::FromArgb(245, 158, 11)
    )
    # Folder Back Tab
    $tabPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $tabPath.AddLines(@(
        (New-Object System.Drawing.PointF(40*$scale, 58*$scale)),
        (New-Object System.Drawing.PointF(40*$scale, 44*$scale)),
        (New-Object System.Drawing.PointF(92*$scale, 44*$scale)),
        (New-Object System.Drawing.PointF(110*$scale, 58*$scale))
    ))
    $tabPath.CloseFigure()
    $g.FillPath($tabBrush, $tabPath)

    # Folder Front Body
    $folderBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 56*$scale)),
        (New-Object System.Drawing.PointF(0, 130*$scale)),
        [System.Drawing.Color]::FromArgb(252, 211, 77),
        [System.Drawing.Color]::FromArgb(217, 119, 6)
    )
    $fRect = New-Object System.Drawing.RectangleF(36*$scale, 56*$scale, 184*$scale, 78*$scale)
    $fPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $fRadius = 14*$scale
    $fPath.AddArc($fRect.X, $fRect.Y, $fRadius, $fRadius, 180, 90)
    $fPath.AddArc($fRect.Right - $fRadius, $fRect.Y, $fRadius, $fRadius, 270, 90)
    $fPath.AddArc($fRect.Right - $fRadius, $fRect.Bottom - $fRadius, $fRadius, $fRadius, 0, 90)
    $fPath.AddArc($fRect.X, $fRect.Bottom - $fRadius, $fRadius, $fRadius, 90, 90)
    $fPath.CloseFigure()
    $g.FillPath($folderBrush, $fPath)

    # 2. NAS Storage Rack at bottom
    $nasBodyBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 158*$scale)),
        (New-Object System.Drawing.PointF(0, 218*$scale)),
        [System.Drawing.Color]::FromArgb(51, 65, 85),
        [System.Drawing.Color]::FromArgb(15, 23, 42)
    )
    $nasRect = New-Object System.Drawing.RectangleF(40*$scale, 160*$scale, 176*$scale, 58*$scale)
    $nasPath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $nasRadius = 10*$scale
    $nasPath.AddArc($nasRect.X, $nasRect.Y, $nasRadius, $nasRadius, 180, 90)
    $nasPath.AddArc($nasRect.Right - $nasRadius, $nasRect.Y, $nasRadius, $nasRadius, 270, 90)
    $nasPath.AddArc($nasRect.Right - $nasRadius, $nasRect.Bottom - $nasRadius, $nasRadius, $nasRadius, 0, 90)
    $nasPath.AddArc($nasRect.X, $nasRect.Bottom - $nasRadius, $nasRadius, $nasRadius, 90, 90)
    $nasPath.CloseFigure()
    $g.FillPath($nasBodyBrush, $nasPath)

    # NAS Drive Slots
    $slotBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(30, 41, 59))
    $g.FillRectangle($slotBrush, 52*$scale, 170*$scale, 40*$scale, 38*$scale)
    $g.FillRectangle($slotBrush, 100*$scale, 170*$scale, 40*$scale, 38*$scale)
    $g.FillRectangle($slotBrush, 148*$scale, 170*$scale, 40*$scale, 38*$scale)

    # Status LED lights (Green & Blue)
    $ledGreen = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(16, 185, 129))
    $ledBlue = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(59, 130, 246))
    $g.FillEllipse($ledGreen, 198*$scale, 172*$scale, 8*$scale, 8*$scale)
    $g.FillEllipse($ledBlue, 198*$scale, 186*$scale, 8*$scale, 8*$scale)

    # 3. Flow Arrow in center: Bright Cyan / Electric Blue downward arrow
    $arrowPen = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(56, 189, 248), 9*$scale)
    $arrowPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $arrowPen.EndCap = [System.Drawing.Drawing2D.LineCap]::ArrowAnchor
    $g.DrawLine($arrowPen, 128*$scale, 120*$scale, 128*$scale, 156*$scale)
}

# 1. Output 3-concept preview side-by-side
$previewBmp = New-Object System.Drawing.Bitmap(780, 280)
$pg = [System.Drawing.Graphics]::FromImage($previewBmp)
$pg.Clear([System.Drawing.Color]::FromArgb(248, 250, 252))

# Concept A
$bmpA = New-Object System.Drawing.Bitmap(256, 256)
$ga = [System.Drawing.Graphics]::FromImage($bmpA)
$ga.Clear([System.Drawing.Color]::Transparent)
Draw-ConceptA $ga 256
$pg.DrawImage($bmpA, 10, 10, 240, 240)

# Concept B
$bmpB = New-Object System.Drawing.Bitmap(256, 256)
$gb = [System.Drawing.Graphics]::FromImage($bmpB)
$gb.Clear([System.Drawing.Color]::Transparent)
Draw-ConceptB $gb 256
$pg.DrawImage($bmpB, 270, 10, 240, 240)

# Concept C
$bmpC = New-Object System.Drawing.Bitmap(256, 256)
$gc = [System.Drawing.Graphics]::FromImage($bmpC)
$gc.Clear([System.Drawing.Color]::Transparent)
Draw-ConceptC $gc 256
$pg.DrawImage($bmpC, 530, 10, 240, 240)

# Save concept preview to brain artifact
$previewPath = "C:\Users\S\.gemini\antigravity\brain\eacd3aa8-c774-45a0-b5d4-d23d66a0eaa6\gui_v0.4.0_icon_concepts.png"
$previewBmp.Save($previewPath, [System.Drawing.Imaging.ImageFormat]::Png)
Write-Host "Concept preview saved to $previewPath"

# 2. Generate multi-resolution icons for Concept C
$sizes = @(256, 128, 64, 48, 32, 24, 16)
$pngBytesList = [System.Collections.Generic.List[byte[]]]::new()

foreach ($sz in $sizes) {
    $b = New-Object System.Drawing.Bitmap($sz, $sz)
    $g = [System.Drawing.Graphics]::FromImage($b)
    $g.Clear([System.Drawing.Color]::Transparent)
    Draw-ConceptC $g $sz
    
    $ms = New-Object System.IO.MemoryStream
    $b.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBytesList.Add($ms.ToArray())
    
    if ($sz -eq 256) {
        $b.Save("D:\FN_AutoSync_Package\FNSyncPlus\FNSyncPlus.WinUI\Assets\Square150x150Logo.scale-200.png", [System.Drawing.Imaging.ImageFormat]::Png)
        $b.Save("D:\FN_AutoSync_Package\FNSyncPlus\FNSyncPlus.WinUI\Assets\StoreLogo.png", [System.Drawing.Imaging.ImageFormat]::Png)
    } elseif ($sz -eq 48) {
        $b.Save("D:\FN_AutoSync_Package\FNSyncPlus\FNSyncPlus.WinUI\Assets\Square44x44Logo.scale-200.png", [System.Drawing.Imaging.ImageFormat]::Png)
        $b.Save("D:\FN_AutoSync_Package\FNSyncPlus\FNSyncPlus.WinUI\Assets\Square44x44Logo.targetsize-48_altform-lightunplated.png", [System.Drawing.Imaging.ImageFormat]::Png)
    } elseif ($sz -eq 24) {
        $b.Save("D:\FN_AutoSync_Package\FNSyncPlus\FNSyncPlus.WinUI\Assets\Square44x44Logo.targetsize-24_altform-unplated.png", [System.Drawing.Imaging.ImageFormat]::Png)
    }
}

# 3. Assemble Windows ICO binary
$icoStream = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($icoStream)

# Header
$bw.Write([uint16]0) # Reserved
$bw.Write([uint16]1) # Type 1 = ICO
$bw.Write([uint16]$sizes.Count) # Image Count

# Calculate offsets
$offset = 6 + ($sizes.Count * 16)

for ($i = 0; $i -lt $sizes.Count; $i++) {
    $sz = $sizes[$i]
    $bytes = $pngBytesList[$i]
    
    $dimByte = 0
    if ($sz -ne 256) { $dimByte = [byte]$sz }
    
    $bw.Write([byte]$dimByte) # Width (0 means 256)
    $bw.Write([byte]$dimByte) # Height
    $bw.Write([byte]0) # Color count
    $bw.Write([byte]0) # Reserved
    $bw.Write([uint16]1) # Color planes
    $bw.Write([uint16]32) # Bits per pixel
    $bw.Write([uint32]$bytes.Length) # Image data size
    $bw.Write([uint32]$offset) # Data offset
    
    $offset += $bytes.Length
}

for ($i = 0; $i -lt $sizes.Count; $i++) {
    $bw.Write($pngBytesList[$i])
}

$icoBytes = $icoStream.ToArray()
[System.IO.File]::WriteAllBytes("D:\FN_AutoSync_Package\FNSyncPlus\FNSyncPlus.WinUI\Assets\AppIcon.ico", $icoBytes)
Write-Host "AppIcon.ico generated successfully ($($icoBytes.Length) bytes, $($sizes.Count) resolutions: $($sizes -join ', ') px)"
