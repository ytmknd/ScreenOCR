Add-Type -AssemblyName System.Drawing

$samplePath = Join-Path $PSScriptRoot 'fixtures\quality-sample-12px.png'
$bitmap = [System.Drawing.Bitmap]::new(
    620,
    90,
    [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$bitmap.SetResolution(96, 96)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$font = [System.Drawing.Font]::new(
    'Yu Gothic UI',
    12,
    [System.Drawing.FontStyle]::Regular,
    [System.Drawing.GraphicsUnit]::Pixel)
$brush = [System.Drawing.Brushes]::Black
$format = [System.Drawing.StringFormat]::GenericTypographic

try {
    if ($font.Name -ne 'Yu Gothic UI') {
        throw "Yu Gothic UI is not available (actual font: $($font.Name))."
    }
    $utf8 = [System.Text.Encoding]::UTF8
    $line1 = $utf8.GetString([Convert]::FromBase64String(
        '55S76Z2i5LiK44Gu5paH5a2X44KS6Kqt44G/5Y+W44Gj44Gm44Kv44Oq44OD44OX44Oc44O844OJ44Gr44Kz44OU44O844GX44G+44GZ44CC'))
    $line2 = $utf8.GetString([Convert]::FromBase64String(
        'V2luZG93cyDjga7oqK3lrprjgpLplovjgYTjgabjgIHoqIDoqp7jgqrjg5fjgrfjg6fjg7PjgpLnorroqo3jgZfjgabjgY/jgaDjgZXjgYTjgII='))
    $line3 = $utf8.GetString([Convert]::FromBase64String(
        '44OV44Kh44Kk44Or5ZCNOiByZXBvcnRfMjAyNi54bHN4IO+8iOabtOaWsOaXpSAyMDI2LzA5LzAx77yJ'))
    $graphics.Clear([System.Drawing.Color]::White)
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
    $graphics.DrawString(
        $line1,
        $font,
        $brush,
        [System.Drawing.PointF]::new(4, 5),
        $format)
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
    $graphics.DrawString(
        $line2,
        $font,
        $brush,
        [System.Drawing.PointF]::new(4, 32),
        $format)
    $graphics.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::SingleBitPerPixelGridFit
    $graphics.DrawString(
        $line3,
        $font,
        $brush,
        [System.Drawing.PointF]::new(4, 59),
        $format)
    $bitmap.Save($samplePath, [System.Drawing.Imaging.ImageFormat]::Png)
    Write-Output "$samplePath ($($bitmap.Width)x$($bitmap.Height), $($font.Name) 12px)"
}
finally {
    $font.Dispose()
    $graphics.Dispose()
    $bitmap.Dispose()
}
