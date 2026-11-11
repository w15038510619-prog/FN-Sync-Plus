; FN Sync Plus — 用户级安装程序（Inno Setup 6）
;
; 设计约束（与产品安全规则一致）：
;   * 只做用户级安装，不需要管理员权限，不写入 HKLM / Program Files；
;   * 程序目录与用户配置分离：程序在 %LOCALAPPDATA%\Programs\FNSyncPlus，
;     配置/凭据在 %LOCALAPPDATA%\FNSyncPlus，卸载默认不删除配置；
;   * 卸载绝不触碰 NAS 上的任何备份数据（安装程序根本不知道 NAS 路径）；
;   * 升级时保留 current.json/previous.json 之外的既有版本目录，以便回滚。
;
; 构建：先用 FNSyncPlus\build-release.ps1 生成 Release_WinUI\install\，
; 再用 Inno Setup 编译本脚本：
;     ISCC.exe /DAppVersion=0.4.0 FNSyncPlus.iss

#ifndef AppVersion
  #define AppVersion "0.4.0"
#endif
#ifndef PayloadDir
  #define PayloadDir "..\..\Release_WinUI\install"
#endif

#define AppName "FN Sync Plus"
#define AppPublisher "FN Sync Plus Project"
#define LauncherName "FN_Sync_Plus.exe"

[Setup]
AppId={{4E1C9F62-5F3B-4B7E-9E1C-2B7B0C6D51A1}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={localappdata}\Programs\FNSyncPlus
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
OutputDir=..\..\Release_WinUI\installer
OutputBaseFilename=FN_Sync_Plus_{#AppVersion}_x64_setup
SetupIconFile=..\FNSyncPlus.WinUI\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#LauncherName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
; 升级时先请用户关闭程序；不自动重启，由更新器/用户决定何时启动
CloseApplications=yes
RestartApplications=no
AllowNoIcons=yes
VersionInfoVersion={#AppVersion}

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："; Flags: checkedonce

[Files]
; 版本化安装目录：app-<version>\ + FN_Sync_Plus.exe + current.json + update.log
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; 安装说明（如果存在）
Source: "..\..\docs\deployment-guide.md"; DestDir: "{app}\docs"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#LauncherName}"; WorkingDir: "{app}"
Name: "{group}\卸载 {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#LauncherName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#LauncherName}"; Description: "启动 {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; 只删除本程序自己产生的临时目录。绝不删除 %LOCALAPPDATA%\FNSyncPlus 下的用户配置与凭据，
; 也绝不删除 NAS 上的备份（本安装程序不持有任何 NAS 路径）。
Type: filesandordirs; Name: "{app}\.health"
Type: filesandordirs; Name: "{app}\.downloads"
Type: filesandordirs; Name: "{app}\.update-work"
Type: files; Name: "{app}\update.log"

[Code]
// 安装完成后确认版本目录和启动器都在，避免装出一个打不开的程序目录。
procedure CurStepChanged(CurStep: TSetupStep);
var
  AppDir, VersionDir: String;
begin
  if CurStep <> ssPostInstall then Exit;
  AppDir := ExpandConstant('{app}');
  VersionDir := AppDir + '\app-{#AppVersion}';
  if not DirExists(VersionDir) then
    MsgBox('安装完成但未找到版本目录：' + VersionDir + #13#10 +
           '请重新运行安装程序，或改用发布包中的 ZIP 手动部署。', mbError, MB_OK)
  else if not FileExists(AppDir + '\{#LauncherName}') then
    MsgBox('安装完成但未找到启动器 ' + AppDir + '\{#LauncherName}' + #13#10 +
           '请重新运行安装程序。', mbError, MB_OK);
end;

// 卸载时提示配置仍在，避免用户以为备份或设置被删除。
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    MsgBox('程序已卸载。' + #13#10 + #13#10 +
           '用户配置与已保存的凭据仍保留在 %LOCALAPPDATA%\FNSyncPlus，' +
           'NAS 上的备份数据完全没有被修改。' + #13#10 +
           '如需彻底清除本机配置，请手动删除该目录。', mbInformation, MB_OK);
end;
