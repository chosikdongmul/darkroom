; Darkroom 설치 파일 (Inno Setup 6). 직접 컴파일하지 말고 build\make-installer.ps1을 쓴다(단일 exe를 먼저 만들고 버전을 넘김).
; - 이 사용자에게만 설치(관리자 권한 불필요): %LOCALAPPDATA%\Programs\Darkroom
; - 시작 메뉴 바로가기, 선택: 바탕화면 바로가기·Windows 시작 시 실행(HKCU Run, 앱의 설정과 같은 값)
; - 업그레이드·제거 전에 실행 중인 Darkroom을 정상 종료(Darkroom.exe --quit, 상태 저장 후 종료)
; - 제거: 프로그램·풀어 둔 콘텐츠·WebView 캐시(%LOCALAPPDATA%\Darkroom)를 지우고, 설정(%APPDATA%\Darkroom)은 물어본다

#ifndef AppVersion
  #error "AppVersion을 넘겨야 합니다: ISCC /DAppVersion=0.5.0 installer.iss"
#endif
#ifndef SourceExe
  #define SourceExe "..\dist\Darkroom.exe"
#endif

[Setup]
AppId={{6F0C3A2E-8B1D-4E57-9C2A-D4A7B1E9F305}
AppName=Darkroom
AppVersion={#AppVersion}
AppVerName=Darkroom {#AppVersion}
AppPublisher=chosikdongmul
AppPublisherURL=https://github.com/chosikdongmul/darkroom
VersionInfoVersion={#AppVersion}
VersionInfoDescription=Darkroom 설치
DefaultDirName={autopf}\Darkroom
DefaultGroupName=Darkroom
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Darkroom-Setup-{#AppVersion}
SetupIconFile=darkroom.ico
UninstallDisplayIcon={app}\Darkroom.exe
UninstallDisplayName=Darkroom
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Windows 언어에 맞는 게 있으면 언어 선택 창을 띄우지 않는다
ShowLanguageDialog=auto
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
korean.AutoStart=Windows 시작 시 Darkroom 실행
english.AutoStart=Start Darkroom when Windows starts
korean.RemoveSettings=Darkroom 설정(위젯 배치·색·글꼴·사진 폴더)과 로그도 지울까요?%n%n아니요를 누르면 다시 설치했을 때 그대로 이어서 씁니다.
english.RemoveSettings=Also delete your Darkroom settings (widget layout, theme, fonts, photo folder) and logs?%n%nChoose No to keep them for a later reinstall.

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "{cm:AutoStart}"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; DestName: "Darkroom.exe"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Darkroom"; Filename: "{app}\Darkroom.exe"
Name: "{autodesktop}\Darkroom"; Filename: "{app}\Darkroom.exe"; Tasks: desktopicon

[Registry]
; 앱의 "Windows 시작 시 실행"과 같은 값(AutoStart.cs). 앱에서 끄고 켜도 된다.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Darkroom"; ValueData: """{app}\Darkroom.exe"" --autostart"; Tasks: autostart

[Run]
Filename: "{app}\Darkroom.exe"; Description: "{cm:LaunchProgram,Darkroom}"; Flags: nowait postinstall skipifsilent
; 앱 안 업데이트(/SILENT /RELAUNCH=1): 끝나면 다시 실행
Filename: "{app}\Darkroom.exe"; Flags: nowait; Check: RelaunchAfterUpdate

[UninstallRun]
Filename: "{app}\Darkroom.exe"; Parameters: "--quit"; RunOnceId: "QuitDarkroom"; Flags: runhidden waituntilterminated skipifdoesntexist

[UninstallDelete]
Type: filesandordirs; Name: "{localappdata}\Darkroom"

[Code]
function RelaunchAfterUpdate: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Exe: String;
  Code: Integer;
begin
  Result := '';
  { 업그레이드: 이미 설치된 Darkroom이 실행 중이면 정상 종료시킨다(설정 저장). }
  Exe := ExpandConstant('{app}\Darkroom.exe');
  if FileExists(Exe) then
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
  begin
    { 앱에서 켠 자동 실행도 지운다(설치 때 고르지 않았어도) }
    RegDeleteValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Run', 'Darkroom');
    if (not UninstallSilent) and DirExists(ExpandConstant('{userappdata}\Darkroom')) then
      if MsgBox(CustomMessage('RemoveSettings'), mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(ExpandConstant('{userappdata}\Darkroom'), True, True, True);
  end;
end;
