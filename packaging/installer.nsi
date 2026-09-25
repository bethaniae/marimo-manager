!include "MUI2.nsh"

Name "Marimo Launcher"
OutFile "MarimoLauncher-Setup.exe"
InstallDir "$PROGRAMFILES\MarimoLauncher"
RequestExecutionLevel admin
SetCompressor /SOLID lzma

!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_LANGUAGE "English"

!ifndef PUBLISH_DIR
  !define PUBLISH_DIR "publish"
!endif

Section "Install"
  SetOutPath "$INSTDIR"
  File /r "${PUBLISH_DIR}/*"

  CreateDirectory "$SMPROGRAMS"
  CreateShortcut "$SMPROGRAMS\MarimoLauncher.lnk" "$INSTDIR\MarimoLauncher.exe" "" "$INSTDIR\MarimoLauncher.exe" 0
  CreateShortcut "$DESKTOP\MarimoLauncher.lnk" "$INSTDIR\MarimoLauncher.exe" "" "$INSTDIR\MarimoLauncher.exe" 0

  WriteUninstaller "$INSTDIR\Uninstall.exe"
SectionEnd

Section "Uninstall"
  RMDir /r "$INSTDIR"
  Delete "$SMPROGRAMS\MarimoLauncher.lnk"
  Delete "$DESKTOP\MarimoLauncher.lnk"
SectionEnd
