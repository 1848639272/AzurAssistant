#ifndef PayloadDir
  #error PayloadDir must point to a clean self-contained publish directory
#endif
#ifndef ReleaseOutput
  #error ReleaseOutput is required
#endif
#ifndef BuildNumber
  #error BuildNumber is required
#endif
#ifndef InstallerName
  #error InstallerName is required
#endif
#ifndef VCRedist
  #error VCRedist must point to the verified Microsoft x64 redistributable
#endif

[Setup]
AppId={{41485CB6-B2A3-4A39-A2EB-D3B8A15C70B4}
AppName=蔚蓝助手
AppVersion=1.0.0_rc2_20261127
AppVerName=蔚蓝助手 v1.0.0_rc2_20261127
AppPublisher=AzurAssistant
VersionInfoVersion=1.0.0.0
VersionInfoDescription=蔚蓝助手完整安装包
DefaultDirName={autopf}\AzurAssistant
DefaultGroupName=蔚蓝助手
DisableProgramGroupPage=yes
DisableDirPage=auto
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=commandline
AppMutex=Local\AzurAssistant.DesktopInstance
SetupMutex=AzurAssistant.Setup
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
UsePreviousAppDir=yes
UsePreviousTasks=yes
UninstallDisplayIcon={app}\AzurAssistant.exe
SetupIconFile={#PayloadDir}\assets\branding\app.ico
OutputDir={#ReleaseOutput}
OutputBaseFilename={#InstallerName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
Uninstallable=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "快捷方式："; Flags: unchecked

[Files]
Source: "{#VCRedist}"; DestName: "vc_redist.x64.exe"; Flags: dontcopy noencryption
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion overwritereadonly recursesubdirs createallsubdirs

[Messages]
ErrorRenamingTemp=无法在目标目录替换文件。请关闭仍在使用该目录的程序，确认目录允许修改后重试。安装日志位于 %%TEMP%% 的 Setup Log 文件中。%n

[Icons]
Name: "{group}\蔚蓝助手"; Filename: "{app}\AzurAssistant.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\蔚蓝助手"; Filename: "{app}\AzurAssistant.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\AzurAssistant.exe"; Parameters: "--skip-startup-actions"; Description: "启动蔚蓝助手"; Flags: shellexec nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{app}\first-launch-notice.pending"

[Code]
const
  SYNCHRONIZE = $00100000;
  WAIT_OBJECT_0 = 0;
function OpenProcess(Access: LongWord; Inherit: Boolean; Pid: LongWord): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: LongWord): LongWord;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): Boolean;
  external 'CloseHandle@kernel32.dll stdcall';

var
  OldFiles, NewFiles: TArrayOfString;
  WasInstalled: Boolean;

function InitializeSetup(): Boolean;
var
  Pid: Integer;
  Handle: THandle;
  WaitResult: LongWord;
begin
  Result := False;
  Pid := StrToIntDef(ExpandConstant('{param:WAITPID|0}'), 0);
  if Pid > 0 then begin
    Handle := OpenProcess(SYNCHRONIZE, False, Pid);
    if Handle <> 0 then begin
      WaitResult := WaitForSingleObject(Handle, 30000);
      CloseHandle(Handle);
      if WaitResult <> WAIT_OBJECT_0 then begin
        MsgBox('旧助手仍未退出，请关闭蔚蓝助手后重新运行安装包。', mbError, MB_OK);
        exit;
      end;
    end;
  end;
  Result := True;
end;

function RuntimeIssues(): String;
var
  Names: TArrayOfString;
  I: Integer;
  Version: Int64;
  Issue: String;
begin
  Names := ['msvcp140.dll', 'msvcp140_1.dll', 'vcruntime140.dll', 'vcruntime140_1.dll'];
  Result := '';
  for I := 0 to GetArrayLength(Names) - 1 do begin
    Issue := '';
    if not GetPackedVersion(ExpandConstant('{sys}\') + Names[I], Version) then
      Issue := Names[I] + ' 缺失或无法读取'
    else if ComparePackedVersion(Version, PackVersionComponents(14, 44, 0, 0)) < 0 then
      Issue := Names[I] + ' 当前 ' + VersionToStr(Version) + '，需要 14.44 或更高';
    if Issue <> '' then begin
      if Result <> '' then Result := Result + '；';
      Result := Result + Issue;
    end;
  end;
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo,
  MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var Issues: String;
begin
  Result := MemoDirInfo + NewLine + NewLine + '运行环境：已内置 .NET 桌面运行时。';
  Issues := RuntimeIssues();
  if Issues = '' then Result := Result + NewLine + 'Visual C++ x64 运行库检查通过。'
  else Result := Result + NewLine + '安装时将补齐 Visual C++ x64 运行库：' + Issues + '。';
  if MemoTasksInfo <> '' then Result := Result + NewLine + NewLine + MemoTasksInfo;
end;

function DirectoryIssue(): String;
var
  Dir, Source, First, Second: String;
  Created: Boolean;
begin
  Dir := ExpandConstant('{app}');
  Result := '无法在安装目录创建、重命名或删除文件：' + Dir + '。请确认目标目录可修改。';
  Created := not DirExists(Dir);
  if Created and not ForceDirectories(Dir) then exit;
  try
    Source := ExpandConstant('{tmp}\directory-probe.tmp');
    if not SaveStringToFile(Source, '1', False) then begin
      Result := '无法写入安装临时目录：' + ExpandConstant('{tmp}');
      exit;
    end;
    First := GenerateUniqueName(Dir, '.tmp');
    if not CopyFile(Source, First, True) then exit;
    try
      Second := GenerateUniqueName(Dir, '.tmp');
      if not RenameFile(First, Second) then exit;
      First := Second;
      if DeleteFile(First) then Result := '';
    finally
      if FileExists(First) then DeleteFile(First);
    end;
  finally
    if Created then RemoveDir(Dir);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
  Issues: String;
begin
  Result := DirectoryIssue();
  if Result <> '' then begin
    Result := Result + #13#10 + '安装日志：' + ExpandConstant('{log}');
    Log(Result);
    exit;
  end;
  Issues := RuntimeIssues();
  if Issues = '' then begin Log('Visual C++ x64 运行库检查通过。'); exit; end;
  Log('Visual C++ x64 运行库需要补齐：' + Issues);
  if not IsAdminInstallMode() then
    Result := '请使用管理员权限安装包内的 Visual C++ x64 运行库。'
  else begin
    try
      ExtractTemporaryFile('vc_redist.x64.exe');
      if not Exec(ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then
        Result := 'Visual C++ x64 安装程序无法启动，错误码 ' + IntToStr(Code) + '。'
      else if (Code = 3010) or (Code = 1641) then begin
        NeedsRestart := True;
        Result := 'Visual C++ x64 要求重启 Windows，请重启后再次运行安装包。';
      end
      else if ((Code <> 0) and (Code <> 1638)) or (RuntimeIssues() <> '') then
        Result := 'Visual C++ x64 修复后未通过检查，安装程序返回 ' + IntToStr(Code) + '。';
    except
      Result := '无法解压包内的 Visual C++ x64 运行库：' + GetExceptionMessage();
    end;
  end;
  if Result <> '' then begin
    Result := Result + #13#10 + '检测到：' + Issues + #13#10 + '安装日志：' + ExpandConstant('{log}');
    Log(Result);
  end;
end;

function ManagedPath(Path: String): Boolean;
var
  Extension, Dir: String;
begin
  Path := Lowercase(Path);
  Result := False;
  if (Path = '') or (Pos('..', Path) > 0) or (Pos(':', Path) > 0) or (Path[1] = '\') or (Pos('/', Path) > 0) then exit;
  if (Pos('assets\', Path) = 1) or (Pos('models\', Path) = 1) or (Pos('licenses\', Path) = 1) then Result := True
  else if Pos('\', Path) = 0 then begin
    Extension := ExtractFileExt(Path);
    Result := (Extension = '.dll') or (Extension = '.pdb') or (Path = 'azurassistant.exe') or
      (Path = 'release.json') or (Path = 'update-source.json') or (Path = 'readme.txt') or (Path = 'license') or
      (Path = 'azurassistant.deps.json') or (Path = 'azurassistant.runtimeconfig.json');
  end
  else begin
    Dir := ExtractFileDir(Path);
    Result := (Pos('|' + Dir + '|', '|cs|de|es|fr|it|ja|ko|pl|pt-br|ru|tr|zh-hans|zh-hant|') > 0)
      and PathEndsWith(Path, '.resources.dll', True);
  end;
end;

function InNewFiles(Path: String): Boolean;
var I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(NewFiles) - 1 do
    if CompareText(Path, NewFiles[I]) = 0 then begin Result := True; exit; end;
end;

function IsOrdinaryInstallPath(RelativePath: String): Boolean;
var
  Root, Path: String;
  Entry: TFindRec;
begin
  Result := False;
  Root := ExpandConstant('{app}');
  repeat
    if RelativePath = '' then Path := Root
    else Path := AddBackslash(Root) + RelativePath;
    if not FindFirst(Path, Entry) then exit;
    try
      if (Entry.Attributes and $400) <> 0 then exit;
    finally
      FindClose(Entry);
    end;
    if RelativePath = '' then break;
    RelativePath := ExtractFileDir(RelativePath);
  until False;
  Result := True;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var I: Integer;
begin
  if CurStep = ssInstall then begin
    WasInstalled := FileExists(ExpandConstant('{app}\AzurAssistant.exe')) or FileExists(ExpandConstant('{app}\payload-files.txt'));
    LoadStringsFromFile(ExpandConstant('{app}\payload-files.txt'), OldFiles);
  end;
  if CurStep = ssPostInstall then begin
    if not WasInstalled then
      if not SaveStringToFile(ExpandConstant('{app}\first-launch-notice.pending'), '1', False) then
        Log('无法保存首次启动使用说明标记');
    if not LoadStringsFromFile(ExpandConstant('{app}\payload-files.txt'), NewFiles) or (GetArrayLength(NewFiles) = 0) then begin
      Log('新程序文件清单无法读取，保留旧文件。');
      exit;
    end;
    for I := 0 to GetArrayLength(OldFiles) - 1 do
      if ManagedPath(OldFiles[I]) and not InNewFiles(OldFiles[I]) and IsOrdinaryInstallPath(OldFiles[I]) then
        if not DeleteFile(ExpandConstant('{app}\') + OldFiles[I]) then
          Log('无法移除旧程序文件：' + OldFiles[I]);
  end;
end;
