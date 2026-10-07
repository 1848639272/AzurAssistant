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
AppVersion=0.1.0 beta
AppVerName=蔚蓝助手 v0.1.0 beta
AppPublisher=AzurAssistant
VersionInfoVersion=0.1.0.0
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
CloseApplications=no
RestartApplications=no
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
Source: "{#PayloadDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#VCRedist}"; DestDir: "{tmp}"; Flags: deleteafterinstall

[Icons]
Name: "{group}\蔚蓝助手"; Filename: "{app}\AzurAssistant.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\蔚蓝助手"; Filename: "{app}\AzurAssistant.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\AzurAssistant.exe"; Parameters: "--skip-startup-actions"; Description: "启动蔚蓝助手"; Flags: shellexec nowait postinstall skipifsilent

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

function NeedsVCRuntime(): Boolean;
var
  Major, Minor: Cardinal;
begin
  Result := True;
  if GetVersionNumbers(ExpandConstant('{sys}\msvcp140.dll'), Major, Minor) then
    if (Major shr 16 = 14) and ((Major and $FFFF) >= 44) and
      FileExists(ExpandConstant('{sys}\msvcp140_1.dll')) and
      FileExists(ExpandConstant('{sys}\vcruntime140.dll')) and
      FileExists(ExpandConstant('{sys}\vcruntime140_1.dll')) then Result := False;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Result := '';
  if NeedsVCRuntime() then begin
    if not IsAdminInstallMode() then begin
      Result := '缺少 Visual C++ x64 运行库，请以管理员方式安装完整包。';
      exit;
    end;
    ExtractTemporaryFile('vc_redist.x64.exe');
    if not Exec(ExpandConstant('{tmp}\vc_redist.x64.exe'), '/install /quiet /norestart', '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Result := 'Visual C++ 运行库安装程序无法启动。'
    else if Code = 3010 then NeedsRestart := True
    else if (Code <> 0) and (Code <> 1638) then
      Result := 'Visual C++ 运行库安装失败，错误码：' + IntToStr(Code);
  end;
end;

function ManagedPath(Path: String): Boolean;
var
  Extension: String;
begin
  Path := Lowercase(Path);
  Result := False;
  if (Path = '') or (Pos('..', Path) > 0) or (Pos(':', Path) > 0) or (Path[1] = '\') or (Path[1] = '/') then exit;
  if (Pos('assets\', Path) = 1) or (Pos('models\', Path) = 1) or (Pos('licenses\', Path) = 1) then Result := True
  else if Pos('\', Path) = 0 then begin
    Extension := ExtractFileExt(Path);
    Result := (Extension = '.dll') or (Extension = '.pdb') or (Path = 'azurassistant.exe') or
      (Path = 'release.json') or (Path = 'update-source.json') or (Path = 'readme.txt') or
      (Path = 'azurassistant.deps.json') or (Path = 'azurassistant.runtimeconfig.json');
  end;
end;

function InNewFiles(Path: String): Boolean;
var I: Integer;
begin
  Result := False;
  for I := 0 to GetArrayLength(NewFiles) - 1 do
    if CompareText(Path, NewFiles[I]) = 0 then begin Result := True; exit; end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var I: Integer;
begin
  if CurStep = ssInstall then begin
    WasInstalled := FileExists(ExpandConstant('{app}\AzurAssistant.exe'));
    LoadStringsFromFile(ExpandConstant('{app}\payload-files.txt'), OldFiles);
  end;
  if CurStep = ssPostInstall then begin
    LoadStringsFromFile(ExpandConstant('{app}\payload-files.txt'), NewFiles);
    for I := 0 to GetArrayLength(OldFiles) - 1 do
      if ManagedPath(OldFiles[I]) and not InNewFiles(OldFiles[I]) then
        if not DeleteFile(ExpandConstant('{app}\') + OldFiles[I]) then
          Log('无法移除旧程序文件：' + OldFiles[I]);
  end;
end;
