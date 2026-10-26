Unicode true
!include "MUI2.nsh"
!include "LogicLib.nsh"
!include "x64.nsh"
!include "WinVer.nsh"

!define APP_NAME "NewScope"
!ifndef APP_VERSION
!define APP_VERSION "1.0.4"
!endif
!ifndef APP_FILE_VERSION
!define APP_FILE_VERSION "${APP_VERSION}.0"
!endif
!define APP_PUBLISHER "NewScope"
!define APP_EXE "NewScope.exe"
!define APP_REG "Software\NewScope"
!define UNINST_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\NewScope"
!define LEGACY_INNO_KEY "Software\Microsoft\Windows\CurrentVersion\Uninstall\{0124D37A-9BC8-4EA8-BADB-3897E104E3F7}_is1"
!define UNINST_EXE "Uninstall.exe"

Name "${APP_NAME}"
OutFile "..\dist\NewScope-Setup-win-x64.exe"
InstallDir "$LOCALAPPDATA\Programs\${APP_NAME}"
InstallDirRegKey HKCU "${APP_REG}" "InstallDir"
RequestExecutionLevel user
SetCompressor /SOLID lzma
SetCompressorDictSize 64
CRCCheck on
ManifestDPIAware true
ManifestSupportedOS win10
VIProductVersion "${APP_FILE_VERSION}"
VIAddVersionKey /LANG=1033 "ProductName" "${APP_NAME}"
VIAddVersionKey /LANG=1033 "ProductVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "CompanyName" "${APP_PUBLISHER}"
VIAddVersionKey /LANG=1033 "FileDescription" "${APP_NAME} Setup"
VIAddVersionKey /LANG=1033 "FileVersion" "${APP_VERSION}"
VIAddVersionKey /LANG=1033 "LegalCopyright" "${APP_PUBLISHER}"

!define MUI_ABORTWARNING
!define MUI_ICON "..\src\NewScope\Assets\newscope.ico"
!define MUI_UNICON "..\src\NewScope\Assets\newscope.ico"
!define MUI_LANGDLL_REGISTRY_ROOT "HKCU"
!define MUI_LANGDLL_REGISTRY_KEY "${APP_REG}\Installer"
!define MUI_LANGDLL_REGISTRY_VALUENAME "Language"
!define MUI_FINISHPAGE_RUN "$INSTDIR\${APP_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT "$(LaunchApp)"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_COMPONENTS
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH
!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_LANGUAGE "SimpChinese"
!insertmacro MUI_LANGUAGE "English"

LangString SecApp ${LANG_SIMPCHINESE} "NewScope（必需）"
LangString SecApp ${LANG_ENGLISH} "NewScope (required)"
LangString SecAppDesc ${LANG_SIMPCHINESE} "安装 NewScope 主程序和 J-Link 采样组件。"
LangString SecAppDesc ${LANG_ENGLISH} "Install NewScope and its J-Link sampling components."
LangString SecDesktop ${LANG_SIMPCHINESE} "创建桌面快捷方式"
LangString SecDesktop ${LANG_ENGLISH} "Create a desktop shortcut"
LangString SecDesktopDesc ${LANG_SIMPCHINESE} "在桌面创建 NewScope 快捷方式。"
LangString SecDesktopDesc ${LANG_ENGLISH} "Create a NewScope shortcut on the desktop."
LangString LaunchApp ${LANG_SIMPCHINESE} "运行 NewScope"
LangString LaunchApp ${LANG_ENGLISH} "Run NewScope"
LangString AppRunning ${LANG_SIMPCHINESE} "NewScope 正在运行。请先退出 NewScope，然后重新运行安装程序。"
LangString AppRunning ${LANG_ENGLISH} "NewScope is running. Please close it and run the installer again."
LangString NeedX64 ${LANG_SIMPCHINESE} "NewScope 需要 64 位 Windows 10 或更高版本。"
LangString NeedX64 ${LANG_ENGLISH} "NewScope requires 64-bit Windows 10 or later."
LangString ShortcutDesc ${LANG_SIMPCHINESE} "J-Link 变量示波器"
LangString ShortcutDesc ${LANG_ENGLISH} "J-Link variable scope"

Function CheckRunning
  FindWindow $0 "" "NewScope — J-Link 变量示波器"
  ${If} $0 == 0
    FindWindow $0 "" "NewScope — J-Link Variable Scope"
  ${EndIf}
  ${If} $0 != 0
    ${IfNot} ${Silent}
      MessageBox MB_ICONSTOP "$(AppRunning)"
    ${EndIf}
    SetErrorLevel 2
    Abort
  ${EndIf}
FunctionEnd

Function .onInit
  ${IfNot} ${RunningX64}
    MessageBox MB_ICONSTOP "$(NeedX64)"
    SetErrorLevel 2
    Abort
  ${EndIf}
  ${IfNot} ${AtLeastWin10}
    MessageBox MB_ICONSTOP "$(NeedX64)"
    SetErrorLevel 2
    Abort
  ${EndIf}
  SetRegView 64
  SetShellVarContext current
  Call CheckRunning
  ${IfNot} ${Silent}
    !insertmacro MUI_LANGDLL_DISPLAY
  ${EndIf}
FunctionEnd

Function un.onInit
  SetRegView 64
  SetShellVarContext current
  Call un.CheckRunning
FunctionEnd

Function un.CheckRunning
  FindWindow $0 "" "NewScope — J-Link 变量示波器"
  ${If} $0 == 0
    FindWindow $0 "" "NewScope — J-Link Variable Scope"
  ${EndIf}
  ${If} $0 != 0
    ${IfNot} ${Silent}
      MessageBox MB_ICONSTOP "$(AppRunning)"
    ${EndIf}
    SetErrorLevel 2
    Abort
  ${EndIf}
FunctionEnd

Section "$(SecApp)" SEC_APP
  SectionIn RO
  SetShellVarContext current
  SetOutPath "$INSTDIR"

  Delete "$INSTDIR\unins000.exe"
  Delete "$INSTDIR\unins000.dat"
  Delete "$INSTDIR\unins000.msg"

  File /r "..\dist\publish\*.*"
  WriteUninstaller "$INSTDIR\${UNINST_EXE}"
  ${If} "$INSTDIR" == "$LOCALAPPDATA\Programs\${APP_NAME}"
    DeleteRegKey HKCU "${LEGACY_INNO_KEY}"
  ${EndIf}

  CreateDirectory "$SMPROGRAMS\${APP_NAME}"
  CreateShortCut "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0 SW_SHOWNORMAL "" "$(ShortcutDesc)"
  WriteRegStr HKCU "${APP_REG}" "InstallDir" "$INSTDIR"

  WriteRegStr HKCU "${UNINST_KEY}" "DisplayName" "${APP_NAME} ${APP_VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayVersion" "${APP_VERSION}"
  WriteRegStr HKCU "${UNINST_KEY}" "Publisher" "${APP_PUBLISHER}"
  WriteRegStr HKCU "${UNINST_KEY}" "DisplayIcon" "$INSTDIR\${APP_EXE},0"
  WriteRegStr HKCU "${UNINST_KEY}" "InstallLocation" "$INSTDIR"
  WriteRegStr HKCU "${UNINST_KEY}" "UninstallString" '"$INSTDIR\${UNINST_EXE}"'
  WriteRegStr HKCU "${UNINST_KEY}" "QuietUninstallString" '"$INSTDIR\${UNINST_EXE}" /S'
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoModify" 1
  WriteRegDWORD HKCU "${UNINST_KEY}" "NoRepair" 1
SectionEnd

Section "$(SecDesktop)" SEC_DESKTOP
  SetShellVarContext current
  CreateShortCut "$DESKTOP\${APP_NAME}.lnk" "$INSTDIR\${APP_EXE}" "" "$INSTDIR\${APP_EXE}" 0 SW_SHOWNORMAL "" "$(ShortcutDesc)"
SectionEnd

!insertmacro MUI_FUNCTION_DESCRIPTION_BEGIN
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_APP} "$(SecAppDesc)"
  !insertmacro MUI_DESCRIPTION_TEXT ${SEC_DESKTOP} "$(SecDesktopDesc)"
!insertmacro MUI_FUNCTION_DESCRIPTION_END

Section "Uninstall"
  SetShellVarContext current
  Delete "$DESKTOP\${APP_NAME}.lnk"
  Delete "$SMPROGRAMS\${APP_NAME}\${APP_NAME}.lnk"
  RMDir "$SMPROGRAMS\${APP_NAME}"

  Delete "$INSTDIR\${UNINST_EXE}"
  RMDir /r "$INSTDIR"

  DeleteRegKey HKCU "${UNINST_KEY}"
  DeleteRegKey HKCU "${APP_REG}"
SectionEnd
