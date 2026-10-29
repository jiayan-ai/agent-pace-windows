#ifndef SourceDir
#define SourceDir "..\dist\win-x64"
#endif
[Setup]
AppId={{6E86874C-8ACD-416D-859B-072EA686F53A}
AppName=Agent Pace
AppVersion=1.0.0-beta.4
AppPublisher=jiayan-ai
AppPublisherURL=https://github.com/jiayan-ai/agent-pace-windows
DefaultDirName={localappdata}\Programs\Agent Pace
DefaultGroupName=Agent Pace
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=Agent-Pace-1.0.0-beta.4-win-x64-setup
SetupIconFile=..\src\AgentPace.Windows\Assets\AppIcon.ico
UninstallDisplayIcon={app}\AgentPace.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
#if FileExists(AddBackslash(CompilerPath) + "Languages\ChineseSimplified.isl")
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
#endif
[Tasks]
Name: "startup"; Description: "Start Agent Pace when I sign in / 登录 Windows 时启动"; Flags: unchecked
[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
[Icons]
Name: "{group}\Agent Pace"; Filename: "{app}\AgentPace.exe"
[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "AgentPace"; ValueData: """{app}\AgentPace.exe"" --background"; Flags: uninsdeletevalue; Tasks: startup
[Run]
Filename: "{app}\AgentPace.exe"; Description: "Launch Agent Pace / 打开 Agent Pace"; Flags: nowait postinstall skipifsilent
