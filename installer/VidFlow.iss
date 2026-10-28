; VidFlow installer (Inno Setup 6). Build with:  pwsh tools/build-release.ps1
; Per-user install, no administrator rights required.

#define AppName "VidFlow"
#ifndef AppVersion
  #define AppVersion "1.3.9"
#endif
; x64 or arm64 (build-release.ps1 builds both).
#ifndef Arch
  #define Arch "x64"
#endif
#if Arch == "arm64"
  #define SourceDir "..\VidFlow-Release-arm64"
#else
  #define SourceDir "..\VidFlow-Release"
#endif

[Setup]
AppId={{9C7E2C4B-6E7B-4E2D-9C1A-6A1F2B7D8E31}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
; The components (yt-dlp, ffmpeg, Deno, aria2) are x64 programs; only Windows 11 runs those on ARM.
MinVersion=10.0.22000
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
#endif
OutputDir=..\dist
OutputBaseFilename={#AppName}-Setup-{#AppVersion}-{#Arch}
SetupIconFile=..\VidFlow\Assets\AppIcon.ico
UninstallDisplayIcon={app}\VidFlow.exe
; Tell Explorer to refresh icons after installing: an update keeps the same path, so the old icon would stay cached.
ChangesAssociations=yes
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ShowLanguageDialog=auto

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "chinesesimp"; MessagesFile: "compiler:Default.isl"

[Messages]
; Simplified Chinese for the pages a per-user install / uninstall actually shows. Anything not listed
; falls back to English.
chinesesimp.LanguageName=简体中文
chinesesimp.LanguageID=$0804
chinesesimp.LanguageCodePage=0
chinesesimp.SetupAppTitle=安装
chinesesimp.SetupWindowTitle=安装 - %1
chinesesimp.UninstallAppTitle=卸载
chinesesimp.UninstallAppFullTitle=卸载 %1
chinesesimp.SelectLanguageTitle=选择安装语言
chinesesimp.SelectLanguageLabel=选择安装过程中使用的语言。
chinesesimp.ButtonBack=< 上一步(&B)
chinesesimp.ButtonNext=下一步(&N) >
chinesesimp.ButtonInstall=安装(&I)
chinesesimp.ButtonOK=确定
chinesesimp.ButtonCancel=取消
chinesesimp.ButtonYes=是(&Y)
chinesesimp.ButtonYesToAll=全是(&A)
chinesesimp.ButtonNo=否(&N)
chinesesimp.ButtonNoToAll=全否(&O)
chinesesimp.ButtonFinish=完成(&F)
chinesesimp.ButtonBrowse=浏览(&B)...
chinesesimp.ButtonWizardBrowse=浏览(&R)...
chinesesimp.ButtonNewFolder=新建文件夹(&M)
chinesesimp.ExitSetupTitle=退出安装
chinesesimp.ExitSetupMessage=安装尚未完成。如果现在退出，程序将不会被安装。%n%n你可以稍后再次运行安装程序完成安装。%n%n确定要退出安装吗？
chinesesimp.WizardSelectDir=选择安装位置
chinesesimp.SelectDirDesc=要将 [name] 安装到哪里？
chinesesimp.SelectDirLabel3=安装程序会将 [name] 安装到下面的文件夹中。
chinesesimp.SelectDirBrowseLabel=点击“下一步”继续。如需选择其他文件夹，请点击“浏览”。
chinesesimp.WizardSelectTasks=选择附加任务
chinesesimp.SelectTasksDesc=需要执行哪些附加任务？
chinesesimp.SelectTasksLabel2=选择安装 [name] 时需要执行的附加任务，然后点击“下一步”。
chinesesimp.WizardReady=准备安装
chinesesimp.ReadyLabel1=安装程序已准备好在你的电脑上安装 [name]。
chinesesimp.ReadyLabel2a=点击“安装”开始安装，或点击“上一步”查看或修改设置。
chinesesimp.ReadyLabel2b=点击“安装”开始安装。
chinesesimp.ReadyMemoDir=安装位置：
chinesesimp.ReadyMemoTasks=附加任务：
chinesesimp.WizardPreparing=正在准备安装
chinesesimp.PreparingDesc=安装程序正在准备在你的电脑上安装 [name]。
chinesesimp.ApplicationsFound=以下程序正在使用需要更新的文件。建议允许安装程序自动关闭这些程序。
chinesesimp.ApplicationsFound2=以下程序正在使用需要更新的文件。建议允许安装程序自动关闭这些程序。安装完成后，安装程序会尝试重新启动它们。
chinesesimp.CloseApplications=自动关闭这些程序(&A)
chinesesimp.DontCloseApplications=不要关闭这些程序(&D)
chinesesimp.WizardInstalling=正在安装
chinesesimp.InstallingLabel=请稍候，正在安装 [name]。
chinesesimp.FinishedHeadingLabel=[name] 安装完成
chinesesimp.FinishedLabelNoIcons=[name] 已安装到你的电脑上。
chinesesimp.FinishedLabel=[name] 已安装到你的电脑上，可以通过开始菜单中的快捷方式启动。
chinesesimp.ClickFinish=点击“完成”退出安装程序。
chinesesimp.StatusClosingApplications=正在关闭程序...
chinesesimp.StatusCreateDirs=正在创建文件夹...
chinesesimp.StatusExtractFiles=正在复制文件...
chinesesimp.StatusCreateIcons=正在创建快捷方式...
chinesesimp.StatusSavingUninstall=正在保存卸载信息...
chinesesimp.StatusRunProgram=正在完成安装...
chinesesimp.StatusRollback=正在撤销更改...
chinesesimp.ConfirmUninstall=确定要完全删除 %1 及其所有组件吗？
chinesesimp.UninstallStatusLabel=请稍候，正在从你的电脑上删除 %1。
chinesesimp.UninstalledAll=%1 已成功从你的电脑上删除。
chinesesimp.UninstalledMost=%1 卸载完成。%n%n部分内容无法删除，可以手动删除。
chinesesimp.WizardUninstalling=卸载状态
chinesesimp.StatusUninstalling=正在卸载 %1...

[CustomMessages]
english.DesktopIcon=Create a &desktop shortcut
chinesesimp.DesktopIcon=创建桌面快捷方式(&D)
english.LaunchApp=Launch VidFlow
chinesesimp.LaunchApp=启动 VidFlow
english.RemoveData=Also delete VidFlow's settings, download list, sign-in data and downloaded components (yt-dlp, ffmpeg)?%n%nYour downloaded videos are not affected.
chinesesimp.RemoveData=是否同时删除 VidFlow 的设置、下载列表、登录信息和已下载的组件（yt-dlp、ffmpeg）？%n%n你下载的视频不会受影响。

[Tasks]
Name: "desktopicon"; Description: "{cm:DesktopIcon}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\VidFlow.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\VidFlow.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\VidFlow.exe"; Description: "{cm:LaunchApp}"; Flags: nowait postinstall skipifsilent
; In-app updates run silently with /UPDATE=1: reopen VidFlow when done.
Filename: "{app}\VidFlow.exe"; Flags: nowait; Check: IsUpdate

[UninstallRun]
; Close the app (it may be running in the tray), then remove its notification registration.
Filename: "{sys}\taskkill.exe"; Parameters: "/IM VidFlow.exe /F"; Flags: runhidden; RunOnceId: "CloseVidFlow"
Filename: "{app}\VidFlow.exe"; Parameters: "--uninstall-cleanup"; Flags: runhidden waituntilterminated; RunOnceId: "CleanupNotifications"

[Code]
function IsUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // Keep user data by default (and always in silent mode); remove it only when the user agrees.
  if (CurUninstallStep = usPostUninstall) and
     (SuppressibleMsgBox(CustomMessage('RemoveData'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES) then
    DelTree(ExpandConstant('{localappdata}\VidFlow'), True, True, True);
end;
