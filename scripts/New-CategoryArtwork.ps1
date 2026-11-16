$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$output = Join-Path (Split-Path $PSScriptRoot -Parent) 'MediaBrowser.Plugins.TuneIn\Images'

function Point([single]$x, [single]$y) { return [System.Drawing.PointF]::new($x, $y) }
function Rounded([single]$x, [single]$y, [single]$width, [single]$height, [single]$radius) {
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $diameter = $radius * 2
    $path.AddArc($x, $y, $diameter, $diameter, 180, 90)
    $path.AddArc($x + $width - $diameter, $y, $diameter, $diameter, 270, 90)
    $path.AddArc($x + $width - $diameter, $y + $height - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($x, $y + $height - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    return ,$path
}

$cards = @(
    @('localradio', 'Local Radio', '#167D8D'),
    @('music', 'Music', '#7045B8'),
    @('bylanguage', 'By Language', '#3067B3'),
    @('bylocation', 'By Location', '#287D57'),
    @('talk', 'Talk Radio', '#B05B26'),
    @('sports', 'Sports', '#B03D4D'),
    @('podcasts', 'Podcasts', '#3F57B2'),
    @('myfavs', 'My Favorites', '#AC3976')
)
foreach ($card in $cards) {
    $bitmap = [System.Drawing.Bitmap]::new(1280, 720)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $rectangle = [System.Drawing.Rectangle]::new(0, 0, 1280, 720)
    $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new($rectangle,
        [System.Drawing.ColorTranslator]::FromHtml('#111B2B'),
        [System.Drawing.ColorTranslator]::FromHtml($card[2]), 25)
    $white = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $muted = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(190, 230, 238, 248))
    $halo = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(22, 255, 255, 255))
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::White, 16)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $font = [System.Drawing.Font]::new('Segoe UI', 46, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $small = [System.Drawing.Font]::new('Segoe UI', 24, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
    $letter = [System.Drawing.Font]::new('Segoe UI', 72, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
    $format = [System.Drawing.StringFormat]::new()
    $format.Alignment = [System.Drawing.StringAlignment]::Center
    try {
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
        $graphics.FillRectangle($background, $rectangle)
        $graphics.FillEllipse($halo, 440, 95, 400, 400)
        switch ($card[0]) {
            'music' {
                $graphics.DrawLine($pen, 595, 210, 595, 365)
                $graphics.DrawLine($pen, 725, 180, 725, 335)
                $graphics.FillPolygon($white, [System.Drawing.PointF[]]@((Point 587 202), (Point 733 170), (Point 733 220), (Point 587 252)))
                $graphics.FillEllipse($white, 530, 335, 72, 50)
                $graphics.FillEllipse($white, 660, 305, 72, 50)
            }
            'myfavs' {
                $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
                try {
                    $path.AddBezier(640, 405, 430, 280, 505, 125, 640, 225)
                    $path.AddBezier(640, 225, 775, 125, 850, 280, 640, 405)
                    $path.CloseFigure()
                    $graphics.FillPath($white, $path)
                } finally { $path.Dispose() }
            }
            'talk' {
                $path = Rounded 589 160 102 185 51
                try { $graphics.DrawPath($pen, $path) } finally { $path.Dispose() }
                $graphics.DrawArc($pen, 549, 205, 182, 180, 0, 180)
                $graphics.DrawLine($pen, 640, 385, 640, 430)
                $graphics.DrawLine($pen, 600, 430, 680, 430)
            }
            'sports' {
                $graphics.DrawEllipse($pen, 525, 175, 230, 230)
                $graphics.DrawLine($pen, 525, 290, 755, 290)
                $graphics.DrawLine($pen, 640, 175, 640, 405)
                $graphics.DrawArc($pen, 445, 178, 185, 225, 285, 150)
                $graphics.DrawArc($pen, 650, 178, 185, 225, 105, 150)
            }
            'podcasts' {
                $graphics.DrawArc($pen, 520, 165, 240, 240, 180, 180)
                foreach ($x in @(515, 720)) {
                    $path = Rounded $x 275 45 115 18
                    try { $graphics.FillPath($white, $path) } finally { $path.Dispose() }
                }
                $graphics.FillEllipse($white, 620, 250, 40, 40)
                $graphics.DrawLine($pen, 640, 315, 640, 400)
                $graphics.DrawLine($pen, 610, 415, 670, 415)
            }
            'bylocation' {
                $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
                try {
                    $path.AddBezier(640, 425, 590, 365, 530, 300, 540, 245)
                    $path.AddBezier(540, 245, 550, 120, 730, 120, 740, 245)
                    $path.AddBezier(740, 245, 750, 300, 690, 365, 640, 425)
                    $path.CloseFigure()
                    $graphics.DrawPath($pen, $path)
                    $graphics.DrawEllipse($pen, 605, 210, 70, 70)
                } finally { $path.Dispose() }
            }
            'bylanguage' {
                $path = Rounded 490 170 190 135 20
                try { $graphics.DrawPath($pen, $path) } finally { $path.Dispose() }
                $path = Rounded 605 285 190 135 20
                try { $graphics.DrawPath($pen, $path) } finally { $path.Dispose() }
                $graphics.DrawString('A', $letter, $white, [single]585, [single]185, $format)
                $graphics.DrawString('B', $letter, $white, [single]700, [single]300, $format)
                $graphics.DrawLine($pen, 515, 305, 515, 340)
                $graphics.DrawLine($pen, 515, 340, 550, 305)
                $graphics.DrawLine($pen, 770, 420, 770, 445)
                $graphics.DrawLine($pen, 770, 445, 740, 420)
            }
            'localradio' {
                $path = Rounded 495 230 290 180 24
                try { $graphics.DrawPath($pen, $path) } finally { $path.Dispose() }
                $graphics.DrawLine($pen, 550, 230, 605, 150)
                foreach ($y in @(285, 325, 365)) { $graphics.DrawLine($pen, 535, $y, 640, $y) }
                $graphics.DrawEllipse($pen, 690, 290, 48, 48)
                $graphics.DrawLine($pen, 705, 370, 735, 370)
            }
        }
        $graphics.DrawString($card[1], $font, $white, [single]640, [single]530, $format)
        $graphics.DrawString('TUNEIN RADIO', $small, $muted, [single]640, [single]600, $format)
        $bitmap.Save((Join-Path $output ('tunein-' + $card[0] + '.png')), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $format.Dispose(); $letter.Dispose(); $small.Dispose(); $font.Dispose()
        $pen.Dispose(); $halo.Dispose(); $muted.Dispose(); $white.Dispose()
        $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose()
    }
}
