#ifndef PublishDir
  #error Supply /DPublishDir= followed by the absolute path of the win-x64 publish folder.
#endif
#ifndef BuildOutput
  #define BuildOutput "output"
#endif
#ifndef BuildVersion
  #define BuildVersion "1.6.5"
#endif

[Setup]
AppId={{C79DF90C-B339-4190-8A4C-BC3E619780E2}
AppName=File Inventory
AppVersion={#BuildVersion}
AppPublisher=File Inventory
DefaultDirName={localappdata}\Programs\File Inventory
DefaultGroupName=File Inventory
DisableDirPage=no
UsePreviousAppDir=yes
UsePreviousTasks=no
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#BuildOutput}
OutputBaseFilename=FileInventory_Setup_x64
Compression=lzma2/ultra64
LZMAUseSeparateProcess=yes
LZMADictionarySize=65536
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\Assets\FileInventory.ico
CloseApplications=yes
RestartApplications=no
UninstallDisplayIcon={app}\FileInventory.exe
UninstallDisplayName=File Inventory
VersionInfoVersion={#BuildVersion}.0
VersionInfoDescription=File Inventory Setup

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{userprograms}\File Inventory"; Filename: "{app}\FileInventory.exe"; WorkingDir: "{app}"

[Tasks]
Name: "linguistic_en"; Description: "Download English Linguistic Pack (~29 MB) for concept-based Deep Search"; GroupDescription: "Optional Advanced Linguistic Search:"; Flags: unchecked exclusive
Name: "linguistic_multi"; Description: "Download Multilingual (EN + NL) Linguistic Pack for concept-based Deep Search"; GroupDescription: "Optional Advanced Linguistic Search:"; Flags: unchecked exclusive

[Run]
Filename: "{app}\FileInventory.exe"; Parameters: "--install-pack en"; StatusMsg: "Downloading English Linguistic Pack..."; Flags: runhidden waituntilterminated skipifsilent; Tasks: linguistic_en
Filename: "{app}\FileInventory.exe"; Parameters: "--install-pack multilingual"; StatusMsg: "Downloading Multilingual Linguistic Pack..."; Flags: runhidden waituntilterminated skipifsilent; Tasks: linguistic_multi
Filename: "{app}\FileInventory.exe"; Description: "Open File Inventory"; Flags: nowait postinstall skipifsilent
Filename: "{app}\FileInventory.exe"; Flags: nowait skipifnotsilent; Check: IsAppUpdate

[Code]
function HasCommandLineSwitch(const Switch: string): Boolean;
var
  I: Integer;
begin
  Result := False;
  for I := 1 to ParamCount do
  begin
    if CompareText(ParamStr(I), Switch) = 0 then
    begin
      Result := True;
      Exit;
    end;
  end;
end;

function IsAppUpdate: Boolean;
begin
  Result := HasCommandLineSwitch('/UPDATE');
end;

function IsSilentOrUpdate: Boolean;
begin
  Result := WizardSilent or HasCommandLineSwitch('/UPDATE') or
    HasCommandLineSwitch('/SILENT') or HasCommandLineSwitch('/VERYSILENT');
end;

function ExtractJsonString(const Json, Key: string): string;
var
  SearchKey, Rest: string;
  P, QuoteStart, QuoteEnd: Integer;
begin
  Result := '';
  SearchKey := '"' + Key + '"';
  P := Pos(SearchKey, Json);
  if P = 0 then
    Exit;

  Rest := Copy(Json, P + Length(SearchKey), MaxInt);
  P := Pos(':', Rest);
  if P = 0 then
    Exit;

  Rest := Copy(Rest, P + 1, MaxInt);
  QuoteStart := Pos('"', Rest);
  if QuoteStart = 0 then
    Exit;

  Rest := Copy(Rest, QuoteStart + 1, MaxInt);
  QuoteEnd := Pos('"', Rest);
  if QuoteEnd = 0 then
    Exit;

  Result := Copy(Rest, 1, QuoteEnd - 1);
end;

function NextVersionPart(var Ver: string): Integer;
var
  DotPos, I: Integer;
  Part, Digits: string;
begin
  if Ver = '' then
  begin
    Result := 0;
    Exit;
  end;

  DotPos := Pos('.', Ver);
  if DotPos > 0 then
  begin
    Part := Copy(Ver, 1, DotPos - 1);
    Delete(Ver, 1, DotPos);
  end
  else
  begin
    Part := Ver;
    Ver := '';
  end;

  Digits := '';
  for I := 1 to Length(Part) do
  begin
    if (Part[I] >= '0') and (Part[I] <= '9') then
      Digits := Digits + Part[I]
    else
      Break;
  end;

  Result := StrToIntDef(Digits, 0);
end;

function CompareVersions(const V1, V2: string): Integer;
var
  S1, S2: string;
  N1, N2: Integer;
begin
  S1 := V1;
  S2 := V2;
  while (S1 <> '') or (S2 <> '') do
  begin
    N1 := NextVersionPart(S1);
    N2 := NextVersionPart(S2);
    if N1 > N2 then
    begin
      Result := 1;
      Exit;
    end
    else if N1 < N2 then
    begin
      Result := -1;
      Exit;
    end;
  end;
  Result := 0;
end;

function InitializeSetup(): Boolean;
var
  WinHttp: Variant;
  ResponseText, TagName, RemoteVersion: string;
  FileName, DownloadUrl, TempFile, DestFile, PromptMsg: string;
  ErrorCode: Integer;
begin
  Result := True;

  if IsSilentOrUpdate then
    Exit;

  try
    WinHttp := CreateOleObject('WinHttp.WinHttpRequest.5.1');
    // 3-second timeout (resolve, connect, send, receive) so offline installs never hang
    WinHttp.SetTimeouts(3000, 3000, 3000, 3000);
    WinHttp.Open('GET', 'https://api.github.com/repos/Caspi7/file-inventory/releases/latest', False);
    WinHttp.SetRequestHeader('User-Agent', 'FileInventory-Setup/{#BuildVersion}');
    WinHttp.SetRequestHeader('Accept', 'application/vnd.github+json');
    WinHttp.Send('');

    if WinHttp.Status <> 200 then
      Exit;

    ResponseText := WinHttp.ResponseText;
  except
    // Offline or network error: seamlessly continue installing the bundled version
    Exit;
  end;

  TagName := Trim(ExtractJsonString(ResponseText, 'tag_name'));
  if TagName = '' then
    Exit;

  RemoteVersion := TagName;
  if (Length(RemoteVersion) > 0) and ((RemoteVersion[1] = 'v') or (RemoteVersion[1] = 'V')) then
    Delete(RemoteVersion, 1, 1);

  if CompareVersions(RemoteVersion, '{#BuildVersion}') <= 0 then
    Exit;

  PromptMsg := Format(
    'A newer version (%s) is available online (this installer contains %s).' + #13#10#13#10 +
    'Download and run the newer installer now? Click No to install %s offline.', [
    RemoteVersion, '{#BuildVersion}', '{#BuildVersion}']);

  if MsgBox(PromptMsg, mbConfirmation, MB_YESNO) <> IDYES then
    Exit;

  FileName := Format('FileInventory_Setup_x64_%s.exe', [RemoteVersion]);
  DownloadUrl := Format(
    'https://github.com/Caspi7/file-inventory/releases/download/%s/%s', [
    TagName, FileName]);

  try
    DownloadTemporaryFile(DownloadUrl, FileName, '', nil);
    TempFile := ExpandConstant('{tmp}\') + FileName;
    DestFile := GetTempDir + FileName;
    if CopyFile(TempFile, DestFile, False) then
    begin
      if ShellExec('', DestFile, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode) then
      begin
        Result := False;
        Exit;
      end;
    end;
  except
    // Download failed: inform user and fall back to bundled installer
  end;

  MsgBox(
    'Unable to download or launch the newer installer. Continuing with {#BuildVersion} offline.',
    mbInformation, MB_OK);
end;
