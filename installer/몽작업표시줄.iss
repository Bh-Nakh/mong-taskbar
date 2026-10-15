; 몽작업표시줄 설치 스크립트 (Inno Setup 6). release.bat 이 /DSrcDir=<몽작업표시줄.exe 와 hook\placer.dll 이 있는 폴더> 로 실행한다.
; 버전은 빌드된 exe 의 제품 버전(= src\Version.cs 의 Number)을 그대로 읽어 온다 (설치 스크립트에서 따로 올릴 필요 없음).
#ifndef SrcDir
  #define SrcDir ".."
#endif
#define AppName "몽작업표시줄"
#define AppExe "몽작업표시줄.exe"
#define AppVersion GetFileProductVersion(SrcDir + "\" + AppExe)
#define ProtoId "mongtaskbar"

[Setup]
AppId={{C5167C44-54A2-4EC1-890F-D32328A3425D}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=NoAd
; 그룹·아이콘·설정이 exe 옆에 저장되므로 쓰기 가능한 사용자 폴더(%LOCALAPPDATA%\Programs)에만 설치한다.
DefaultDirName={autopf}\{#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\setup
OutputBaseFilename=몽작업표시줄-Setup-{#AppVersion}
SetupIconFile=..\res\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
; DLL(placer.dll)은 탐색기 안에 들어 있어 닫을 수 없으므로 앱 닫기 대상에서 빼고 exe 만 닫는다. (DLL 은 아래 [Code] 에서 이름을 바꿔 비운다)
CloseApplicationsFilter=*.exe
RestartApplications=no
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoCompany=NoAd

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SrcDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
; 즉시 배치용 DLL (작업표시줄을 담당하는 explorer 에 들어가는 파일)
Source: "{#SrcDir}\hook\placer.dll"; DestDir: "{app}\hook"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; 홈페이지(설치 확인 페이지)의 [실행] 버튼이 쓰는 프로토콜. 제거할 때 함께 지운다.
Root: HKA; Subkey: "Software\Classes\{#ProtoId}"; ValueType: string; ValueName: ""; ValueData: "URL:{#AppName}"; Flags: uninsdeletekey
Root: HKA; Subkey: "Software\Classes\{#ProtoId}"; ValueType: string; ValueName: "URL Protocol"; ValueData: ""
Root: HKA; Subkey: "Software\Classes\{#ProtoId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExe}"""

[InstallDelete]
; 이름이 TaskbarGroup 이었던 옛 실행 파일이 같은 폴더에 남아 있으면 정리한다.
Type: files; Name: "{app}\TaskbarGroup.exe"
Type: files; Name: "{app}\hook\placer.dll.old"

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// placer.dll 은 작업표시줄 explorer 안에 들어가 있어 그대로는 덮어쓸 수 없다.
// 앱 build.bat 과 같은 방식으로 정지 파일을 만들어 DLL 이 스스로 빠져나가게 한 뒤 진행한다.
procedure ReleasePlacer();
var
  HookDir, StopFile, Dll, OldDll: String;
begin
  HookDir := ExpandConstant('{app}\hook');
  if not DirExists(HookDir) then Exit;
  StopFile := HookDir + '\placer.stop';
  SaveStringToFile(StopFile, '1', False);
  Sleep(3000);
  DeleteFile(StopFile);
  // 정지 파일로도 DLL 이 탐색기에서 빠지지 않았을 수 있다. 사용 중인 DLL 도 이름은 바꿀 수 있으므로 옆으로 치워 새 DLL 이 그 자리에 설치되게 한다.
  Dll := HookDir + '\placer.dll';
  OldDll := HookDir + '\placer.dll.old';
  if FileExists(Dll) then
  begin
    DeleteFile(OldDll);
    RenameFile(Dll, OldDll);
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  ReleasePlacer();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then ReleasePlacer();
end;
