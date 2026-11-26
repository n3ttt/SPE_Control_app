; Inno Setup script for SPE Control. Built by build.ps1.
#define AppName "SPE Control"
; AppName stays as the internal id (install folder, startup entry, firewall rule) so upgrades keep working.
; AppLabel is what people see.
#define AppLabel "Expert Amp Control"
#define AppVersion "1.2.0"
#define AppExe "SPEControl.exe"

[Setup]
AppId={{6B7D3F0E-6C1A-4E2B-9B54-3C1E2F7A9D11}
AppName={#AppLabel}
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
Name: "startup"; Description: "Start {#AppLabel} automatically when Windows starts (recommended)"
Name: "desktopicon"; Description: "Put a shortcut on the desktop"

[Files]
Source: "..\dist\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; Shortcuts from versions that were labelled "SPE Control"
Type: files; Name: "{group}\{#AppName}.lnk"
Type: files; Name: "{group}\Uninstall {#AppName}.lnk"
Type: files; Name: "{autodesktop}\{#AppName}.lnk"

[Icons]
Name: "{group}\{#AppLabel}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppLabel}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppLabel}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --startup"; Tasks: startup; Flags: uninsdeletevalue

[Run]
; Let phones and tablets on the home network reach the panel (the app itself refuses anything not on the local network).
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall add rule name=""{#AppName}"" dir=in action=allow program=""{app}\{#AppExe}"" enable=yes profile=any"; Flags: runhidden
Filename: "{app}\{#AppExe}"; Description: "Open {#AppLabel} now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\netsh.exe"; Parameters: "advfirewall firewall delete rule name=""{#AppName}"""; Flags: runhidden; RunOnceId: "DelFirewall"
