param(
    [string]$Python = "python",
    # インストーラーから呼ぶとき用。失敗してもウィンドウをすぐ閉じず、理由を読めるようにする。
    [switch]$PauseOnError
)

$ErrorActionPreference = "Stop"

try {
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
}
catch {
    if (-not $PauseOnError) { throw }
    Write-Host ""
    Write-Host "PP-OCRv6 Small を導入できませんでした: $($_.Exception.Message)" -ForegroundColor Red
    Write-Host "Python 3 とインターネット接続が必要です。ScreenOCR は Windows OCR でも使えます（トレイの「OCR エンジン」で切り替え）。"
    Read-Host "Enter キーで閉じます"
    exit 1
}
