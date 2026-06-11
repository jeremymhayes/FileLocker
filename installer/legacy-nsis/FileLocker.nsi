; Legacy FileLocker NSIS installer script.
; Active packaging and updates use Inno Setup. This file is kept only for
; historical reference and should not be used for new releases.

!include "MUI2.nsh"
!include "FileFunc.nsh"
!include "x64.nsh"

!ifndef APP_NAME
  !define APP_NAME "FileLocker"
!endif

!ifndef APP_PUBLISHER
  !define APP_PUBLISHER "Jeremy Hayes"
!endif

!ifndef APP_EXE
  !define APP_EXE "FileLocker.exe"
!endif

!ifndef APP_VERSION
  !define APP_VERSION "1.3.1.0"
!endif

!ifndef APP_FILE_VERSION
  !define APP_FILE_VERSION "${APP_VERSION}"
!endif

; Legacy default publish folder used by the old NSIS wrapper.
!ifndef PUBLISH_DIR
  !define PUBLISH_DIR "..\artifacts\nsis\publish"
!endif

!ifndef OUTPUT_DIR
  !define OUTPUT_DIR "."
!endif

!define APP_ID "FileLocker"
!define INSTALL_DIR "$PROGRAMFILES64\${APP_NAME}"
!define UNINSTALL_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\${APP_ID}"

!macro RequirePublishFile RelativePath
  !if /FileExists "${PUBLISH_DIR}\${RelativePath}"
    ; required publish file exists
  !else
    !error "PUBLISH_DIR is invalid. Missing required file: ${PUBLISH_DIR}\${RelativePath}"
  !endif
!macroend

; Fail at compile time if the publish folder is incomplete
!if /FileExists "${PUBLISH_DIR}\${APP_EXE}"
  ; publish folder looks valid
!else
  !error "PUBLISH_DIR is invalid. Expected to find ${APP_EXE} in: ${PUBLISH_DIR}"
!endif
!insertmacro RequirePublishFile "App.xbf"
!insertmacro RequirePublishFile "MainWindow.xbf"
!insertmacro RequirePublishFile "FileLocker.pri"
!insertmacro RequirePublishFile "Themes\Styles.xbf"
!insertmacro RequirePublishFile "Assets\StoreLogo.png"
!insertmacro RequirePublishFile "wwwroot\index.html"

Name "${APP_NAME}"
OutFile "${OUTPUT_DIR}\${APP_NAME}-Setup-${APP_VERSION}.exe"
InstallDir "${INSTALL_DIR}"
InstallDirRegKey HKLM "${UNINSTALL_KEY}" "InstallLocation"
RequestExecutionLevel admin
Unicode true
SetCompressor /SOLID lzma

BrandingText "${APP_PUBLISHER}"
ShowInstDetails show
ShowUnInstDetails show

VIProductVersion "${APP_FILE_VERSION}"
VIAddVersionKey "ProductName" "${APP_NAME}"
VIAddVersionKey "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey "FileVersion" "${APP_FILE_VERSION}"
VIAddVersionKey "ProductVersion" "${APP_FILE_VERSION}"
VIAddVersionKey "FileDescription" "FileLocker Installer"
VIAddVersionKey "LegalCopyright" "Copyright (c) 2026 Jeremy Hayes"

!define MUI_ABORTWARNING

; Only set custom icons if the .ico file exists
!if /FileExists "..\FileLocker\logo.ico"
  !define MUI_ICON "..\FileLocker\logo.ico"
  !define MUI_UNICON "..\FileLocker\logo.ico"
!endif

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "Launch ${APP_NAME}"
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "English"

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP|MB_OK "${APP_NAME} is distributed as a 64-bit installer."
    Abort
  ${EndIf}
FunctionEnd

Section "Install"
  SetRegView 64
  SetShellVarContext all
  SetOutPath "$INSTDIR"

  RMDir /r "$INSTDIR\wwwroot"
  RMDir /r "$INSTDIR\Assets"
  RMDir /r "$INSTDIR\Themes"

  File /r "${PUBLISH_DIR}\*.*"

  WriteUninstaller "$INSTDIR\Uninstall.exe"

  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"
  CreateShortcut "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk" "$INSTDIR\Uninstall.exe"
  CreateShortcut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}"

  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayName" "${APP_NAME}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE},0"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "UninstallString" '"$INSTDIR\Uninstall.exe"'
  WriteRegStr HKLM "${UNINSTALL_KEY}" "QuietUninstallString" '"$INSTDIR\Uninstall.exe" /S'
  WriteRegStr HKLM "${UNINSTALL_KEY}" "URLInfoAbout" "https://github.com/jeremymhayes/FileLocker"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "HelpLink" "https://github.com/jeremymhayes/FileLocker/issues"
  WriteRegStr HKLM "${UNINSTALL_KEY}" "Readme" "https://github.com/jeremymhayes/FileLocker#readme"
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoModify" 1
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "NoRepair" 1

  ${GetSize} "$INSTDIR" "/S=0K" $0 $1 $2
  IntFmt $0 "0x%08X" $0
  WriteRegDWORD HKLM "${UNINSTALL_KEY}" "EstimatedSize" $0
SectionEnd

Section "Uninstall"
  SetRegView 64
  SetShellVarContext all

  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\Uninstall ${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"
  Delete "$DESKTOP\${APP_NAME}.lnk"

  Delete "$INSTDIR\Uninstall.exe"
  RMDir /r "$INSTDIR"

  DeleteRegKey HKLM "${UNINSTALL_KEY}"
SectionEnd
