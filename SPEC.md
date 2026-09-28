# ScreenOCR 仕様書

画面上の文字を PP-OCRv6 Small または Windows OCR で認識し、クリップボードにコピーする常駐ユーティリティ。
Sysinternals ZoomIt の「Copy text (OCR) from a region of the screen to clipboard (Ctrl+Alt+6)」機能を単体で再実装し、**日本語環境での実用性を ZoomIt より確実に高める**ことを目的とする。

- 文書バージョン: 1.1
- 作成日: 2026-09-01
- 更新日: 2026-09-04（PP-OCRv6 Small を既定バックエンドとして採用）
- 対象 OS: Windows 11 (10.0.22621 以降)。開発検証機は Windows 11 Education 10.0.26200

---

## 1. 背景と目的

### 1.1 ZoomIt の該当機能

ZoomIt 公式ドキュメントのショートカット表より抜粋:

| Function | Shortcut |
| --- | --- |
| Copy a Region of The Screen To Clipboard | Ctrl + 6 |
| Save a Region of The Screen To a File | Ctrl + Shift + 6 |
| **Copy text (OCR) from a region of The Screen to Clipboard** | **Ctrl + Alt + 6** |
| Exit | Esc or Right-Click |

本アプリが再現するのは **Ctrl+Alt+6 のみ**。画像スニップ・録画・ズーム・描画は対象外。

### 1.2 ZoomIt 実装の調査結果

microsoft/PowerToys `src/modules/ZoomIt/ZoomIt/` の実ソースを確認した。以下が「日本語環境でうまく動かない」原因の根拠である。

`ZoomItSettings.h` — 既定キーは確かに Ctrl+Alt+6:

```cpp
DWORD g_SnipOcrToggleKey = ((HOTKEYF_CONTROL | HOTKEYF_ALT) << 8) | '6';
```

`Zoomit.cpp` `OcrFromHBITMAP()`:

```cpp
// Create OCR engine from user profile languages
winrt::OcrEngine engine = winrt::OcrEngine::TryCreateFromUserProfileLanguages();
if( !engine )
{
    return std::wstring{};      // ← 無言で空文字を返すだけ
}
winrt::OcrResult result = engine.RecognizeAsync( softwareBitmap ).get();
return std::wstring( result.Text() );   // ← 後処理は一切なし
```

`Zoomit.cpp` `IDC_COPY_OCR` ハンドラ:

```cpp
StretchBlt( hSaveDc, 0, 0, copyWidth, copyHeight, hdcScreen,
            monInfo.rcMonitor.left + copyX,          // ← 現在のモニタ基準
            monInfo.rcMonitor.top  + copyY,
            copyWidth, copyHeight, SRCCOPY | CAPTUREBLT );
std::wstring ocrText = OcrFromHBITMAP( hSaveBitmap );
if( !ocrText.empty() && OpenClipboard( hWnd ) ) { ... }   // ← 空なら何も起きない
```

`SNIP_OCR_HOTKEY` ハンドラは、範囲選択の前に必ず**静的ズーム 1x に入ってから** `SelectRectangle` を出す構造で、LiveZoom + LiveDraw の組み合わせは `// Block liveZoom liveDraw snip OCR due to mirroring bug` として明示的に無効化されている。

### 1.3 ZoomIt の問題点と本アプリの対応方針

| # | ZoomIt の問題 | 影響 | 本アプリの対応 |
| --- | --- | --- | --- |
| D1 | `TryCreateFromUserProfileLanguages()` のみ。言語を明示指定できず、プロファイル言語に対応する認識エンジンが無いと `nullptr` | 検証機の `AvailableRecognizerLanguages` は **`ja` のみ**。ユーザープロファイル言語が en-US 優先だとエンジン生成に失敗し**何も起きない** | 言語を設定で明示指定＋フォールバック鎖（§6.1） |
| D2 | 失敗時に無言。クリップボードも書き換えない | ユーザーには「壊れている」としか分からない | 全失敗パターンで理由と対処をトースト表示＋ログ（§8） |
| D3 | `OcrResult.Text()` を無加工でコピー | Windows OCR は語の区切りを半角空白で連結するため、日本語が `日本 語 の テキスト` のように**空白だらけ**になる | 文字種を見た空白除去・行組み立て（§7） |
| D4 | 拡大は無く、`MaxImageDimension` 超過時に**縮小**のみ | 小さい文字（100% DPI の 9〜12pt 日本語）で認識率が大きく落ちる | 適応的アップスケール＋再試行（§6.3） |
| D5 | 取得元が `monInfo.rcMonitor` 基準で単一モニタに閉じている | マルチモニタをまたぐ選択ができない | 仮想デスクトップ全体を一括キャプチャ（§5.2） |
| D6 | 範囲選択の前に静的ズームへ入る重い経路。LiveZoom/LiveDraw と併用不可 | モード遷移由来の不安定さ | ズームを介さず、凍結スナップショット上のオーバーレイで選択（§5） |
| D7 | 暗背景（ダークモード）・縦書きへの配慮なし | 日本語 UI で頻出 | 反転・回転を含む複数試行から最良を選択（§6.5） |
| D8 | 誤認識の常用パターン（`ー`/`一` など）を直す手段がない | 毎回手直しが必要 | ユーザー定義の置換ルール（正規表現）を設定で提供（§7.4） |

### 1.4 検証機の実測値（実装時の前提）

```
Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages
  Count: 1
    ja  (日本語)
OcrEngine.MaxImageDimension: 10000
```

- **英語の認識エンジンが入っていない。** `ja` エンジンは英数字も認識するが精度は劣る。README で en-US OCR 言語機能の追加手順を案内する（§8.3）。
- ZoomIt のソースコメントは `max dimension (2600px)` と書いているが、実測は `10000`。**ハードコードせず必ず実行時に `OcrEngine.MaxImageDimension` を読む。**
- **`TryCreateFromUserProfileLanguages()` はこの PC では `ja` を正常に返す。** つまり D1（エンジン生成失敗）は**この PC では発生していない**。D1 は他環境での保険として対策するが、実際に観測された不具合は **D3（空白分断）と D4（等倍による精度低下）** の 2 つである。

### 1.5 実測: ZoomIt と同じ経路を再現した結果

Yu Gothic UI 12px（実際の画面文字とほぼ同じ大きさ）で日本語 3 行を描画した 620×90 の PNG を、
ZoomIt と同じく **等倍のまま** `OcrResult.Text` で取り出した結果:

```
入力: 画面上の文字を読み取ってクリップボードにコピーします。
出力: 画 面 上 の 文 字 を 読 み 取 っ て ク リ ) プ - ド に ] を し ま す 。
```

`OcrLine.Words` は **1 文字が 1 語**として返る（L1 は 25 words）。これが D3 の直接の証拠である。

同じ画像を倍率だけ変えて認識させた実測値（正解文字数 / 全 96 文字）:

| 倍率 | 正解文字数 | 所要 | 特徴的な誤り |
| --- | --- | --- | --- |
| 1x（ZoomIt 相当） | **69 / 96** | 49 ms | `クリ)プ-ドに]を` `言噐` `確謬` `更新三 202b` |
| **2x** | **83 / 96** | 49 ms | `コビー`（最良） |
| 3x | 80 / 96 | 54 ms | `亘面` `wndows` `2025` |
| 4x | 80 / 96 | 64 ms | `亘面` `wtndows` |

**重要な知見:**

1. 等倍では日本語として崩壊する（`開いて`→`いて`、`言語`→`言噐`、`確認`→`確謬`）。拡大は必須。
2. **倍率は大きいほど良いわけではない。2x が最良で、3x・4x はむしろ悪化する。**
   したがって「領域が小さいほど高倍率」という単純な表は誤り（初版 §6.3 の表はこれで棄却された）。
3. 1 回の認識は **50〜65ms** と安い。複数倍率を試して良い方を採る戦略は十分に実用的。

### 1.6 実測: en-US エンジンの挙動（デュアルエンジン設計の根拠）

`Language.OCR~~~en-US~0.0.1.0` 導入後、§1.5 と同じ画像を両エンジンで認識した結果。

**ja エンジン単体:**
```
画面上の文字を読み取ってクリップボードにコビーします。
Wlndowsの設定を問いて、言語オプションを確認してください。
ファイル名:「e四代-2026.x (更新日2026/09/01 )
```

**en-US エンジン単体:**
```
Windows
774JIZZ: repott_2026.xlsx ( 2026/09/01)
```

**設計上決定的な 3 つの知見:**

1. **en-US エンジンは日本語行を丸ごと捨てる。** 画像 1 行目（純日本語）に対する出力は**皆無**。
   ゴミを出すのではなく何も返さないため、マージで日本語が上書きされる危険は原則として低い。
2. **ただし常に捨てるわけではない。** 3 行目の `ファイル名:` は `774JIZZ:` という
   **ラテン文字のゴミに化ける**。ここが最大の事故ポイントであり、マージは
   「en が何か返した領域は en を採る」ではなく**必ずスコア比較が必要**。
3. **ラテン文字は en が明確に優る。** `Wlndows`→`Windows`（完全一致）、
   `e四代-2026.x`→`repott_2026.xlsx`（`repott` の 1 文字違いまで改善）。
   ただし en も完全ではない。

**結論:** 語（ラン）単位の空間マージ ＋ スコア比較という §6.4/§7 の設計は妥当。
行単位では不可（en は断片しか返さないため）。

### 1.7 初回マージ実測で判明した 2 つの欠陥

上記条件で `--lang ja,en-US` を実行した結果:

```
画面上の文字を読み取ってクリップボードにコビーします。
Windowsの設定を問いて、言語オプションを確認してください。
ファイル名:「erepott_2026.xlsxx (更新日2026/09/01 )
```

`Wlndows`→`Windows`、`四代-2026.`→`repott_2026.xlsx` の置換は成功。日本語行も保持された。
一方で次の 2 点は**要修正**。

**欠陥 M-1: 置換後に隣接ランの残骸が残る（`erepott_2026.xlsxx`）**

ja 側は `e` / `四代-2026.` / `x (` の 3 ランに分かれており、en の 1 語
`repott_2026.xlsx` はこの 3 ランすべてに空間的に重なる。しかし置換されたのは中央のランだけで、
前後の `e` と `x` が残った。

→ **候補語を「消費」として扱い、採用された候補語の矩形に実質的に覆われるベースランは
削除しなければならない。** 現状はランごとに独立判定しているため取りこぼす。

**欠陥 M-2: スコアが文字数に支配され、長いラテン文字のゴミが有利になる**

| ベース(ja) | 候補(en) | BaseScore | CandidateScore | 結果 |
| --- | --- | --- | --- | --- |
| `ファイル名:「` | `774 JIZZ:` | 7 | **8** | 置換されず（margin=2 に救われた） |

**候補のほうが高得点だった。** margin 2 で偶然守られただけで、極めて脆い。
原因は `validChars`（生の文字数）が支配項になっていること。日本語は 1 文字あたりの情報量が
多いため、文字数での単純比較は異なる字種間では成立しない。

→ **文字数をそのまま基礎点にせず、文字ごとの妥当性で重み付けする**必要がある:

| 文字種 | 重み |
| --- | --- |
| 常用漢字・かな・約物 | 1.0 |
| 英単語辞書にヒットしたトークン内のラテン文字 | 1.0 |
| 辞書にヒットしないトークン内のラテン文字 | 0.3 |
| 数字 | 0.5 |
| 常用外の CJK 統合漢字 | 減点（従来どおり） |
| 混在スクリプトトークン | 減点（従来どおり） |

この重み付けなら `774 JIZZ:`（辞書ヒット無しのラテン＋数字）は低得点、
`ファイル名:「`（かな・漢字・約物）は高得点となり、margin に頼らず正しく判定できる。

### 1.8 実測: デュアルエンジンの所要時間

同一画像・CLI（1 呼び出し＝1 プロセス）での `ElapsedMilliseconds`:

| 構成 | 所要 |
| --- | --- |
| `ja` 単体 | 540 ms |
| `en-US` 単体 | 478 ms |
| `ja` + `en-US` 統合 | 735 ms |

**単一エンジンでも約 500ms かかっている**ことから、支配項は WinRT `OcrEngine` の生成コストであり、
2 エンジン化による増分は約 200ms にとどまる。マージ処理自体のコストは無視できる。

常駐版（`TrayApp`）は `OcrService` がエンジンをキャッシュするため、この生成コストは初回のみ発生する。
2 回目以降のキャプチャは認識時間（1 エンジンあたり 50〜90ms）が支配的になる。

**したがって所要時間の受け入れ基準は「常駐時の 2 回目以降」に対して適用する。**
CLI の 1 ショット実行はエンジン生成を毎回含むため、この基準の対象外とする。

---

## 2. 用語

| 用語 | 定義 |
| --- | --- |
| 仮想デスクトップ | 全モニタを内包する矩形。`SM_XVIRTUALSCREEN` 等で取得 |
| 物理ピクセル | DPI スケール適用前の実ピクセル。本アプリの座標系はすべてこれ |
| 凍結スナップショット | ホットキー押下直後に取得した仮想デスクトップ全体のビットマップ |
| オーバーレイ | 凍結スナップショットを表示して範囲選択させる全画面ウィンドウ |
| 認識試行 (attempt) | 前処理条件を変えて OCR を 1 回実行する単位 |

---

## 3. 全体像

```
[常駐] トレイアイコン + 非表示メッセージウィンドウ
   |
   | Ctrl+Alt+6 (RegisterHotKey / WM_HOTKEY)
   v
[1] 仮想デスクトップ全体を BitBlt でキャプチャ（凍結スナップショット）
   v
[2] オーバーレイ表示（スナップショットを暗転描画、十字カーソル）
   |   ドラッグで矩形選択 / Esc・右クリックでキャンセル
   v
[3] 選択矩形をスナップショットから切り出し（再キャプチャしない）
   v
[3.5] QR コードを走査（qrCodeEnabled）
   |   読めたら [7] へ直行（OCR は行わない）／読めなければ次へ
   v
[4] 前処理（拡大・反転・回転）→ 複数試行
   v
[5] Windows.Media.Ocr で認識 → 最良の試行を選択
   v
[6] 後処理（CJK 空白除去・行組み立て・置換ルール）
   v
[7] クリップボードへ CF_UNICODETEXT で書き込み
   v
[8] トースト表示「NN 文字をコピーしました」/ 失敗理由
```

---

## 4. 起動・常駐

### 4.1 プロセス

- タスクバーに出ない常駐アプリ。UI はタスクトレイアイコンのみ。
- **多重起動禁止**: 名前付き Mutex `Global\ScreenOCR.SingleInstance`。2 個目の起動は「既に起動しています」をトースト表示して終了コード 0 で終了。
- DPI 認識: `SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)` を `Main` の最初で呼ぶ（app.manifest でも `<dpiAwareness>PerMonitorV2</dpiAwareness>` を宣言）。以降、Win32 のスクリーン座標＝物理ピクセルとして扱う。
- 範囲選択の完了後からOCR結果の通知直前まで、選択範囲の中央に非アクティブ・最前面の「OCR 処理中…」インジケーターを表示する。成功、失敗、例外のいずれでも確実に閉じる。

### 4.2 トレイメニュー

```
ScreenOCR
──────────────────
文字を読み取る (Ctrl+Alt+6)
──────────────────
OCR エンジン    ▸ ● Windows OCR
                   ○ PP-OCRv6 Small
OCR 言語        ▸ ● 日本語 (ja)
                   （自動）
                   ...インストール済み言語を列挙
QR コードを先に読み取る  ☑
後処理          ▸ ☑ 日本語の余分な空白を削除
                   ☐ 行を連結する
                   ☑ 暗背景を自動反転
                   ☐ 縦書きを試行する
──────────────────
スタートアップに登録  ☐
設定ファイルを開く
設定を再読み込み
ログフォルダーを開く
──────────────────
ScreenOCR v1.0.0
終了
```

- チェック操作は即座に `config.json` へ保存し反映する。
- 「OCR 言語」は `OcrEngine.AvailableRecognizerLanguages` を起動時に列挙して動的生成する。1 つも無い場合は無効化し、代わりに「OCR 言語機能をインストール…」を表示して `ms-settings:regionlanguage` を開く。

### 4.3 スタートアップ登録

`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` に値名 `ScreenOCR`、データ `"<exe のフルパス>"` を書く／消す。既定は未登録。

### 4.4 昇格ウィンドウについて（既知の制限）

UIPI により、非昇格プロセスのホットキーは**昇格ウィンドウがフォアグラウンドの間は届かない**。ZoomIt と同じ制限であり、回避するには本アプリ自体を管理者として実行する必要がある。README に「タスクスケジューラで最上位の特権にて実行」する手順を記載する。

---

## 5. ホットキーと範囲選択

### 5.1 ホットキー

- 既定 `Ctrl+Alt+6`（ZoomIt と同一）。`RegisterHotKey(hWnd, ID, MOD_CONTROL|MOD_ALT|MOD_NOREPEAT, 0x36)`。
- 設定 `hotkey` に `"Ctrl+Alt+6"` 形式の文字列で保持。パーサは `Ctrl` / `Alt` / `Shift` / `Win` の任意組み合わせ ＋ 末尾 1 キー（`0-9`, `A-Z`, `F1`-`F24`）を受理する。
- 登録失敗時（他アプリが占有。**本物の ZoomIt が起動中だと必ず衝突する**）: トーストで「Ctrl+Alt+6 は他のアプリが使用中です。ZoomIt が起動していないか確認するか、設定でキーを変更してください」と表示し、トレイアイコンにエラーバッジを付ける。常駐は継続する。
- OCR 処理中に再度押された場合は無視する（多重実行防止フラグ）。

### 5.2 キャプチャ（凍結スナップショット）

1. `GetSystemMetrics(SM_XVIRTUALSCREEN / SM_YVIRTUALSCREEN / SM_CXVIRTUALSCREEN / SM_CYVIRTUALSCREEN)` で仮想デスクトップ矩形を取得。
2. `GetDC(NULL)` → `CreateCompatibleBitmap` → `BitBlt(..., SRCCOPY | CAPTUREBLT)` で一括取得。
3. カーソルは含めない（`BitBlt` は既定でカーソルを含まない）。
4. 以降のすべての切り出しはこのスナップショットから行う。オーバーレイ表示中に背後の画面が変化しても結果は変わらない（ZoomIt の再キャプチャ由来のずれを回避）。

> 実装メモ: DRM 保護コンテンツや一部のハードウェアオーバーレイは黒くなり得る。これは `BitBlt` の制約であり ZoomIt も同様。将来 `Windows.Graphics.Capture` への差し替えを検討（§11.4 P2）。

### 5.3 オーバーレイ（範囲選択 UI）

- 仮想デスクトップ全体を覆う 1 枚のボーダレス最前面ウィンドウ。`WS_POPUP`, `WS_EX_TOPMOST | WS_EX_TOOLWINDOW`、タスクバーに出さない。
- 表示内容: 凍結スナップショットを等倍描画し、その上に黒 35% の暗幕を重ねる。
- 選択中の矩形内は暗幕を抜いて元の明るさで見せ、1px の枠線（`#0078D4`）を描く。
- カーソルは `IDC_CROSS`。
- カーソル近傍に選択サイズを `W × H`（物理 px）で表示。画面端では反対側に回り込む。
- 表示直後に `SetForegroundWindow` でフォーカスを取る（ホットキー起動なのでフォアグラウンド権限が得られる）。

#### 操作

| 操作 | 動作 |
| --- | --- |
| 左ボタン押下 → ドラッグ → 離す | 矩形確定。OCR 開始 |
| Esc | キャンセル。クリップボードは変更しない |
| 右クリック | キャンセル（ZoomIt と同じ） |
| Ctrl+Alt+6 再押下 | キャンセル |
| フォーカスを失う (`WM_KILLFOCUS`) | キャンセル |
| ドラッグ距離が 5px 未満 | 「範囲が小さすぎます」トーストを出してキャンセル |
| Ctrl+A | マウスのあるモニタ全体を選択して確定 |

- 選択矩形は仮想デスクトップ座標（物理 px）で正規化して保持する（左上・右下の順序を問わない）。
- 確定後、オーバーレイは**即座に隠す**（OCR 中に画面を塞がない）。

#### 混在 DPI について

プロセスは PerMonitorV2 のため、ウィンドウ座標系＝物理ピクセルで一貫する。オーバーレイは仮想デスクトップ全域を 1 ウィンドウで覆い、スナップショットを 1:1 で描画するため、モニタ間で DPI が異なっても選択座標はずれない。サイズ表示のフォントのみ、カーソルのあるモニタの DPI に合わせて `GetDpiForWindow` 基準で拡縮する。

---

## 6. OCR

### 6.1 エンジン生成（D1 への対応）

`OcrService.CreateEngine()` は次の順で試し、最初に成功したものを使う。**ZoomIt と違い、必ず「使えるエンジン」に到達する。**

1. 設定 `ocrLanguage` が具体的な BCP-47 タグ（例 `"ja"`, `"en-US"`）なら `OcrEngine.TryCreateFromLanguage(new Language(tag))`
2. `"auto"` の場合:
   1. `OcrEngine.TryCreateFromUserProfileLanguages()`（ZoomIt と同じ挙動）
   2. `AvailableRecognizerLanguages` に `ja` で始まるものがあれば、それ
   3. `AvailableRecognizerLanguages` の先頭
3. すべて失敗（`AvailableRecognizerLanguages` が空）→ `NoRecognizerAvailable` エラー（§8.2 E1）

生成したエンジンと言語タグはキャッシュし、設定変更時に破棄する。

### 6.2 SoftwareBitmap への変換

`System.Drawing.Bitmap`（32bpp ARGB）→ `SoftwareBitmap.CreateCopyFromBuffer(buffer, BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied)`。ZoomIt のような WIC / `ISoftwareBitmapNativeFactory` 経由の相互運用は不要。`OcrEngine` が要求する形式は `Gray8` または `Bgra8`。アルファは常に不透明（255）で埋める。

### 6.3 前処理とスケール決定（D4 への対応）

§1.5 の実測により、**倍率は固定表で決めず、複数倍率を実際に試して品質スコアで選ぶ**。

候補倍率は次のとおり。長辺 `L`（物理 px）で開始点だけを変える:

| L | 試す倍率（この順） |
| --- | --- |
| L < 1400 | **2.0** → 3.0 → 4.0 |
| L ≥ 1400 | **1.0** → 2.0 |

- **2.0 を最初に試す**（実測の最良値）。1 回 50〜65ms なので追加試行のコストは小さい。
- 拡大は高品質補間（`InterpolationMode.HighQualityBicubic`, `PixelOffsetMode.HighQuality`）で行う。
- 拡大後の長辺が `OcrEngine.MaxImageDimension` を超える倍率は候補から除外する。定数ではなく実行時の値を使う。
- 元画像が既に高 DPI（`GetDpiForMonitor` が 150% 超）の場合、候補リストを 1 段階ずつ下げる。

### 6.4 品質スコア（倍率・試行の選択基準）

**単純な文字数では 2x と 3x を区別できない**（§1.5 では 83 文字と 80 文字でほぼ同数だが内容の正しさが違う）。
そこで「日本語として出現しにくい文字が多い結果は誤認識」という前提でスコアを定義する。

```
score = validChars - 4 * implausibleChars
```

- `validChars` = 空白・制御文字を除いた文字数
- `implausibleChars` = CJK 統合漢字（`U+3400`–`U+4DBF`, `U+4E00`–`U+9FFF`）のうち、
  **常用漢字表（2136 字）に含まれないもの**の個数。ひらがな・カタカナ・ASCII・約物・全角形は常に妥当とみなす。
- 常用漢字表は `src\ScreenOCR\Resources\joyo-kanji.txt`（UTF-8, 2136 字）として埋め込みリソースで持つ。

§1.5 の実測データに対してこのスコアが正しく働くこと（1x の `噐` `謬`、3x/4x の `亘` が減点され、
2x が最高得点になること）を単体テストで固定する（§12.1 T13）。

### 6.5 認識試行と最良選択（D7 への対応）

試行は**早期打ち切り付きで最大 5 回**、合計 2,000ms を上限とする。

| 順 | 条件 | 内容 |
| --- | --- | --- |
| A | 常に | 候補倍率の 1 番目（通常 2.0 倍）・元の色 |
| B | A の `implausibleChars > 0` または `validChars < 8` | 候補倍率の 2 番目 |
| C | B も条件を満たしたまま、かつ候補が残る | 候補倍率の 3 番目 |
| D | 設定 `autoInvert` が ON かつ切り出し画像の平均輝度 < 0.40 | A と同じ倍率で色反転 |
| E | 設定 `tryVertical` が ON、または A〜D の最良 `validChars < 3` | 画像を時計回り 90° 回転（縦書き対策） |

- **早期打ち切り**: A が `validChars >= 8` かつ `implausibleChars == 0` なら B・C・E は実行しない
  （D は条件を満たせば実行する）。
- 最終的に **score 最大**の試行を採用。同点なら試行順の早い方。
- 各試行は `OcrEngine.RecognizeAsync` を `await` する。UI スレッドはブロックしない。
- 全試行が 0 文字 → `NoTextFound` エラー（§8.2 E2）。
- 採用した倍率と score はログに残す（§8.4）。

### 6.6 結果の取り出し

`OcrResult.Text` は使わない（空白の入り方を制御できないため）。`OcrResult.Lines` → 各 `OcrLine.Words`（`OcrWord.Text`, `OcrWord.BoundingRect`）を後処理に渡す。試行 D（回転）を採用した場合、`BoundingRect` は回転後の座標系である点に注意して並べ替える。

### 6.7 PP-OCRv6 Small バックエンド

`ocrEngine: "ppocrv6-small"` の場合は RapidOCR 3.9 以上を別プロセスで実行し、ONNX Runtime の PP-OCRv6 Small 検出・認識モデルを明示的に選択する。埋め込みPythonブリッジを一時ディレクトリへ展開し、入力PNGと出力JSONを介して `text`、`confidence`、`boundingBox` を共通 DTO へ変換する。

- `ppOcrV6SmallPythonPath` で Python を指定し、RapidOCR 3.9 以上の有無を検出する。
- タイムアウト、終了コード、JSON欠落/不正を区別して通知し、一時ファイルは削除する。
- 日本語・英語を含む統一認識モデルを使い、言語選択とデュアル言語統合は無効にする。

#### 6.7.1 狭い範囲を選んだときの取りこぼし対策

PP-OCR の検出（DB）は文字の輪郭を閉じられないと領域を出せないため、**文字が切り抜きの縁に接していると検出が空になる**。英単語 1 語だけをぴったり囲むような選択でこれが起きる。ブリッジは検出へ渡す前に次を行う。

1. **拡大**: 高さが 48 px 未満なら 48 px になるよう `INTER_CUBIC` で拡大する（上限は 8 倍かつ長辺 1600 px）。
2. **余白**: 縁 1 px の画素の中央値を背景色とし、短辺の 40 %（16〜64 px にクランプ）の余白を四辺へ足す。`BORDER_REPLICATE` は縁に接した文字を引き伸ばして偽の線を作るため使わない。
3. **座標の復元**: 検出座標から余白を引き、拡大率で割って元の切り抜きの座標系へ戻す。値は切り抜きの範囲へクランプする。
4. **二重検出の除去**: 余白を足すと稀に同じ行が「日」と「日本語」のように二重に検出される。短い方の文字列が長い方に含まれ、横が少しでも重なり、縦の重なりが短い方の高さの 70 % 以上（＝同じ行）であれば短い方を捨てる。同じ行に離れて現れる同じ語は横が重ならないため残る。

それでも検出が空だった場合は**認識モデルだけを一段落としで実行する**（RapidOCR の `use_det=False`）。切り抜き全体を 1 行の文字列として認識し、信頼度が 0.5 以上のものだけを採用する。このとき渡すのは拡大も余白も付けない元の切り抜き（認識モデルは行を切り出し済みの画像を前提とするため）で、`boundingBox` は切り抜き全体とする。

JSON には `mode`（`"det"` / `"rec-only"`）と `scale` を含める。C# 側は `mode` に応じて試行名を `PPv6` / `PPv6-rec` とし、`scale` を採用倍率としてログ（§8.4）へ残す。

実測（Segoe UI / メイリオ、12〜40 px の「Hello」「日本語」を余白 0〜2 px で切り抜いた 38 件）では、対策前は 18 件で検出が空だったが、拡大＋余白で 0 件になった。

### 6.8 QR コードの読み取り（OCR の前段。`qrCodeEnabled`、既定 ON）

画面の QR コードは、OCR に通しても模様として扱われ意味のある文字列にならない。選択範囲を OCR へ渡す**前**に QR として読み、成功したらその内容をクリップボードへ入れて OCR は行わない（§3 の [3.5]）。専用のホットキーもモード切り替えも設けず、`Ctrl+Alt+6` の操作は 1 つのままにする。**読めなければ何も通知せず、そのまま従来どおり OCR へ進む**（QR を含まない範囲を選ぶ通常の使い方に一切影響を与えないため）。

デコードは ZXing.Net（Apache-2.0、`ZXing.Net` パッケージ）で行う。ローカル完結であり、画像も結果も外部へ送信しない。OCR と違い成否は「読めた／読めなかった」の二値なので、§6.4 のようなスコアリングはせず、最初に成功した試行の結果をそのまま採る。

| 試行名 | 内容 | 目的 |
| --- | --- | --- |
| `QR` | 等倍 | 通常の QR |
| `QR-inv` | 白黒反転 | ダークテーマの画面に描かれた反転 QR |
| `QR-2x` | 2 倍拡大 | 1 モジュールが 1〜2 px しかない小さな QR。長辺 700 px 未満の選択でのみ試す（広い範囲での無駄な再走査を避ける） |

- `BarcodeFormat.QR_CODE` のみを対象とし、`TryHarder` と `AutoRotate` を有効にする（傾き・回転のある QR を読むため）。
- 1 回の選択に複数の QR があれば全部読み、検出順に改行で連結する。同じ文字列は 1 件にまとめる（`DecodeMultiple` が回転違いで同じシンボルを 2 回返すことがあるため）。
- **Kanji モード（Shift_JIS）と ECI 指定の QR を読むには `CodePagesEncodingProvider` の登録が要る**。.NET では既定で登録されていないため、最初の走査前に一度だけ `Encoding.RegisterProvider` を呼ぶ。
- 輝度源は `RGBLuminanceSource` の `BGR32`（アルファを読まない形式）とする。`BitBlt` 由来のビットマップはアルファが 0 のことがあり、アルファを見る `BGRA32` では全面が透明と解釈されて読めなくなる。
- バウンディングボックスは ZXing が返す位置決めパターンの中心から作るため、シンボル外周より一回り内側になる（`--json` とログ用で、切り出しには使わない）。
- 走査で例外が出た場合は警告をログへ残し、QR を諦めて OCR を続行する（QR の失敗で OCR まで止めない）。

**URL を自動で開くことはしない。** 開くのは利用者が成功通知をクリックしたときだけで、その導線を出すのも `http` / `https` の 1 件だけのときに限る（§8.2 の S2）。QR の内容は撮影元を信用できないため、スキャンだけで遷移が起きる作りにはしない。

---

## 7. テキスト後処理（D3 への対応。本アプリの中核）

入力は `IReadOnlyList<OcrLine>`。すべて `TextPostProcessor` に集約し、UI 非依存の純粋関数として実装する（＝単体テスト対象）。

### 7.1 行内の語連結（既定 ON: `removeCjkSpaces`）

隣接する 2 語 `prev` / `next` の間に半角空白を入れるかを次で決める。

1. **字種判定**: 語の末尾文字／先頭文字が CJK かどうかを Unicode 範囲で判定する。CJK とみなす範囲:
   - `U+3000`–`U+303F` 記号・句読点
   - `U+3040`–`U+309F` ひらがな
   - `U+30A0`–`U+30FF` カタカナ
   - `U+31F0`–`U+31FF` カタカナ拡張
   - `U+3400`–`U+4DBF`, `U+4E00`–`U+9FFF` 漢字
   - `U+F900`–`U+FAFF` 互換漢字
   - `U+FF00`–`U+FF60`, `U+FFE0`–`U+FFE6` 全角形
   - `U+FF61`–`U+FF9F` 半角カナ
2. `prev` 末尾・`next` 先頭の**どちらかが CJK なら空白を入れない**。
3. 両方が非 CJK（ラテン文字・数字・記号）なら**語間ギャップで判定**する。`gap = next.Left - (prev.Left + prev.Width)`、`refW = 行内の語の平均文字幅`（＝ Σ(word.Width) / Σ(word.Text.Length)）とし、`gap > refW * 0.35` なら空白を入れる。`refW` が算出できない場合は無条件に空白を入れる（従来動作）。
4. `removeCjkSpaces` が OFF のときは、`OcrResult.Text` と同じく常に半角空白 1 個で連結する（**ZoomIt 互換動作**）。

### 7.2 行の並べ替え（既定 ON: `sortLines`）

`OcrLine` を各行の外接矩形（語の `BoundingRect` の和）から `top` の昇順、同一行帯（`|top 差| < 行高の 0.5`）内では `left` の昇順に**安定ソート**する。エンジンの返す順序が段組みで乱れるケースを吸収する。OFF ならエンジン順のまま。

### 7.3 行の連結（既定 OFF: `joinLines`）

ZoomIt 互換は「画面の行＝出力の行」。ON の場合のみ次で連結する:

- 直前行の末尾が `。．！？!?」』）)：:；;・…` のいずれか → 連結しない（改行を保つ）
- 直前行の末尾と次行の先頭が**ともに CJK** → 空白なしで連結
- どちらかが非 CJK → 半角空白 1 個を挟んで連結
- 空行は常に段落境界として保持

### 7.4 ユーザー定義の置換ルール（D8 への対応。`replacements`）

`config.json` に順序付きで保持し、最後に適用する。

```jsonc
"replacements": [
  { "pattern": "(?<=[ぁ-んァ-ヶ])一(?=[ぁ-んァ-ヶ])", "replacement": "ー", "regex": true, "enabled": true },
  { "pattern": "\u00A0", "replacement": " ", "regex": false, "enabled": true }
]
```

- `regex: true` は .NET 正規表現。`RegexOptions.None`、タイムアウト 200ms。
- 不正な正規表現はロード時に検出し、警告ログを出して**そのルールのみ無効化**する（アプリは落とさない）。
- 既定では**空配列**（推測による自動書き換えはしない）。README に上記の例を「よく使う設定」として載せる。

### 7.5 仕上げ

1. 各行の行末空白を除去。
2. 連続する半角空白 2 個以上を 1 個に圧縮（`collapseSpaces`、既定 ON）。
3. 先頭・末尾の空行を除去。
4. 改行コードは **CRLF**（`\r\n`）に統一する。
5. `normalizeFullWidth`（既定 **OFF**）が ON の場合のみ、全角英数字・全角空白を半角へ。丸数字などの互換分解は行わない（NFKC は使わない）。
6. `halfToFullKana`（既定 **OFF**）が ON の場合のみ、半角カナを全角カナへ（濁点・半濁点の合成を含む）。

---

## 8. クリップボードとフィードバック

### 8.1 クリップボード書き込み

- 形式は `CF_UNICODETEXT` のみ（ZoomIt と同じ）。
- 他プロセスがクリップボードを開いていて失敗することがあるため、`OpenClipboard` を **100ms 間隔で最大 10 回**リトライする。全滅なら E3。
- `copyImageToo`（既定 OFF）が ON の場合のみ、`CF_DIB` も併せて設定する。
- **失敗時は既存のクリップボード内容を壊さない**（`EmptyClipboard` は書き込み直前にのみ呼ぶ）。

### 8.2 結果通知（D2 への対応）

通知はトースト API ではなく**自前のレイヤードウィンドウ**で出す（集中モードや通知設定で握り潰されないようにするため。ZoomIt が無言なことへの直接の対策）。

- 表示位置: 選択矩形の右下外側。画面外にはみ出す場合はマウスのあるモニタの右下。
- 表示時間: 成功 1.2 秒、失敗 4.0 秒。フェードアウト 200ms。クリックで即閉じる。

| ID | 状況 | メッセージ |
| --- | --- | --- |
| S1 | 成功 | `NN 文字をコピーしました`（サブ行に `ja / 3.0x` のように言語と採用倍率） |
| S2 | QR 成功 | `QR コードを読み取りました`（サブ行は `NN 文字をコピーしました`、複数なら `N 件 / NN 文字をコピーしました`。内容が `http`/`https` の 1 件だけなら `クリックで開く: <ホスト名>` とし、クリックで既定のブラウザーを開く） |
| E1 | 認識エンジンが 1 つも無い | `OCR 言語がインストールされていません` / `設定 > 時刻と言語 > 言語と地域 > 日本語 > 言語オプション > 光学式文字認識 を追加してください`（クリックで `ms-settings:regionlanguage` を開く） |
| E2 | 文字が見つからない | `文字を認識できませんでした` / `範囲を広げるか、拡大表示してから再試行してください` |
| E3 | クリップボードを開けない | `クリップボードを開けませんでした` / `他のアプリが使用中の可能性があります` |
| E4 | キャプチャ失敗 | `画面をキャプチャできませんでした` |
| E5 | ホットキー登録失敗 | §5.1 のメッセージ |
| E6 | 範囲が小さすぎる | `範囲が小さすぎます` |

### 8.3 言語機能の追加案内（README にも記載）

```
設定 > 時刻と言語 > 言語と地域 > （日本語）… > 言語オプション
  > オプションの言語機能 > 機能を追加する > 光学式文字認識
```

英数字混在の精度を上げたい場合は English (United States) も同様に追加することを推奨する（検証機には `ja` しか入っていない）。

### 8.4 ログ

- 出力先: `%APPDATA%\ScreenOCR\logs\screenocr-YYYYMMDD.log`（UTF-8、7 日でローテーション削除）。
- 記録内容: 起動／終了、ホットキー登録結果、利用可能言語一覧、1 回の OCR ごとに `選択矩形 / スケール / 試行ごとの文字数 / 採用試行 / 所要 ms / 最終文字数`。
- QR を読めたときは `選択矩形 / 試行名 / 件数 / 所要 ms / 文字数` を記録する。読めなかったときは何も記録しない（通常の OCR と区別がつかず、ログが膨らむだけのため）。
- **認識テキスト本文は既定で記録しない**（画面内容が機微な場合があるため）。QR のデコード結果も同様に記録しない（URL や個人情報のことがあるため）。
- `debugSaveImages`（既定 OFF）が ON のとき、前処理後の PNG を `%APPDATA%\ScreenOCR\debug\` に `yyyyMMdd-HHmmss-<attempt>.png` で保存する。認識不良の切り分け用。

---

## 9. 設定

### 9.1 ファイル

`%APPDATA%\ScreenOCR\config.json`（UTF-8, BOM なし）。存在しなければ既定値で生成する。不正な JSON の場合は既定値で起動し、警告トースト＋ログを出す（起動は妨げない）。

### 9.2 既定値

```jsonc
{
  "hotkey": "Ctrl+Alt+6",
  "ocrEngine": "ppocrv6-small", // "ppocrv6-small" | "windows"
  "ppOcrV6SmallPythonPath": "python",
  "ppOcrV6SmallTimeoutMs": 120000,
  "ocrLanguage": "auto",          // "auto" | BCP-47 タグ（例 "ja"）

  "qrCodeEnabled": true,          // OCR の前に QR コードを走査する（§6.8）

  "overlayDimPercent": 35,
  "selectionBorderColor": "#0078D4",
  "minSelectionSize": 5,

  "autoInvert": true,             // 暗背景を自動反転して再試行
  "tryVertical": false,           // 縦書き（90度回転）を常に試行
  "maxScale": 4.0,
  "attemptTimeoutMs": 2000,

  "removeCjkSpaces": true,        // ★日本語の余分な空白を削除
  "sortLines": true,
  "joinLines": false,
  "collapseSpaces": true,
  "normalizeFullWidth": false,
  "halfToFullKana": false,
  "replacements": [],

  "copyImageToo": false,
  "runAtStartup": false,
  "debugSaveImages": false,
  "logRetentionDays": 7
}
```

### 9.3 反映

- トレイメニューからの変更は即時保存・即時反映。
- 「設定を再読み込み」でファイルを読み直す。`hotkey` が変わった場合は `UnregisterHotKey` → `RegisterHotKey` をやり直す。
- 未知のキーは保持したまま書き戻す（前方互換）。

---

## 10. CLI モード（テスト・自動化用）

GUI を出さずに同じパイプラインを実行できるようにする。**実装の検証を GUI 操作なしで行うための必須要件。**

| コマンド | 動作 |
| --- | --- |
| `ScreenOCR.exe --list-engines` | Windows OCR と PP-OCRv6 Small の検出状態を表示 |
| `ScreenOCR.exe --list-languages` | `AvailableRecognizerLanguages` と `MaxImageDimension` を標準出力に出して終了 |
| `ScreenOCR.exe --ocr-file <path> [--engine windows\|ppocrv6-small] [--ppocr-python <path>] [--lang ja] [--raw] [--json]` | 選択したエンジンと§7のパイプラインを適用し、結果を標準出力へ。`--raw` は後処理なし。`--json` は試行ごとのスコア等を含む JSON |
| `ScreenOCR.exe --region x,y,w,h [--lang ja] [--json]` | 画面座標を直接指定して OCR。結果を標準出力へ（クリップボードには書かない） |
| `ScreenOCR.exe (--ocr-file <path> \| --region x,y,w,h) --qr [--json]` | §6.8 の QR 走査だけを行う。`--json` は試行名・件数・所要 ms・各コードのバウンディングボックスを含む |
| `ScreenOCR.exe --version` | バージョン |

- CLI モードは Mutex を取らず、トレイ常駐と同時に使える。
- 終了コード: `0` 成功 / `1` 文字なし / `2` エンジンなし / `3` 引数エラー / `4` その他。QR が見つからなかった場合は `1`。
- **GUI と違い、`--qr` を付けないかぎり CLI は QR を走査しない**（`--ocr-file` の出力が入力画像によって OCR 結果と QR の内容のどちらかに変わると、自動化の出力が読めなくなるため）。`--qr` は OCR を一切走らせないので、`--lang` / `--engine` / `--ppocr-python` / `--raw` / `--no-merge` との併用は黙って無視せず引数エラー（`3`）にする。
- 標準出力のエンコーディングは UTF-8 に設定する（`Console.OutputEncoding`）。

---

## 11. 実装方針

### 11.1 技術選定

- **C# / .NET 10**（検証機に SDK 10.0.400 導入済み）
- TFM: `net10.0-windows10.0.26100.0`（`Windows.Media.Ocr` の WinRT projection を利用。`Microsoft.Windows.SDK.NET.Ref` は NuGet から自動復元される。nuget.org への到達は確認済み）
- `UseWindowsForms=true`（`NotifyIcon` / `Form` / `System.Drawing` を使う）
- QR デコードのみ `ZXing.Net`（Apache-2.0）を NuGet から使う。アプリ本体で唯一の実行時パッケージ依存。ローカル完結で外部送信は無く、Python / RapidOCR の有無に関係なく動く。自前実装は有限体演算・Reed-Solomon・4 モードのデコードと Kanji モードまで必要になるため採らない
- `<ApplicationManifest>` で PerMonitorV2 と `requestedExecutionLevel level="asInvoker"` を宣言
- 発行: `dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true`（配布用に `--self-contained true` も併記）

### 11.2 プロジェクト構成

```
D:\ScreenOCR\
  SPEC.md
  README.md
  .gitignore
  ScreenOCR.sln
  src\ScreenOCR\
    ScreenOCR.csproj
    app.manifest
    Program.cs             エントリ・CLI 分岐・単一インスタンス・DPI 設定
    TrayApp.cs             NotifyIcon・メニュー・メッセージウィンドウ・全体フロー
    HotKey.cs              RegisterHotKey ラッパ／"Ctrl+Alt+6" パーサ
    ScreenCapture.cs       仮想デスクトップ BitBlt・矩形切り出し
    SelectionOverlay.cs    オーバーレイ Form・ドラッグ選択・描画
    ImagePreprocessor.cs   スケール決定・拡大・反転・回転・平均輝度
    OcrService.cs          エンジン生成・SoftwareBitmap 変換・試行制御・スコアリング
    QrCodeReader.cs        §6.8 QR 走査（ZXing.Net。等倍・反転・2 倍拡大）
    TextPostProcessor.cs   §7 全体（純粋関数）
    ClipboardWriter.cs     CF_UNICODETEXT 書き込み・リトライ
    Toast.cs               自前トーストウィンドウ
    AppConfig.cs           JSON 設定の読み書き・既定値・置換ルール検証
    Startup.cs             Run キー登録／解除
    Logger.cs              ファイルログ・ローテーション
    Native.cs              P/Invoke 宣言の集約
  tests\ScreenOCR.Tests\
    ScreenOCR.Tests.csproj (xUnit)
    TextPostProcessorTests.cs
    HotKeyParserTests.cs
    ImagePreprocessorTests.cs
    QrCodeReaderTests.cs
    AppConfigTests.cs
  tests\fixtures\            qr-*.png（generate-qr-samples.py で生成、コミット済み）
  tests\generate-qr-samples.py
```

`TextPostProcessor` は WinRT 型に直接依存させず、`OcrWordInfo(string Text, RectF Bounds)` / `OcrLineInfo(IReadOnlyList<OcrWordInfo> Words)` という自前の DTO を入力にする。`OcrService` が WinRT の `OcrResult` を DTO へ詰め替える。これによりテストプロジェクトから OCR エンジン無しで後処理を検証できる。

### 11.3 スレッド

- ホットキー受信・オーバーレイ・トーストは UI スレッド。
- キャプチャ〜OCR〜後処理は `Task.Run` のバックグラウンド。
- `RecognizeAsync` は `await` する（ZoomIt が `.get()` のために MTA ワーカースレッドを起こしている問題は、C# の `async`/`await` では発生しない）。
- クリップボード書き込みは UI スレッド（STA）へ戻して実行する。

### 11.4 優先度

| 優先 | 内容 |
| --- | --- |
| **P0（必須）** | §4 常駐・§5 ホットキーと選択・§6.1 エンジン生成とフォールバック・§6.2/6.3 変換と拡大・§6.4 品質スコア・§7.1/7.2/7.5 後処理・§8.1/8.2 クリップボードと通知・§9 設定・§10 CLI |
| **P1（推奨）** | §6.5 試行 D/E（反転・縦書き）・§7.3 行連結・§7.4 置換ルール・§8.4 ログとデバッグ画像・§4.3 スタートアップ登録 |
| **P2（将来）** | `Windows.Graphics.Capture` への差し替え・クリックでウィンドウ全体を選択・選択時のルーペ・認識履歴・Tesseract 等の代替エンジン |

---

## 12. 受け入れ基準

### 12.1 単体テスト（`dotnet test` で自動検証）

`TextPostProcessorTests`（OCR 実行を伴わない。DTO を入力にする）:

| # | 入力（語の並び） | 期待出力 |
| --- | --- | --- |
| T1 | `日本` `語` `の` `テキスト` | `日本語のテキスト` |
| T2 | `Hello` `world`（ギャップ大） | `Hello world` |
| T3 | `設定` `ファイル` `を` `開く` | `設定ファイルを開く` |
| T4 | `Windows` `の` `設定` | `Windowsの設定`（CJK 隣接側は空白なし） |
| T5 | `ID` `:` `12345`（ギャップ小） | `ID:12345` |
| T6 | 2 行 `これは` / `テストです`、`joinLines: false` | `これは\r\nテストです` |
| T7 | 同上、`joinLines: true` | `これはテストです` |
| T8 | 1 行目末尾が `。`、`joinLines: true` | 連結しない |
| T9 | `removeCjkSpaces: false` | 全語が半角空白連結（ZoomIt 互換） |
| T10 | 段組みで `top` が前後した行、`sortLines: true` | 上から順に並ぶ |
| T11 | 不正な正規表現を含む `replacements` | 例外を投げず、当該ルールのみ無効 |
| T12 | 任意の入力 | 出力の改行は常に `\r\n` |

`HotKeyParserTests`: `"Ctrl+Alt+6"`, `"Ctrl+Shift+F9"`, `"Win+Alt+A"` の正常系、`"Ctrl+"`, `""`, `"Foo+1"` の異常系。

`ImagePreprocessorTests`: 候補倍率リストの生成（長辺 < 1400 は `[2.0, 3.0, 4.0]`、長辺 >= 1400 は `[1.0, 2.0]`）、`MaxImageDimension` を超える倍率が候補から除外されること、高 DPI 時に候補が 1 段階下がること、平均輝度計算。**旧版の固定倍率表（長辺 200/500/1000/2000 → s0 = 4/3/2/1）は §1.5 の実測により棄却済みなので、これをテストしてはならない。**

`PpOcrV6SmallServiceTests`: RapidOCRブリッジJSONの行テキスト・4点バウンディングボックスを共通DTOへ変換できること、空結果と不正JSONを区別すること。

`QrCodeReaderTests`: `tests/fixtures/qr-*.png` を読み直す。固定画像は **OpenCV の QR エンコーダー**（`tests/generate-qr-samples.py`）で作り、デコード側（ZXing.Net）と別実装にする。ZXing で書いて ZXing で読む自己完結テストは、エンコード側と同じ思い違いを検出できないため採らない。項目は、ASCII の URL、UTF-8 バイトモードの日本語、**Kanji モード（Shift_JIS）**、白黒反転（試行名が `QR-inv` になること）、1 モジュール 2 px の小さな QR、1 枚に 2 個の QR（改行で連結されること）、バウンディングボックスが元画像の内側に収まること、QR の無い文字画像で例外にも誤検出にもならないこと、`http`/`https` 以外（`mailto:`、`file:`、`javascript:`、スキーム無し）では URL を開く導線を出さないこと。

### 12.2 手動／CLI による検証

| # | 手順 | 期待結果 |
| --- | --- | --- |
| M1 | `ScreenOCR.exe --list-languages` | `ja (日本語)` と `MaxImageDimension: 10000` が出る |
| M2 | メモ帳に MS UI Gothic 9pt で日本語 3 行を表示 → Ctrl+Alt+6 → 範囲選択 | **空白の入らない**日本語 3 行がクリップボードに入り、成功トーストが出る |
| M3 | 同じ画像を `--ocr-file ... --raw` で CLI 実行 | 語間に空白が入った ZoomIt 相当の出力になり、M2 との差が確認できる |
| M4 | ダークモードのウィンドウを選択 | 反転試行が働き認識できる（ログに採用試行が記録される） |
| M5 | 100% と 150% の 2 モニタにまたがる範囲を選択 | 選択どおりの領域が切り出される（ZoomIt では不可） |
| M6 | Esc / 右クリックでキャンセル | クリップボードの既存内容が保持される |
| M7 | 文字のない無地の領域を選択 | E2 のトーストが出て、クリップボードは変更されない |
| M8 | 本物の ZoomIt を起動した状態で本アプリを起動 | E5 のトーストが出て、常駐は継続する |
| M9 | 設定で `hotkey` を `Ctrl+Alt+7` に変更 → 再読み込み | 新しいキーで動作し、旧キーは反応しない |
| M10 | 4K モニタで 10pt 相当の小さい文字を選択 | 拡大が効いて実用的な精度で認識される |
| M11 | 二重起動 | 2 個目は通知して終了、1 個目は影響を受けない |
| M12 | RapidOCR導入後に `--list-engines`、続けて日英混在画像を `--engine ppocrv6-small` で認識 | rapidocr 3.9 以上が available と表示され、日英テキストと座標が得られる |
| M13 | `ppocrv6-small` で英単語 1 語・日本語 2〜3 文字を余白なしでぴったり囲んで選択（§6.7.1） | 認識され、ログの試行名が `PPv6`（検出成功）または `PPv6-rec`（一段落とし）になる |
| M14 | 画面に URL の QR コードを表示して Ctrl+Alt+6 で囲む | S2 のトーストが出て URL がクリップボードに入る。**クリックするまでブラウザーは開かない**。続けて QR の無い文字を選ぶと、通知なしで従来どおり OCR される |

---

## 13. 非対象

- ZoomIt のズーム・描画・録画・ブレイクタイマー・DemoType・パノラマの各機能
- OCR 結果の翻訳・要約などの後段処理
- クラウド OCR / 外部 API への送信（**画面内容は一切外部送信しない**）
- Windows 10 未満のサポート。`win-x64` を主対象とし、`win-arm64` はビルド確認のみ
