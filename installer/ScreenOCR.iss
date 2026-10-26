; ScreenOCR のインストーラー（Inno Setup 7）。
;
; 直接コンパイルせず、scripts\build-installer.ps1 から作る。スクリプトが .NET ランタイム同梱
; （self-contained）の発行物を artifacts\publish\ScreenOCR に作り、ランタイムのライセンス文書を
; 添えてから、PublishDir / OutputDir を渡してこのファイルをコンパイルする。
;
; 設計:
; - 現在のユーザーにだけ入れる（管理者権限なし）。アプリはスタートアップ登録を HKCU の Run キーに、
;   設定とログを %APPDATA%\ScreenOCR に置くユーザー単位の作りなので、それに合わせる。
; - ランタイムは同梱するので、配布先に .NET を入れてもらう必要はない。
; - PP-OCRv6 Small（Python + RapidOCR）は同梱しない。任意のタスクで導入スクリプトを実行する。

#ifndef PublishDir
  #define PublishDir AddBackslash(SourcePath) + "..\artifacts\publish\ScreenOCR"
#endif
#ifndef OutputDir
  #define OutputDir AddBackslash(SourcePath) + "..\artifacts\installer"
#endif

#define AppName "ScreenOCR"
#define AppExe "ScreenOCR.exe"

#if !FileExists(PublishDir + "\" + AppExe)
  #error 発行物がありません。scripts\build-installer.ps1 から実行してください。
#endif
#if !FileExists(PublishDir + "\licenses\dotnet-LICENSE.txt")
  #error .NET ランタイムのライセンス文書がありません。scripts\build-installer.ps1 から実行してください。
#endif

; バージョンは発行した EXE から読む（csproj の <Version> が唯一の出どころ）。
#define VerMajor
#define VerMinor
#define VerRev
#define VerBuild
#expr GetVersionComponents(PublishDir + "\" + AppExe, VerMajor, VerMinor, VerRev, VerBuild)
#define AppVersion Str(VerMajor) + "." + Str(VerMinor) + "." + Str(VerRev)

[Setup]
; アップグレードとアンインストールはこの ID で同じアプリと判定される。変えないこと。
AppId={{F654961A-F15D-438F-B24C-C53CA04809F8}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=ytmknd
AppPublisherURL=https://github.com/ytmknd/ScreenOCR
AppSupportURL=https://github.com/ytmknd/ScreenOCR
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; README の対象どおり Windows 11 以降。
MinVersion=10.0.22000
WizardStyle=modern dynamic
SetupIconFile=..\src\ScreenOCR\Resources\ScreenOCR.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
; 常駐中は上書きもアンインストールもさせない。アプリは起動時に Global\ のミューテックスを作る。
AppMutex=ScreenOCR.SingleInstance,Global\ScreenOCR.SingleInstance
Compression=lzma2/ultra64
SolidCompression=yes
OutputDir={#OutputDir}
OutputBaseFilename=ScreenOCR-Setup-{#AppVersion}

[Languages]
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"

[CustomMessages]
GroupStartup=起動:
TaskStartup=Windows にサインインしたときに ScreenOCR を起動する
GroupEngine=OCR エンジン:
TaskPpOcr=PP-OCRv6 Small を導入する（Python 3 とインターネット接続が必要）
StatusPpOcr=PP-OCRv6 Small を導入しています…

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
; アプリの既定（スタートアップ未登録）に合わせて既定はオフ。
Name: "startup"; Description: "{cm:TaskStartup}"; GroupDescription: "{cm:GroupStartup}"; Flags: unchecked
Name: "ppocr"; Description: "{cm:TaskPpOcr}"; GroupDescription: "{cm:GroupEngine}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "licenses\*"; DestDir: "{app}\licenses"; Flags: ignoreversion
Source: "..\scripts\install-ppocrv6-small.ps1"; DestDir: "{app}\scripts"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; アプリのトレイの「スタートアップに登録」と同じ値（引用符付きのフルパス）を書く。
; これでトレイのチェック状態とも一致する。削除はアンインストール時に [Code] で行う。
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "ScreenOCR"; ValueData: """{app}\{#AppExe}"""; Tasks: startup

[Run]
; コンソールは隠さない。pip の失敗が見えなくなるため。失敗時はスクリプトが Enter 待ちで止まる。
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\scripts\install-ppocrv6-small.ps1"" -PauseOnError"; StatusMsg: "{cm:StatusPpOcr}"; Tasks: ppocr; Flags: waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  RunValue = 'ScreenOCR';

{ スタートアップ登録は、インストール時のタスクでも、後からトレイで入れたものでも残りうる。
  このインストール先を指しているときだけ消す。開発中の app\ScreenOCR.exe など、
  別の場所の登録まで消さないため。 }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep <> usUninstall then
    Exit;
  if RegQueryStringValue(HKCU, RunKey, RunValue, Command) then
    if Pos(Lowercase(AddBackslash(ExpandConstant('{app}'))), Lowercase(Command)) > 0 then
      RegDeleteValue(HKCU, RunKey, RunValue);
end;
