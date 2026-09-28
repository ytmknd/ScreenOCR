# ScreenOCR

ScreenOCR は、画面上で選んだ範囲を PP-OCRv6 Small または Windows 標準 OCR で読み取り、テキストをクリップボードへコピーする Windows 11 常駐アプリです。同じ操作で QR コードも読み取れます。Sysinternals ZoomIt の OCR 操作と同じ `Ctrl+Alt+6` を使いながら、日本語に入りやすい余分な空白を除去します。OCR も QR もローカルで動作し、画像や認識結果を外部サービスへ送信しません。

## 使い方

1. ScreenOCR を起動します。タスクバーには表示されず、通知領域にアイコンが現れます。
2. `Ctrl+Alt+6` を押します。仮想デスクトップ全体が、その時点の凍結画像として表示されます。
3. 左ボタンで読みたい範囲をドラッグします。ボタンを離すと選択範囲の中央に「OCR 処理中…」が表示され、成功時はテキストがクリップボードへ入ります。範囲に QR コードがあれば、OCR より先にその内容がコピーされます。

`Esc`、右クリック、再度の `Ctrl+Alt+6`、またはフォーカス喪失でキャンセルできます。`Ctrl+A` はマウスがあるモニター全体を選びます。5 px 未満の範囲は受け付けません。トレイアイコンのダブルクリックまたは「文字を読み取る」でも開始できます。

ホットキー直後に全モニターを一度だけ `BitBlt` し、選択後に画面を取り直しません。そのため、選択中に背後が変わっても切り出す画像は変わりません。DRM 保護コンテンツや一部のハードウェアオーバーレイは黒くなる場合があります。

OCR は、選択画像の長辺が 1400 px 未満なら `2.0x`、`3.0x`、`4.0x`、1400 px 以上なら `1.0x`、`2.0x` の順で候補を試します。`OcrEngine.MaxImageDimension` を超える候補は除外します。150% を超える高 DPI モニターでは各候補を 1 段階下げます。

最初の結果に常用漢字外の不自然な CJK 漢字があるか、有効文字が 8 文字未満の場合だけ次の倍率へ進みます。各結果は `有効文字数 - 常用漢字外CJK×重み - 混在スクリプトトークン×重み - 孤立ラテン1文字×重み + 英単語辞書ヒット×重み`（既定 `4 / 6 / 2 / 2`）で採点し、最大スコア（同点なら先の試行）を採用します。常用漢字表は埋め込みの2136字、英単語辞書は埋め込みの約9500語（一般的な英単語＋Windows UI頻出語）を使用します。重みはすべて `config.json` の `scoreRareKanjiPenalty` 等から調整できます。暗背景の反転は試行 D、縦書きの90度回転は試行 Eです。採用倍率とスコアはログおよび `--json` に出力されます。

### 日英デュアルエンジン統合（実験的）

`ocrLanguages` に 2 つ以上の言語（既定 `["ja", "en-US"]`）が設定され、かつそれぞれの OCR 言語パックが実際にインストールされている場合、ベース言語（既定 ja）の倍率探索が終わった後、同じ前処理画像で 2 番目の言語を 1 回だけ追加認識し、単語のバウンディングボックスが重なる区間ごとにスコアの高い方を採用します。1 つの候補語（他エンジンの単語）が複数の狭いベースランに跨って重なる場合は、それらのランをまとめて 1 グループとして候補語で置換します（置換後に前後のランの残骸が残らないようにするため）。基礎点は文字ごとの妥当性で重み付けします（常用漢字・かな・約物は満点、英単語辞書にヒットしたラテン文字は満点、ヒットしないラテン文字は低い重み、数字は中間の重み）。日本語 1 文字とラテン 1 文字の情報量の違いを無視した単純な文字数比較を避けるためです。実機検証済み（`en-US` OCR パック導入後、SPEC.md §1.6/§1.7 参照）。利用可能な言語が 1 つしかない環境では自動的に単一エンジン動作にフォールバックし、`--json` の `MergeInfo.Merged` が `false` になります（トレイの「両エンジンを統合する」も無効表示になります）。統合を無効化したい場合は `mergeEngines: false` または `--no-merge` を使ってください。

## QR コードの読み取り

`Ctrl+Alt+6` で選んだ範囲に QR コードがあると、OCR へ渡す前にデコードし、その内容をクリップボードへコピーして「QR コードを読み取りました」と通知します。読み取れなければ何も通知せず、従来どおり OCR に進みます。専用のホットキーやモード切り替えはありません。オフにするにはトレイの「QR コードを先に読み取る」か `qrCodeEnabled: false` を使います。

- 等倍 → 白黒反転（ダークテーマの反転 QR 用）→ 2 倍拡大（小さな QR 用、長辺 700 px 未満のときだけ）の順に試します。採用した試行名はログに `QR` / `QR-inv` / `QR-2x` として残ります。
- 傾いた QR、回転した QR、1 回の選択に複数の QR（検出順に改行で連結）に対応します。日本語は UTF-8 バイトモードと Shift_JIS の Kanji モードの両方を読めます。
- **URL の QR でも自動ではブラウザーを開きません。** 内容が `http` / `https` の 1 件だけのとき、通知が `クリックで開く: <ホスト名>` になり、クリックしたときだけ既定のブラウザーで開きます。
- デコードは [ZXing.Net](https://github.com/micjahn/ZXing.Net)（Apache-2.0）でローカルに行い、画像も結果も外部へ送信しません。ログにもデコード結果の本文は残しません。

## PP-OCRv6 Small の導入と選択

英文字を優先しつつ日本語も扱う軽量な選択肢として、[RapidOCR](https://rapidai.github.io/RapidOCRDocs/main/install_usage/rapidocr/usage/) の ONNX Runtime バックエンドから PP-OCRv6 Small の検出・認識モデルを使用できます。Python が利用できる状態で次を実行すると、RapidOCR 3.9 系と必要なモデルをインストールして動作確認します。

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install-ppocrv6-small.ps1
```

導入後、トレイメニューの `OCR エンジン` → `PP-OCRv6 Small` を選びます。別の Python にインストールした場合は `ppOcrV6SmallPythonPath`、CLI の一回だけ変更する場合は `--ppocr-python` にその `python.exe` を指定してください。認識時の一時画像とJSONは処理後に削除されます。

英単語 1 語だけをぴったり囲むような狭い範囲でも読めるように、検出へ渡す前に小さい切り抜きを高さ 48 px まで拡大し、背景色の余白を四辺へ足します（PP-OCR の検出は文字が切り抜きの縁に接していると領域を出せないため）。それでも検出が空だった場合は、切り抜き全体を 1 行として認識モデルだけで読み直します。この経路を通ったときはログの試行名が `PPv6-rec` になります（SPEC.md §6.7.1）。

## OCR 言語機能のインストール

日本語 OCR が一覧に無い場合は、Windows の次の場所から追加します。

`設定` → `時刻と言語` → `言語と地域` → `日本語` の `…` → `言語のオプション` → `オプションの言語機能` → `機能を追加する` → `光学式文字認識`

英数字が混在する画面の精度を上げたい場合は、`English (United States)` にも同じ手順で光学式文字認識を追加してください。認識エンジンは、設定言語、ユーザープロファイル言語、日本語、インストール済み言語の先頭、の順にフォールバックします。

## 設定

設定ファイルは `%APPDATA%\ScreenOCR\config.json`（UTF-8、BOM なし）です。初回 GUI 起動時に作成されます。トレイの「設定ファイルを開く」で編集し、「設定を再読み込み」で反映します。トレイ上のチェック項目は直ちに保存されます。未知の JSON キーは保存時にも保持します。

| 項目 | 既定値 | 説明 |
| --- | --- | --- |
| `hotkey` | `"Ctrl+Alt+6"` | 修飾キー `Ctrl` / `Alt` / `Shift` / `Win` と、`0-9` / `A-Z` / `F1-F24` の組み合わせ |
| `ocrEngine` | `"ppocrv6-small"` | `ppocrv6-small` または `windows` |
| `ppOcrV6SmallPythonPath` | `"python"` | RapidOCR 3.9 以上を導入した Python 実行ファイル |
| `ppOcrV6SmallTimeoutMs` | `120000` | PP-OCRv6 Small 1 回の処理タイムアウト（ms、初回はモデル初期化を含む） |
| `ocrLanguage` | `"auto"` | `auto` または `ja`、`en-US` などの BCP-47 タグ（単数、後方互換用） |
| `ocrLanguages` | 新規インストール時のみ `["ja", "en-US"]` | 順序付きの言語リスト（先頭がベース優先）。省略時は `ocrLanguage` にフォールバックする（＝既存の設定ファイルは従来どおり単一エンジンで動く） |
| `mergeEngines` | `true` | 2 言語以上が実際に利用可能なとき、両エンジンの結果を統合するか |
| `qrCodeEnabled` | `true` | OCR へ渡す前に選択範囲の QR コードを走査するか |
| `mergeMarginScore` | `0.3` | 対抗候補が採用されるために必要なスコア差（実測値。スコアは文字ごとの重み付けのため小数） |
| `mergeBaseCjkRatio` | `0.30` | ベース言語結果の CJK 比率がこの値以上ならベースとして採用する閾値（暫定値） |
| `mergeCoverageAreaRatio` | `0.5` | 候補語の矩形がベースランの矩形を「実質的に覆っている」とみなす面積比の閾値。1 つの候補語が複数の狭いベースランに跨るケースをまとめて 1 グループとして置換するために使う |
| `scoreRareKanjiPenalty` | `4` | 常用漢字外の CJK 統合漢字 1 文字あたりの減点（暫定値） |
| `scoreMixedScriptPenalty` | `6` | ラテン文字と CJK が同居するトークン 1 個あたりの減点（暫定値） |
| `scoreIsolatedLatinPenalty` | `2` | CJK に挟まれた孤立ラテン 1 文字あたりの減点（暫定値） |
| `scoreEnglishLexiconBonus` | `2` | 英単語辞書にヒットしたトークン 1 個あたりの加点（暫定値） |
| `scorePlausibleCharWeight` | `1.0` | 基礎点の文字重み: 常用漢字・かな・約物など（常用外CJK・ラテン・数字以外）の妥当な文字 1 個あたり |
| `scoreLatinLexiconCharWeight` | `1.0` | 基礎点の文字重み: 英単語辞書にヒットしたラテン文字ラン内の 1 文字あたり |
| `scoreLatinNonLexiconCharWeight` | `0.3` | 基礎点の文字重み: 英単語辞書にヒットしなかったラテン文字ラン内の 1 文字あたり |
| `scoreDigitCharWeight` | `0.5` | 基礎点の文字重み: 数字 1 文字あたり |
| `overlayDimPercent` | `35` | 選択画面の暗幕濃度（%） |
| `selectionBorderColor` | `"#0078D4"` | 選択枠の HTML 色 |
| `minSelectionSize` | `5` | 選択できる最小の幅・高さ（物理 px） |
| `autoInvert` | `true` | 平均輝度 0.40 未満の暗背景で反転試行を追加 |
| `tryVertical` | `false` | 時計回り 90° 回転の縦書き試行を常に追加 |
| `maxScale` | `4.0` | OCR 候補倍率の上限。上限を超える候補はこの値へ丸め、重複を除外。実行時の `MaxImageDimension` を超える候補は除外 |
| `attemptTimeoutMs` | `2000` | 一連の認識試行のタイムアウト（ms） |
| `removeCjkSpaces` | `true` | 日本語・CJK に接する語の余分な半角空白を削除 |
| `sortLines` | `true` | 認識行を上から下、同じ行帯では左から右へ並べ替え |
| `joinLines` | `false` | 文末記号や文字種を見ながら画面上の行を連結 |
| `collapseSpaces` | `true` | 連続する半角空白を 1 個へ圧縮 |
| `normalizeFullWidth` | `false` | 全角英数字・全角空白だけを半角化（NFKC は不使用） |
| `halfToFullKana` | `false` | 半角カナを、濁点・半濁点を含め全角カナへ変換 |
| `replacements` | `[]` | 後述する順序付き置換ルール |
| `copyImageToo` | `false` | テキストに加えて選択画像を `CF_DIB` でコピー |
| `runAtStartup` | `false` | トレイ操作用の状態。実登録先は現在ユーザーの Run キー |
| `debugSaveImages` | `false` | 前処理画像を `%APPDATA%\ScreenOCR\debug` に保存 |
| `logRetentionDays` | `7` | `%APPDATA%\ScreenOCR\logs` のログ保持日数 |

### 置換ルール

通常置換は `regex: false`、.NET 正規表現は `regex: true` にします。正規表現のタイムアウトは 200 ms です。不正なルールだけが無効になり、ほかのルールとアプリの起動には影響しません。既定値は空配列で、推測による置換はしません。

```json
"replacements": [
  {
    "pattern": "(?<=[ぁ-んァ-ヶ])一(?=[ぁ-んァ-ヶ])",
    "replacement": "ー",
    "regex": true,
    "enabled": true
  },
  {
    "pattern": "\u00A0",
    "replacement": " ",
    "regex": false,
    "enabled": true
  }
]
```

## 昇格ウィンドウの制限

通常権限で動く ScreenOCR は、UIPI の制約により、管理者権限のウィンドウが前面にある間はホットキーを受け取れません。必要な場合は次の手順で ScreenOCR 自体を管理者として自動起動します。

1. タスク スケジューラで「タスクの作成」を開きます。
2. 「全般」で「最上位の特権で実行する」をオンにし、Windows 11 を選びます。
3. 「トリガー」に「ログオン時」を追加します。
4. 「操作」に「プログラムの開始」を追加し、発行した `ScreenOCR.exe` のフルパスを指定します。
5. トレイの通常の「スタートアップに登録」はオフにして、二重起動を避けます。

## CLI

```powershell
dotnet run --project src\ScreenOCR -- --list-languages
dotnet run --project src\ScreenOCR -- --list-engines
dotnet run --project src\ScreenOCR -- --ocr-file image.png --lang ja
dotnet run --project src\ScreenOCR -- --ocr-file image.png --engine ppocrv6-small --json
dotnet run --project src\ScreenOCR -- --ocr-file image.png --lang ja,en-US --json
dotnet run --project src\ScreenOCR -- --ocr-file image.png --no-merge
dotnet run --project src\ScreenOCR -- --ocr-file image.png --raw
dotnet run --project src\ScreenOCR -- --ocr-file image.png --json
dotnet run --project src\ScreenOCR -- --ocr-file image.png --json-verbose
dotnet run --project src\ScreenOCR -- --region 100,100,800,300 --lang ja
dotnet run --project src\ScreenOCR -- --ocr-file qr.png --qr
dotnet run --project src\ScreenOCR -- --region 100,100,400,400 --qr --json
dotnet run --project src\ScreenOCR -- --version
```

終了コードは、成功 `0`、文字なし `1`、認識エンジンなし `2`、引数エラー `3`、その他 `4` です。`--qr` は QR コードだけを走査し、見つからなければ終了コード `1` になります。GUI と違い、CLI は `--qr` を付けないかぎり QR を走査しません（同じコマンドの出力が入力画像によって OCR 結果と QR の内容のどちらかに変わらないようにするためです）。`--qr` は OCR を一切走らせないため、`--lang` / `--engine` / `--ppocr-python` / `--raw` / `--no-merge` と併用すると引数エラーになります。`--engine` で `windows` または `ppocrv6-small` を選択でき、`--ppocr-python` はその実行だけ設定ファイルより優先します。`--raw` は OCR の行・単語を空白区切りで再構成した後処理前テキスト、通常出力は各単語の座標から日本語の不要な空白を除いて再構成したテキストです。`--lang` は Windows OCR でカンマ区切りの複数言語を指定でき（例 `ja,en-US`）、`--no-merge` で統合を無効化できます。`--json` のトップレベルには採用した `Scale`、`Score`、`ValidChars`、`ImplausibleChars`、`Attempts`（全試行の値）、`MergeInfo`（統合の可否・採否ラン数・各ランの判定）が入ります。単語ごとのバウンディングボックスなど詳細な情報が必要な場合は `--json-verbose` を使ってください（`--json` は数十行程度に収まるよう、ボックス情報を含めません）。`--region` はクリップボードを変更しません。

## ビルド、テスト、発行

.NET SDK 10.0.400 以降を使います。NuGet 復元で `Microsoft.Windows.SDK.NET.Ref` と、QR デコード用の `ZXing.Net`（Apache-2.0）が取得されます。

```powershell
dotnet build -c Release
dotnet test
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

framework-dependent の発行物には .NET 10 Desktop Runtime が必要です。

## GUI 手動確認

自動テスト以外の確認は次の手順で行います。

- M2: メモ帳に MS UI Gothic 9pt の日本語を 3 行表示し、`Ctrl+Alt+6` で選択します。空白のない 3 行がコピーされ、成功通知が出ることを確認します。
- M4: ダークモードの文字を選び、ログで反転試行 D と採用試行を確認します。
- M5: 100% と 150% のモニターをまたいで選び、選択どおりの領域が認識されることを確認します。
- M6: `Esc` と右クリックでそれぞれ中止し、既存のクリップボード内容が変わらないことを確認します。
- M7: 文字のない領域を選び、「文字を認識できませんでした」が出ることを確認します。
- M8: ZoomIt を起動してから ScreenOCR を起動し、ホットキー競合の通知とエラーバッジが出ても常駐を続けることを確認します。
- M9: `hotkey` を `Ctrl+Alt+7` に変えて再読み込みし、新キーだけが動くことを確認します。
- M10: 4K モニターで 10pt 相当の文字を選び、ログの候補倍率・各スコア・採用倍率と認識結果を確認します。
- M11: GUI を二重起動し、2 個目だけが「既に起動しています」と通知して終了することを確認します。
- M13: PP-OCRv6 Small で英単語 1 語・日本語 2〜3 文字を余白なしでぴったり囲み、認識されることとログの試行名（`PPv6` / `PPv6-rec`）・倍率を確認します。
- M14: 画面に URL の QR コードを表示して `Ctrl+Alt+6` で囲み、URL がコピーされること、クリックするまでブラウザーが開かないこと、続けて QR の無い文字を選ぶと通知なしで従来どおり OCR されることを確認します。

`tests/fixtures/qr-*.png` は `python tests\generate-qr-samples.py`（OpenCV が必要）で再生成できます。生成済みのものをコミットしているため、`dotnet test` に Python は要りません。

認識本文はログに保存しません。QR のデコード結果も同様です。`debugSaveImages` は画面内容を PNG に残すため、調査後はオフにして不要な画像を削除してください。
