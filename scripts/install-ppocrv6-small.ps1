param(
    [string]$Python = "python"
)

$ErrorActionPreference = "Stop"

& $Python -m pip install "rapidocr>=3.9.0,<4"
if ($LASTEXITCODE -ne 0) {
    throw "RapidOCR installation failed (exit code $LASTEXITCODE)."
}

$rapidOcrCommand = & $Python -c "import os,sysconfig; print(os.path.join(sysconfig.get_path('scripts'), 'rapidocr.exe'))"
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $rapidOcrCommand)) {
    throw "rapidocr.exe was not found."
}

& $rapidOcrCommand check
if ($LASTEXITCODE -ne 0) {
    throw "RapidOCR model check failed (exit code $LASTEXITCODE)."
}

Write-Host "PP-OCRv6 Small is ready."
