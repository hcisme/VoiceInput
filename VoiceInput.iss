#define MyAppName "VoiceInput"
#define MyAppVersion "1.0.5"
#define MyAppPublisher "chihaicheng"
#define MyAppExeName "VoiceInput.exe"

#define MyPublishDir "D:\code\CSharp\VoiceInput\VoiceInput\bin\Release\net10.0-windows10.0.17763.0\win-x64\publish\"

[Setup]
AppId={{18afa73b-e046-4499-a91f-958effff84e8}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppMutex=VoiceInput_Unique_App_Mutex
AppPublisher={#MyAppPublisher}

DefaultDirName={localappdata}\{#MyAppName}
DisableProgramGroupPage=yes

OutputDir=D:\code\CSharp\output\VoiceInput
OutputBaseFilename={#MyAppName}_Setup_v{#MyAppVersion}

Compression=lzma
SolidCompression=yes
PrivilegesRequired=lowest

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#MyPublishDir}*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; 创建开始菜单和桌面快捷方式
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
; 安装完成后自动勾选“运行软件”
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent
