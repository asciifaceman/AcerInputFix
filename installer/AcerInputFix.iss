; AcerInputFix — Inno Setup wizard
; Flow:
;   Welcome → Mode (Install / Uninstall / Purge)
;   → Consent (Next) → [install: Dir/Tasks/Ready] or [removal: Confirm]
;   → tracked steps → Summary → Finish
; Build: ISCC.exe /DMyAppVersion=0.2.0 AcerInputFix.iss

#ifndef MyAppVersion
  #define MyAppVersion "0.2.0"
#endif

#define MyAppName "AcerInputFix"
#define MyAppPublisher "asciifaceman"
#define MyAppURL "https://github.com/asciifaceman/AcerInputFix"
#define MyAppExeName "AcerInputFix.exe"
#define MyTaskName "AcerInputFix"
#define MyAppMutex "Local\AcerInputFix"
#define MyAppId "{{E8F4A201-9C3B-4D7E-A1F2-6B8D0C5E9A17}}"

[Setup]
AppId={#MyAppId}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; Always elevate. Per-user (non-admin) installs cannot reliably register the
; Highest logon task used for Col07 auto-reset, and the mode dialog confused
; the Install / Uninstall / Purge flow.
PrivilegesRequired=admin
UsePreviousAppDir=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename={#MyAppName}-{#MyAppVersion}-win-x64-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Acer HID Col07 input workaround
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#MyAppVersion}
SetupMutex={#MyAppName}Setup
AppMutex={#MyAppMutex}
CloseApplications=force
CloseApplicationsFilter={#MyAppExeName}
RestartApplications=no
AllowNoIcons=yes
MinVersion=10.0
DisableWelcomePage=no
ShowLanguageDialog=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel1=Welcome to the AcerInputFix setup wizard
WelcomeLabel2=This wizard can install, upgrade, uninstall, or purge AcerInputFix.%n%nSetup requires Administrator permission so it can register an elevated sign-in task (needed for Col07 auto-reset).%n%nAfter UAC, you will choose Install, Uninstall, or Purge, then confirm with explicit consent before anything is changed.%n%nClick Next to continue.
ReadyLabel1=Setup is ready to begin
ReadyLabel2b=Click Next to apply the consented changes.%n%nSetup will show each task as it runs, then a summary when finished.
FinishedHeadingLabel=AcerInputFix setup complete
FinishedLabel=Setup finished successfully. Review the summary below, then click Finish to exit.
ClickFinish=&Finish

[Tasks]
Name: "startup"; Description: "Start {#MyAppName} when I sign in (elevated logon task — needed for Col07 auto-reset)"; Flags: checkedonce
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "..\artifacts\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion; BeforeInstall: BeforeFileInstall

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{sys}\schtasks.exe"; Parameters: "/Run /TN ""{#MyTaskName}"""; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Comment: "Starts AcerInputFix elevated via the logon task"; Check: StartupTaskSelected
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Comment: "Starts AcerInputFix"; Check: not StartupTaskSelected
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{sys}\schtasks.exe"; Parameters: "/Run /TN ""{#MyTaskName}"""; WorkingDir: "{app}"; IconFilename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; Check: StartupTaskSelected
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon; Check: not StartupTaskSelected

[Run]
Filename: "{sys}\schtasks.exe"; Parameters: "/Run /TN ""{#MyTaskName}"""; Description: "Start {#MyAppName} now (elevated)"; Flags: nowait postinstall skipifsilent unchecked; Check: StartupTaskSelected
Filename: "{app}\{#MyAppExeName}"; Description: "Start {#MyAppName} now"; Flags: nowait postinstall skipifsilent unchecked; Check: not StartupTaskSelected

[UninstallDelete]
Type: files; Name: "{app}\installed-version.txt"
Type: dirifempty; Name: "{app}"

[Code]
const
  ModeInstall = 0;
  ModeUninstall = 1;
  ModePurge = 2;

var
  ModePage: TWizardPage;
  ModeInstallRadio: TNewRadioButton;
  ModeUninstallRadio: TNewRadioButton;
  ModePurgeRadio: TNewRadioButton;
  ConsentPage: TWizardPage;
  ConsentMemo: TNewMemo;
  ConsentCheck: TNewCheckBox;
  RemovalPage: TWizardPage;
  RemovalMemo: TNewMemo;
  ProgressPage: TOutputProgressWizardPage;
  InstallSummary: String;
  WizardMode: Integer;
  RemovalDone: Boolean;
  DidRegisterStartup: Boolean;
  DidStopApp: Boolean;
  StepCount: Integer;
  StepIndex: Integer;

function QuotePs(const Value: String): String;
var
  S: String;
begin
  S := Value;
  StringChangeEx(S, '''', '''''', True);
  Result := '''' + S + '''';
end;

procedure AppendSummary(const Line: String);
begin
  if InstallSummary = '' then
    InstallSummary := Line
  else
    InstallSummary := InstallSummary + #13#10 + Line;
end;

procedure ShowStep(const Title, Detail: String);
begin
  if ProgressPage <> nil then
  begin
    ProgressPage.SetText(Title, Detail);
    if StepCount > 0 then
      ProgressPage.SetProgress(StepIndex, StepCount);
  end;
end;

function IsInstallMode(): Boolean;
begin
  Result := WizardMode = ModeInstall;
end;

function IsRemovalMode(): Boolean;
begin
  Result := (WizardMode = ModeUninstall) or (WizardMode = ModePurge);
end;

function StartupTaskSelected(): Boolean;
begin
  Result := WizardIsTaskSelected('startup');
end;

function StopApp(): Boolean;
var
  ResultCode: Integer;
begin
  Result := True;
  Exec(
    ExpandConstant('{sys}\taskkill.exe'),
    '/IM {#MyAppExeName} /F',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode
  );
  Sleep(500);
  DidStopApp := True;
end;

function RegisterLogonTask(): Boolean;
var
  ExePath, UserName, Command: String;
  ResultCode: Integer;
begin
  ExePath := ExpandConstant('{app}\{#MyAppExeName}');
  UserName := GetEnv('USERDOMAIN') + '\' + GetEnv('USERNAME');

  Command :=
    '-NoProfile -ExecutionPolicy Bypass -Command "' +
    '$ErrorActionPreference=''Stop''; ' +
    '$exe=' + QuotePs(ExePath) + '; ' +
    '$user=' + QuotePs(UserName) + '; ' +
    '$action=New-ScheduledTaskAction -Execute $exe; ' +
    '$trigger=New-ScheduledTaskTrigger -AtLogOn -User $user; ' +
    '$principal=New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest; ' +
    '$settings=New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable -MultipleInstances IgnoreNew; ' +
    'Register-ScheduledTask -TaskName ''{#MyTaskName}'' -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description ''AcerInputFix logon start'' -Force | Out-Null' +
    '"';

  Result := Exec(
    ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    Command,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode
  ) and (ResultCode = 0);

  if not Result then
    Log('RegisterLogonTask failed with code ' + IntToStr(ResultCode));
end;

function UnregisterLogonTask(): Boolean;
var
  ResultCode: Integer;
begin
  Exec(
    ExpandConstant('{sys}\schtasks.exe'),
    '/Delete /TN "{#MyTaskName}" /F',
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode
  );
  Result := True;
end;

procedure DeleteFileIfExists(const FileName: String);
begin
  if FileExists(FileName) then
    DeleteFile(FileName);
end;

procedure DeleteTreeIfExists(const Dir: String);
begin
  if DirExists(Dir) then
    DelTree(Dir, True, True, True);
end;

procedure RemoveShortcuts();
begin
  DeleteFileIfExists(ExpandConstant('{userdesktop}\{#MyAppName}.lnk'));
  DeleteFileIfExists(ExpandConstant('{commondesktop}\{#MyAppName}.lnk'));
  DeleteTreeIfExists(ExpandConstant('{group}'));
  DeleteTreeIfExists(ExpandConstant('{userprograms}\{#MyAppName}'));
  DeleteTreeIfExists(ExpandConstant('{commonprograms}\{#MyAppName}'));
end;

function TryRunPreviousUninstaller(): Boolean;
var
  UninstallKey, UninstallPath, Params: String;
  ResultCode: Integer;
begin
  Result := False;
  UninstallKey :=
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\' +
    '{E8F4A201-9C3B-4D7E-A1F2-6B8D0C5E9A17}_is1';

  if not RegQueryStringValue(HKLM, UninstallKey, 'UninstallString', UninstallPath) then
    if not RegQueryStringValue(HKCU, UninstallKey, 'UninstallString', UninstallPath) then
      Exit;

  UninstallPath := RemoveQuotes(UninstallPath);
  if not FileExists(UninstallPath) then
    Exit;

  Params := '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART';
  Result := Exec(
    UninstallPath,
    Params,
    '',
    SW_HIDE,
    ewWaitUntilTerminated,
    ResultCode
  ) and (ResultCode = 0);
end;

procedure RemoveInstallDir(const Dir: String; const PurgeData: Boolean);
var
  DataLog, DataOld, DataStats: String;
  TmpData: String;
begin
  if not DirExists(Dir) then
    Exit;

  DataLog := AddBackslash(Dir) + 'AcerInputFix.log';
  DataOld := AddBackslash(Dir) + 'AcerInputFix.old.log';
  DataStats := AddBackslash(Dir) + 'stats.json';

  if not PurgeData then
  begin
    TmpData := ExpandConstant('{tmp}\AcerInputFix-data-preserve');
    ForceDirectories(TmpData);
    if FileExists(DataLog) then
      CopyFile(DataLog, AddBackslash(TmpData) + 'AcerInputFix.log', False);
    if FileExists(DataOld) then
      CopyFile(DataOld, AddBackslash(TmpData) + 'AcerInputFix.old.log', False);
    if FileExists(DataStats) then
      CopyFile(DataStats, AddBackslash(TmpData) + 'stats.json', False);
  end;

  DeleteTreeIfExists(Dir);

  if not PurgeData then
  begin
    ForceDirectories(Dir);
    if FileExists(AddBackslash(TmpData) + 'AcerInputFix.log') then
      CopyFile(AddBackslash(TmpData) + 'AcerInputFix.log', DataLog, False);
    if FileExists(AddBackslash(TmpData) + 'AcerInputFix.old.log') then
      CopyFile(AddBackslash(TmpData) + 'AcerInputFix.old.log', DataOld, False);
    if FileExists(AddBackslash(TmpData) + 'stats.json') then
      CopyFile(AddBackslash(TmpData) + 'stats.json', DataStats, False);
  end;
end;

procedure PerformRemoval(const PurgeData: Boolean);
var
  AppDir, LocalDir: String;
begin
  InstallSummary := '';
  StepCount := 5;
  StepIndex := 0;

  ProgressPage := CreateOutputProgressPage(
    'Removing AcerInputFix',
    'Each task is listed as it runs. Please wait.'
  );
  ProgressPage.Show;
  try
    Inc(StepIndex);
    ShowStep('Stopping AcerInputFix if it is running…', '');
    StopApp();
    AppendSummary('• Stopped any running AcerInputFix process');

    Inc(StepIndex);
    ShowStep('Removing elevated sign-in startup task…', '{#MyTaskName}');
    UnregisterLogonTask();
    AppendSummary('• Removed logon task "{#MyTaskName}" (if it existed)');

    Inc(StepIndex);
    ShowStep('Removing Start Menu / desktop shortcuts…', '');
    RemoveShortcuts();
    AppendSummary('• Removed shortcuts (if present)');

    Inc(StepIndex);
    ShowStep('Running previous uninstaller (if present)…', '');
    if TryRunPreviousUninstaller() then
      AppendSummary('• Ran the previous Inno uninstaller')
    else
      AppendSummary('• No prior Inno uninstaller entry (manual cleanup)');

    Inc(StepIndex);
    AppDir := ExpandConstant('{app}');
    LocalDir := ExpandConstant('{localappdata}\{#MyAppName}');
    ShowStep('Removing program files…', AppDir);
    RemoveInstallDir(AppDir, PurgeData);
    if CompareText(AppDir, LocalDir) <> 0 then
      RemoveInstallDir(LocalDir, PurgeData);

    if PurgeData then
    begin
      AppendSummary('• Purged program files and runtime data');
      AppendSummary('    ' + AppDir);
      if CompareText(AppDir, LocalDir) <> 0 then
        AppendSummary('    ' + LocalDir);
    end
    else
    begin
      AppendSummary('• Removed program files');
      AppendSummary('• Kept stats/logs (if any) under the install folder');
      AppendSummary('    ' + AppDir);
    end;

    AppendSummary('');
    if PurgeData then
      AppendSummary('Purge completed successfully.')
    else
      AppendSummary('Uninstall completed successfully.');
    AppendSummary('Click Finish to exit this wizard.');
    Sleep(400);
  finally
    ProgressPage.Hide;
    ProgressPage := nil;
  end;

  RemovalDone := True;
end;

procedure BeforeFileInstall();
begin
  if not IsInstallMode() then
    Exit;
  Inc(StepIndex);
  ShowStep(
    'Installing program files…',
    ExpandConstant('Copying {#MyAppExeName} to {app}')
  );
end;

procedure UpdateConsentText();
begin
  case WizardMode of
    ModeInstall:
      ConsentMemo.Text :=
        'You chose: Install or upgrade AcerInputFix.' + #13#10 +
        '' + #13#10 +
        'Setup will:' + #13#10 +
        '1. Request Administrator permission (UAC) if needed.' + #13#10 +
        '2. Stop AcerInputFix if it is already running.' + #13#10 +
        '3. Install or replace AcerInputFix.exe in the folder you choose.' + #13#10 +
        '4. Optionally register an elevated sign-in scheduled task.' + #13#10 +
        '5. Optionally create Start Menu / desktop shortcuts.' + #13#10 +
        '6. Preserve stats.json / logs across upgrades.' + #13#10 +
        '' + #13#10 +
        'This tool installs a low-level keyboard hook and may temporarily' + #13#10 +
        'disable/enable the Acer Col07 HID device when repairing input.' + #13#10 +
        '' + #13#10 +
        'License for this project has not been chosen yet.' + #13#10 +
        '' + #13#10 +
        'Nothing is changed until you continue and setup begins its tasks.';
    ModeUninstall:
      ConsentMemo.Text :=
        'You chose: Uninstall AcerInputFix.' + #13#10 +
        '' + #13#10 +
        'Setup will:' + #13#10 +
        '1. Stop AcerInputFix if it is running.' + #13#10 +
        '2. Remove the elevated sign-in scheduled task.' + #13#10 +
        '3. Remove Start Menu / desktop shortcuts.' + #13#10 +
        '4. Remove AcerInputFix program files.' + #13#10 +
        '5. Keep stats.json and log files (not a purge).' + #13#10 +
        '' + #13#10 +
        'Nothing is removed until you continue and confirm on the next page.';
    ModePurge:
      ConsentMemo.Text :=
        'You chose: Uninstall and purge AcerInputFix.' + #13#10 +
        '' + #13#10 +
        'Setup will:' + #13#10 +
        '1. Stop AcerInputFix if it is running.' + #13#10 +
        '2. Remove the elevated sign-in scheduled task.' + #13#10 +
        '3. Remove Start Menu / desktop shortcuts.' + #13#10 +
        '4. Remove AcerInputFix program files.' + #13#10 +
        '5. DELETE stats.json, logs, and the install folder(s).' + #13#10 +
        '' + #13#10 +
        'This cannot be undone.' + #13#10 +
        '' + #13#10 +
        'Nothing is removed until you continue and confirm on the next page.';
  end;

  ConsentCheck.Checked := False;
  WizardForm.NextButton.Enabled := False;
end;

procedure UpdateRemovalText();
begin
  if WizardMode = ModePurge then
    RemovalMemo.Text :=
      'Ready to uninstall and purge AcerInputFix.' + #13#10 +
      '' + #13#10 +
      'Click Next to remove the app, startup task, shortcuts,' + #13#10 +
      'program files, stats, and logs.' + #13#10 +
      '' + #13#10 +
      'Each task will be shown as it runs. Then you will get a summary.'
  else
    RemovalMemo.Text :=
      'Ready to uninstall AcerInputFix.' + #13#10 +
      '' + #13#10 +
      'Click Next to remove the app, startup task, shortcuts,' + #13#10 +
      'and program files. Stats/logs will be kept if present.' + #13#10 +
      '' + #13#10 +
      'Each task will be shown as it runs. Then you will get a summary.';
end;

procedure ModeRadioClick(Sender: TObject);
begin
  if ModeInstallRadio.Checked then
    WizardMode := ModeInstall
  else if ModeUninstallRadio.Checked then
    WizardMode := ModeUninstall
  else
    WizardMode := ModePurge;
end;

procedure ConsentCheckClick(Sender: TObject);
begin
  WizardForm.NextButton.Enabled := ConsentCheck.Checked;
end;

procedure InitializeWizard();
begin
  InstallSummary := '';
  WizardMode := ModeInstall;
  RemovalDone := False;
  DidRegisterStartup := False;
  DidStopApp := False;

  ModePage := CreateCustomPage(
    wpWelcome,
    'Choose an action',
    'What do you want this wizard to do?'
  );

  ModeInstallRadio := TNewRadioButton.Create(ModePage);
  ModeInstallRadio.Parent := ModePage.Surface;
  ModeInstallRadio.Left := ScaleX(0);
  ModeInstallRadio.Top := ScaleY(8);
  ModeInstallRadio.Width := ModePage.SurfaceWidth;
  ModeInstallRadio.Height := ScaleY(40);
  ModeInstallRadio.Caption :=
    'Install or upgrade AcerInputFix';
  ModeInstallRadio.Checked := True;
  ModeInstallRadio.OnClick := @ModeRadioClick;

  ModeUninstallRadio := TNewRadioButton.Create(ModePage);
  ModeUninstallRadio.Parent := ModePage.Surface;
  ModeUninstallRadio.Left := ScaleX(0);
  ModeUninstallRadio.Top := ModeInstallRadio.Top + ScaleY(44);
  ModeUninstallRadio.Width := ModePage.SurfaceWidth;
  ModeUninstallRadio.Height := ScaleY(40);
  ModeUninstallRadio.Caption :=
    'Uninstall AcerInputFix (keep stats/logs)';
  ModeUninstallRadio.OnClick := @ModeRadioClick;

  ModePurgeRadio := TNewRadioButton.Create(ModePage);
  ModePurgeRadio.Parent := ModePage.Surface;
  ModePurgeRadio.Left := ScaleX(0);
  ModePurgeRadio.Top := ModeUninstallRadio.Top + ScaleY(44);
  ModePurgeRadio.Width := ModePage.SurfaceWidth;
  ModePurgeRadio.Height := ScaleY(40);
  ModePurgeRadio.Caption :=
    'Uninstall and purge (remove stats/logs too)';
  ModePurgeRadio.OnClick := @ModeRadioClick;

  ConsentPage := CreateCustomPage(
    ModePage.ID,
    'Consent — confirm this action',
    'Please read carefully. Click Next only if you agree.'
  );

  ConsentMemo := TNewMemo.Create(ConsentPage);
  ConsentMemo.Parent := ConsentPage.Surface;
  ConsentMemo.Left := ScaleX(0);
  ConsentMemo.Top := ScaleY(0);
  ConsentMemo.Width := ConsentPage.SurfaceWidth;
  ConsentMemo.Height := ScaleY(180);
  ConsentMemo.ScrollBars := ssVertical;
  ConsentMemo.ReadOnly := True;
  ConsentMemo.WordWrap := True;

  ConsentCheck := TNewCheckBox.Create(ConsentPage);
  ConsentCheck.Parent := ConsentPage.Surface;
  ConsentCheck.Left := ScaleX(0);
  ConsentCheck.Top := ConsentMemo.Top + ConsentMemo.Height + ScaleY(12);
  ConsentCheck.Width := ConsentPage.SurfaceWidth;
  ConsentCheck.Height := ScaleY(40);
  ConsentCheck.Caption :=
    'I understand what this wizard will do and want to continue.';
  ConsentCheck.OnClick := @ConsentCheckClick;
  ConsentCheck.Checked := False;

  RemovalPage := CreateCustomPage(
    ConsentPage.ID,
    'Confirm removal',
    'Last step before files and tasks are removed.'
  );

  RemovalMemo := TNewMemo.Create(RemovalPage);
  RemovalMemo.Parent := RemovalPage.Surface;
  RemovalMemo.Left := ScaleX(0);
  RemovalMemo.Top := ScaleY(0);
  RemovalMemo.Width := RemovalPage.SurfaceWidth;
  RemovalMemo.Height := RemovalPage.SurfaceHeight;
  RemovalMemo.ScrollBars := ssVertical;
  RemovalMemo.ReadOnly := True;
  RemovalMemo.WordWrap := True;

  UpdateConsentText();
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;

  if IsRemovalMode() then
  begin
    if RemovalDone then
    begin
      // After removal, jump straight to the finished summary.
      Result := PageID <> wpFinished;
      Exit;
    end;

    // Before removal runs: only mode/consent/removal pages (+ welcome).
    if (PageID = wpSelectDir) or
       (PageID = wpSelectProgramGroup) or
       (PageID = wpSelectTasks) or
       (PageID = wpReady) or
       (PageID = wpPreparing) or
       (PageID = wpInstalling) then
      Result := True;
  end
  else
  begin
    // Install mode never shows the removal confirm page.
    if PageID = RemovalPage.ID then
      Result := True;
  end;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = ModePage.ID then
  begin
    WizardForm.NextButton.Enabled := True;
    WizardForm.NextButton.Caption := SetupMessage(msgButtonNext);
  end
  else if CurPageID = ConsentPage.ID then
  begin
    ModeRadioClick(nil);
    UpdateConsentText();
    WizardForm.NextButton.Enabled := ConsentCheck.Checked;
    WizardForm.NextButton.Caption := SetupMessage(msgButtonNext);
  end
  else if CurPageID = RemovalPage.ID then
  begin
    UpdateRemovalText();
    WizardForm.NextButton.Enabled := True;
    WizardForm.NextButton.Caption := SetupMessage(msgButtonNext);
  end
  else if CurPageID = wpReady then
  begin
    WizardForm.NextButton.Caption := SetupMessage(msgButtonNext);
  end
  else if CurPageID = wpFinished then
  begin
    if InstallSummary <> '' then
      WizardForm.FinishedLabel.Caption := InstallSummary;
    WizardForm.NextButton.Caption := SetupMessage(msgButtonFinish);

    // Hide "Start now" after uninstall/purge.
    if IsRemovalMode() then
      WizardForm.RunList.Visible := False;
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;

  if CurPageID = ModePage.ID then
    ModeRadioClick(nil)
  else if CurPageID = ConsentPage.ID then
  begin
    if not ConsentCheck.Checked then
    begin
      MsgBox(
        'Check the consent box to confirm you understand what this wizard will do, then click Next.',
        mbInformation,
        MB_OK
      );
      Result := False;
    end;
  end
  else if CurPageID = RemovalPage.ID then
  begin
    PerformRemoval(WizardMode = ModePurge);
    // ShouldSkipPage then advances to wpFinished.
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  NeedsRestart := False;
  Result := '';
  if IsRemovalMode() then
    Result := 'Internal error: install step reached in removal mode.';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  StartupOk: Boolean;
begin
  if not IsInstallMode() then
    Exit;

  if CurStep = ssInstall then
  begin
    StepCount := 4;
    StepIndex := 0;
    InstallSummary := '';

    ProgressPage := CreateOutputProgressPage(
      'Installing AcerInputFix',
      'Each task is listed as it runs. Please wait.'
    );
    ProgressPage.Show;

    Inc(StepIndex);
    ShowStep(
      'Stopping AcerInputFix if it is running…',
      'Closing tray app so files can be replaced'
    );
    StopApp();
    AppendSummary('• Stopped any running AcerInputFix process');
  end
  else if CurStep = ssPostInstall then
  begin
    try
      if ProgressPage = nil then
      begin
        ProgressPage := CreateOutputProgressPage(
          'Finishing AcerInputFix setup',
          'Each task is listed as it runs. Please wait.'
        );
        ProgressPage.Show;
        StepCount := 2;
        StepIndex := 0;
      end;

      AppendSummary('• Installed AcerInputFix {#MyAppVersion} to:');
      AppendSummary('    ' + ExpandConstant('{app}'));

      Inc(StepIndex);
      if WizardIsTaskSelected('startup') then
      begin
        ShowStep(
          'Registering elevated sign-in startup task…',
          'Task name: {#MyTaskName} (RunLevel Highest)'
        );
        StartupOk := RegisterLogonTask();
        DidRegisterStartup := StartupOk;
        if StartupOk then
          AppendSummary('• Registered logon task "{#MyTaskName}" (elevated)')
        else
          AppendSummary('• WARNING: Could not register the logon startup task');
      end
      else
      begin
        ShowStep('Removing prior sign-in startup task (if any)…', '');
        UnregisterLogonTask();
        AppendSummary('• Sign-in startup task not selected (any prior task removed)');
      end;

      Inc(StepIndex);
      ShowStep('Finishing…', 'Preparing summary');
      AppendSummary('');
      AppendSummary('Setup completed successfully.');
      AppendSummary('Click Finish to exit this wizard.');
      if WizardIsTaskSelected('startup') and DidRegisterStartup then
        AppendSummary('AcerInputFix will also start automatically when you sign in.');

      Sleep(400);
    finally
      if ProgressPage <> nil then
      begin
        ProgressPage.Hide;
        ProgressPage := nil;
      end;
    end;
  end;
end;

procedure DeinitializeSetup();
begin
  if ProgressPage <> nil then
  begin
    ProgressPage.Hide;
    ProgressPage := nil;
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopApp();
    UnregisterLogonTask();
  end;
end;
