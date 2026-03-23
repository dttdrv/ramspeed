; RAMSpeed Setup Script
; Requires InnoSetup 6  https://jrsoftware.org/isinfo.php
;
; Compile:
;   iscc installer\RAMSpeed.iss          (run from project root)
;
; Prerequisites:
;   dotnet publish -c Release -r win-x64 --self-contained false -o publish

#define AppName    "RAMSpeed"
#define AppVer     "1.0"
#define AppExe     "RAMSpeed.exe"

[Setup]
AppId={{8F3A1C2D-4E5B-6C7D-8E9F-0A1B2C3D4E5F}
AppName={#AppName}
AppVersion={#AppVer}
AppPublisher={#AppName}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename={#AppName}_Setup_{#AppVer}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName} — Memory Optimizer
SetupIconFile=..\src\RAMSpeed\Resources\app.ico
LicenseFile=
; No license dialog — skip straight to install

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon";  Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "startonboot";  Description: "Start RAMSpeed &automatically when Windows starts"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\src\RAMSpeed\Resources\app.ico"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppName}";         Filename: "{app}\{#AppExe}"; IconFilename: "{app}\app.ico"
Name: "{commondesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; IconFilename: "{app}\app.ico"; Tasks: desktopicon

[Run]
; Register the silent-elevation task without a logon trigger
Filename: "{app}\{#AppExe}"; Parameters: "--register-task"; \
  Flags: runhidden waituntilterminated shellexec
; If selected, update the task to add the logon trigger
Filename: "{app}\{#AppExe}"; Parameters: "--register-task --start-at-logon"; \
  Flags: runhidden waituntilterminated shellexec; Tasks: startonboot
; Launch after install
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; \
  Flags: postinstall nowait skipifsilent shellexec

[UninstallRun]
; Stop the app
Filename: "taskkill"; Parameters: "/F /IM {#AppExe}"; \
  Flags: runhidden waituntilterminated; RunOnceId: "TerminateApp"
; Remove the scheduled task
Filename: "schtasks.exe"; Parameters: "/Delete /TN ""{#AppName}"" /F"; \
  Flags: runhidden waituntilterminated; RunOnceId: "RemoveTask"

[UninstallDelete]
Type: filesandordirs; Name: "{app}"
