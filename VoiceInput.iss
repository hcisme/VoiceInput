#define MyAppName "VoiceInput"
#define MyAppVersion "1.0.2"
#define MyAppPublisher "chihaicheng"
#define MyAppExeName "VoiceInput.exe"

#define MyPublishDir "D:\code\CSharp\VoiceInput\VoiceInput\bin\Release\net10.0-windows10.0.17763.0\win-x64\publish\"

[Setup]
AppId={{9A2B4C6D-1E3F-4G5H-6I7J-8K9L0M1N2O3P}
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
Name: "chinesesimp"; MessagesFile: "compiler:Default.isl"

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