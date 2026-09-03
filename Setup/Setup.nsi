;NSIS Modern User Interface
;GTA NETWORK INSTALLER
;
;  makensis /DSOURCE_DIR=<folder with the packaged client> Setup.nsi
;  (the GitHub Actions windows job passes the output of eng/package-client.ps1)

  !include "MUI2.nsh"

  !ifndef SOURCE_DIR
    !define SOURCE_DIR "C:\GTANetworkProd"
  !endif
  !ifndef OUT_FILE
    !define OUT_FILE "GTANSetup.exe"
  !endif

  Name "GTA Network"
  OutFile "${OUT_FILE}"

  InstallDir "C:\GTANetwork"

  RequestExecutionLevel admin

  !define MUI_ABORTWARNING
  
  !insertmacro MUI_PAGE_LICENSE "License.txt"
  !insertmacro MUI_PAGE_DIRECTORY
  !insertmacro MUI_PAGE_INSTFILES
  
  !insertmacro MUI_UNPAGE_CONFIRM
  !insertmacro MUI_UNPAGE_INSTFILES
  !insertmacro MUI_LANGUAGE "English"

Section "Client" SecDummy

  SetOutPath "$INSTDIR"

${If} ${FileExists} "$INSTDIR\*"
     RMDir /r "$INSTDIR"
${EndIf}

  File /r "${SOURCE_DIR}\*"

  CreateShortCut "$DESKTOP\GTA Network.lnk" "$INSTDIR\GTANLauncher.exe" ""
  WriteUninstaller "$INSTDIR\Uninstall.exe"

SectionEnd

Section "Uninstall"

  Delete "$INSTDIR\Uninstall.exe"
  Delete "$DESKTOP\GTA Network.lnk"
  RMDir /r /REBOOTOK "$INSTDIR"
  DeleteRegKey /ifempty HKLM "HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V\GTANetworkInstallDir"

SectionEnd
