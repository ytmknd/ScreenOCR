param(
    # 明示しなければ Inno Setup 7 → 6 の順に、言語ファイルまで揃ったインストールを探す。
    [string]$Iscc
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\ScreenOCR\ScreenOCR.csproj"
$publish = Join-Path $root "artifacts\publish\ScreenOCR"
$output = Join-Path $root "artifacts\installer"
$script = Join-Path $root "installer\ScreenOCR.iss"

function Find-Iscc {
    $candidates = @(
        (Join-Path $env:ProgramFiles "Inno Setup 7"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 7"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7"),
        (Join-Path $env:ProgramFiles "Inno Setup 6"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6"),
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6")
    )
    foreach ($dir in $candidates) {
        # ISCC.exe だけ置かれた不完全なコピーがあるため、日本語の言語ファイルまで揃っているものに限る。
        if ((Test-Path (Join-Path $dir "ISCC.exe")) -and (Test-Path (Join-Path $dir "Languages\Japanese.isl"))) {
            return (Join-Path $dir "ISCC.exe")
        }
    }
    throw "Inno Setup が見つかりません（Languages\Japanese.isl を含むインストールが必要）。-Iscc で ISCC.exe を指定してください。"
}

if (-not $Iscc) { $Iscc = Find-Iscc }

# 1. .NET ランタイム同梱（self-contained）で発行する。単一ファイルにはしない
#    （Inno の LZMA2 が圧縮するので、二重に圧縮しても起動が遅くなるだけ）。
if (Test-Path $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit code $LASTEXITCODE)." }

# 2. 同梱したランタイムのライセンス文書を、実際に使われたランタイムパックから添える。
$deps = Get-Content -Raw -LiteralPath (Join-Path $publish "ScreenOCR.deps.json") | ConvertFrom-Json
$runtimePack = $deps.libraries.PSObject.Properties.Name |
    Where-Object { $_ -like "runtimepack.Microsoft.NETCore.App.Runtime.win-x64/*" } |
    Select-Object -First 1
if (-not $runtimePack) { throw "ScreenOCR.deps.json に .NET ランタイムパックが見つかりません。" }
$runtimeVersion = $runtimePack.Split("/")[1]
$packDir = Join-Path $root ".packages\microsoft.netcore.app.runtime.win-x64\$runtimeVersion"
$licenses = Join-Path $publish "licenses"
New-Item -ItemType Directory -Force -Path $licenses | Out-Null
Copy-Item -LiteralPath (Join-Path $packDir "LICENSE.TXT") -Destination (Join-Path $licenses "dotnet-LICENSE.txt")
Copy-Item -LiteralPath (Join-Path $packDir "THIRD-PARTY-NOTICES.TXT") -Destination (Join-Path $licenses "dotnet-THIRD-PARTY-NOTICES.txt")

# 3. インストーラーをコンパイルする。
& $Iscc "/DPublishDir=$publish" "/DOutputDir=$output" $script
if ($LASTEXITCODE -ne 0) { throw "ISCC failed (exit code $LASTEXITCODE)." }

$setup = Get-ChildItem -LiteralPath $output -Filter "ScreenOCR-Setup-*.exe" | Sort-Object LastWriteTime | Select-Object -Last 1
Write-Host ""
Write-Host ("Installer: {0} ({1:N1} MB, .NET {2} bundled)" -f $setup.FullName, ($setup.Length / 1MB), $runtimeVersion)
