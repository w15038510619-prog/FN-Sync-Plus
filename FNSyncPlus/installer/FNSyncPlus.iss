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
Name: "english"; MessagesFile: "compiler:Default.isl"
; 中文语言文件由社区维护，Inno Setup 只自带英文。缺失时退回英文，而不是让编译直接失败
; （构建机上没有该文件是很常见的情况）。
#if FileExists(CompilerPath + "Languages\ChineseSimplified.isl")
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif

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
// ---------------------------------------------------------------------------
// 指针重写
//
// 发布包里 current.json 记录的是构建机上的绝对目录，直接复制到用户机器上就会指向一个不存在的
// 路径，启动器随即认为"没有可用版本"，程序装好了却打不开。安装程序在复制完成后必须把指针改写
// 成本机目录，并为覆盖升级补写 previous.json，否则"升级保留旧版本目录"只是占着磁盘、无法回滚。
// ---------------------------------------------------------------------------

const
  AppVersionConst = '{#AppVersion}';

function JsonEscape(const S: String): String;
var
  T: String;
begin
  // JSON 里反斜杠必须转义，否则 Windows 路径会破坏 JSON。
  // StringChangeEx 的第一个参数是 var 参数，不能直接传 const。
  T := S;
  StringChangeEx(T, '\', '\\', True);
  Result := T;
end;

function DigitsOnly(const S: String): String;
var
  I: Integer;
begin
  Result := '';
  for I := 1 to Length(S) do
    if (S[I] >= '0') and (S[I] <= '9') then
      Result := Result + S[I];
end;

function PartWeight(Index: Integer): Int64;
begin
  if Index = 0 then Result := 1000000000
  else if Index = 1 then Result := 1000000
  else if Index = 2 then Result := 1000
  else Result := 1;
end;

// 把 "0.5.0" 变成可比较的权重；非法片段按 0 处理，不抛异常。
function VersionWeight(const V: String): Int64;
var
  I, Part: Integer;
  Buf: String;
begin
  Result := 0;
  Part := 0;
  Buf := '';
  for I := 1 to Length(V) do
  begin
    if V[I] = '.' then
    begin
      if Part <= 3 then
        Result := Result + StrToIntDef(DigitsOnly(Buf), 0) * PartWeight(Part);
      Inc(Part);
      Buf := '';
      if Part > 3 then Exit;
    end
    else
      Buf := Buf + V[I];
  end;
  if Part <= 3 then
    Result := Result + StrToIntDef(DigitsOnly(Buf), 0) * PartWeight(Part);
end;

// 读取该载荷声明支持的数据格式；读不到就是 0（未声明），回滚检查会据此拒绝。
function ReadSupportedFormat(const VersionDir: String): Integer;
var
  Raw: AnsiString;
  S, Digits: String;
  P, I: Integer;
begin
  Result := 0;
  // LoadStringFromFile 读的是 AnsiString；直接传 String 会编译期类型不匹配。
  if not LoadStringFromFile(VersionDir + '\build-meta.json', Raw) then Exit;
  S := Raw;
  P := Pos('"supported_data_format"', S);
  if P = 0 then Exit;
  S := Copy(S, P + Length('"supported_data_format"'), Length(S));
  P := Pos(':', S);
  if P = 0 then Exit;
  S := Copy(S, P + 1, Length(S));
  Digits := '';
  for I := 1 to Length(S) do
  begin
    if (S[I] >= '0') and (S[I] <= '9') then Digits := Digits + S[I]
    else if Digits <> '' then Break;
  end;
  Result := StrToIntDef(Digits, 0);
end;

procedure WritePointerFile(const Path, Version, VersionDir: String; Format: Integer);
var
  Json: String;
begin
  Json := '{"version":"' + Version + '","directory":"' + JsonEscape(VersionDir) + '",' +
          '"payload_sha256":"installed-by-setup","supported_data_format":' + IntToStr(Format) + '}';
  SaveStringToFile(Path, Json, False);
end;

// 找出比当前版本旧的最高版本目录，用于覆盖升级后的回滚。
function FindPreviousVersion(const AppDir, CurrentVersion: String): String;
var
  FindRec: TFindRec;
  Name, Version: String;
  BestWeight: Int64;
begin
  Result := '';
  BestWeight := 0;
  if not FindFirst(AppDir + '\app-*', FindRec) then Exit;
  try
    repeat
      if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
      begin
        Name := FindRec.Name;
        if Copy(Name, 1, 4) = 'app-' then
        begin
          Version := Copy(Name, 5, Length(Name) - 4);
          if (Version <> '') and
             (VersionWeight(Version) < VersionWeight(CurrentVersion)) and
             (VersionWeight(Version) > BestWeight) and
             FileExists(AppDir + '\' + Name + '\FNSyncPlus.WinUI.exe') then
          begin
            BestWeight := VersionWeight(Version);
            Result := Version;
          end;
        end;
      end;
    until not FindNext(FindRec);
  finally
    FindClose(FindRec);
  end;
end;

// 安装完成后确认版本目录和启动器都在，并把指针改写成本机路径。
procedure CurStepChanged(CurStep: TSetupStep);
var
  AppDir, VersionDir, PreviousVersion: String;
begin
  if CurStep <> ssPostInstall then Exit;
  AppDir := ExpandConstant('{app}');
  VersionDir := AppDir + '\app-' + AppVersionConst;
  if not DirExists(VersionDir) then
  begin
    MsgBox('安装完成但未找到版本目录：' + VersionDir + #13#10 +
           '请重新运行安装程序，或改用发布包中的 ZIP 手动部署。', mbError, MB_OK);
    Exit;
  end;
  if not FileExists(AppDir + '\{#LauncherName}') then
  begin
    MsgBox('安装完成但未找到启动器 ' + AppDir + '\{#LauncherName}' + #13#10 +
           '请重新运行安装程序。', mbError, MB_OK);
    Exit;
  end;

  WritePointerFile(AppDir + '\current.json', AppVersionConst, VersionDir,
    ReadSupportedFormat(VersionDir));

  PreviousVersion := FindPreviousVersion(AppDir, AppVersionConst);
  if PreviousVersion <> '' then
    WritePointerFile(AppDir + '\previous.json', PreviousVersion,
      AppDir + '\app-' + PreviousVersion,
      ReadSupportedFormat(AppDir + '\app-' + PreviousVersion));
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
