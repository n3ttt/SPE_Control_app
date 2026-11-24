; Inno Setup script for SPE Control. Built by build.ps1.
#define AppName "SPE Control"
#define AppVersion "1.0.0"
#define AppExe "SPEControl.exe"

[Setup]
AppId={{6B7D3F0E-6C1A-4E2B-9B54-3C1E2F7A9D11}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=n3ttt
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=SPEControl-Setup
SetupIconFile=app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
AppMutex=SPEControlPanelServer
CloseApplications=yes

[Tasks]
Name: "startup"; Description: "Start SPE Control automatically when Windows starts (recommended)"
Name: "desktopicon"; Description: "Put a shortcut on the desktop"

[Files]
Source: "..\dist\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --startup"; Tasks: startup; Flags: uninsdeletevalue

[Run]
; Let phones and tablets on the home network reach the panel (the app itself refuses anything not on the local network).
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=any protocol=TCP"; Flags: runhidden
Filename: "{app}\{#AppExe}"; Description: "Open SPE Control now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden; RunOnceId: "DelFirewall"
