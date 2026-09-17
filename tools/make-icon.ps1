Add-Type -AssemblyName System.Drawing

$size = 512
$bmp = New-Object System.Drawing.Bitmap($size, $size)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

# rounded square
$r = 104
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$path.AddArc(0, 0, $r, $r, 180, 90)
$path.AddArc($size - $r, 0, $r, $r, 270, 90)
$path.AddArc($size - $r, $size - $r, $r, $r, 0, 90)
$path.AddArc(0, $size - $r, $r, $r, 90, 90)
$path.CloseFigure()

# background gradient
$rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
$c1 = [System.Drawing.Color]::FromArgb(255, 58, 46, 92)
$c2 = [System.Drawing.Color]::FromArgb(255, 24, 20, 43)
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $c1, $c2, 55.0)
$g.FillPath($brush, $path)

# top right glow
$glow = New-Object System.Drawing.Drawing2D.GraphicsPath
$glow.AddEllipse(300, -60, 320, 320)
$gb = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow)
$gb.CenterColor = [System.Drawing.Color]::FromArgb(90, 0, 200, 220)
$gb.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 0, 200, 220))
$g.FillPath($gb, $glow)

# bottom left glow
$glow2 = New-Object System.Drawing.Drawing2D.GraphicsPath
$glow2.AddEllipse(-90, 300, 300, 300)
$gb2 = New-Object System.Drawing.Drawing2D.PathGradientBrush($glow2)
$gb2.CenterColor = [System.Drawing.Color]::FromArgb(80, 150, 90, 230)
$gb2.SurroundColors = @([System.Drawing.Color]::FromArgb(0, 150, 90, 230))
$g.FillPath($gb2, $glow2)

# main text
$fmt = New-Object System.Drawing.StringFormat
$fmt.Alignment = [System.Drawing.StringAlignment]::Center
$fmt.LineAlignment = [System.Drawing.StringAlignment]::Center
$font = New-Object System.Drawing.Font("Arial", 60, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$shadow = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(120, 0, 0, 0))
$g.DrawString("AuraCanAI", $font, $shadow, (New-Object System.Drawing.RectangleF(4, 224, $size, 120)), $fmt)
$white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
$g.DrawString("AuraCanAI", $font, $white, (New-Object System.Drawing.RectangleF(0, 220, $size, 120)), $fmt)

# accent bar
$accent = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(255, 0, 214, 214))
$g.FillRectangle($accent, 176, 330, 160, 10)

$g.Dispose()
New-Item -ItemType Directory -Force -Path "D:\AuraCanAI.Dalamud\images" | Out-Null
$bmp.Save("D:\AuraCanAI.Dalamud\images\icon.png", [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()
Write-Output "icon.png generated"
