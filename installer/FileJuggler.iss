; File Juggler - Windows installer
;
; Build with:
;   iscc /DMyAppVersion=0.1.0 installer\FileJuggler.iss
; or, from the repo root:
;   tools\pack.ps1 -Version 0.1.0
;
; Design notes that are load-bearing rather than incidental:
;
; * PrivilegesRequired=lowest. The tool needs no elevation: its config lives in
;   %LOCALAPPDATA%, not Program Files. Installing per-user means the installer
;   never raises a UAC prompt, which is the first impression the product makes.
;
; * Autostart is opt-in and defaults to OFF. Autostarting the *editor* would open a
;   window at every sign-in, which is precisely the behaviour this product exists
;   to avoid ("never in the taskbar, never steals focus"). The thing that should
;   autostart is the resident daemon, which does not exist yet. The task is wired
;   up now so the mechanism is proven; repoint it at juggler.exe when the daemon
;   lands, and the default can become checked.
;
; * Uninstall never deletes the user's config. It holds real filesystem paths and
;   hand-written rules; silently removing it would be hostile. It is reported on the
;   uninstall completion page instead.

#ifndef MyAppVersion
  #define MyAppVersion "0.1.0"
#endif

#define AppName        "File Juggler"
#define AppExeName     "Juggler.Ui.exe"
#define AppPublisher   "Limitless"
#define AppRepo        "https://github.com/harshit5129/file-juggler"
#define AppIcon        "..\src\Juggler.Ui\Assets\Icon.ico"
#define AppPayload     "..\artifacts\publish\win-x64\Juggler.Ui.exe"

[Setup]
AppId={{7C4E8A21-5D93-4B6F-9A17-2E8C6F0B31D4}
AppName={#AppName}
AppVersion={#MyAppVersion}
AppVerName={#AppName} {#MyAppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription={#AppName} rule editor
AppComments=Pre-alpha. The resident daemon is not built yet, so no rule is applied to any file.
DefaultDirName={localappdata}\Programs\FileJuggler
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputBaseFilename=FileJuggler-{#MyAppVersion}-win-x64-setup
OutputDir=..\artifacts\dist
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Per-user install: no UAC prompt, no admin rights, nothing written outside the profile.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExeName}
SetupIconFile={#AppIcon}
LicenseFile=..\LICENSE
MinVersion=10.0
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; See the design note at the top: this is off by default on purpose.
Name: "autostart"; Description: "Start {#AppName} automatically when I sign in"; GroupDescription: "Startup: (the resident daemon starts this way once it exists)"; Flags: unchecked
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
; No Flags: a task with no explicit flag is checked by default. There is no "checked" flag.
Name: "startmenuicon"; Description: "Create a Start Menu shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "{#AppPayload}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
; Deliberately no IconFilename here. Setting it to the build-time source path writes an
; absolute path into the .lnk that cannot resolve on the target machine, and the shortcut
; then shows the Windows default application icon. With no IconFilename the shortcut
; inherits the emblem already embedded in Juggler.Ui.exe as a Win32 icon resource.
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: startmenuicon
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; HKCU, not HKLM: a per-user install must not need elevation to register itself.
; uninsdeletevalue so uninstalling removes the entry even if the task was unchecked later.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; \
    ValueType: string; ValueName: "{#AppName}"; \
    ValueData: """{app}\{#AppExeName}"""; \
    Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; \
    Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Build leftovers only. The config directory is deliberately NOT listed: it holds
; the user's own rules and real filesystem paths.
Type: filesandordirs; Name: "{app}"

[Code]
{ Report the config location on the uninstall summary page instead of deleting it. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  { An unconditional MsgBox here would block forever during a silent or scripted
    uninstall, because nothing is there to click it. Guard it. }
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
  begin
    if DirExists(ExpandConstant('{localappdata}\FileJuggler')) then
      MsgBox('Your configuration was kept at:' + #13#10 +
             ExpandConstant('{localappdata}\FileJuggler') + #13#10#13#10 +
             'It contains your rules and real file paths, so it is never deleted for you.' + #13#10 +
             'Delete it yourself if you want a clean slate.',
             mbInformation, MB_OK);
  end;
end;