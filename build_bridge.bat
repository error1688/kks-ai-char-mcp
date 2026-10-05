@echo off
REM Rebuild AICharBridge.dll. The game must be closed first:
REM if the output DLL is locked, csc fails with CS0016 (or silently).
REM
REM All paths are variables -- no machine-specific directory is hardcoded:
REM   KKS_HOME  game root (contains CharaStudio_Data and BepInEx). Default: E:\game\KKS
REM   CSC       C# compiler. Default: the csc.exe shipped with .NET Framework 4.x
REM Example:  set "KKS_HOME=D:\Games\KKS" & build_bridge.bat

if not defined KKS_HOME set "KKS_HOME=E:\game\KKS"
if not defined CSC set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"

set "M=%KKS_HOME%\CharaStudio_Data\Managed"
set "OUT=%KKS_HOME%\BepInEx\plugins\KKS_AICharBridge.dll"

if not exist "%CSC%" (
  echo Compiler not found: %CSC%
  exit /b 1
)
if not exist "%M%\Assembly-CSharp.dll" (
  echo Game assemblies not found: %M%
  exit /b 1
)

REM Close the game, otherwise the output DLL is locked.
taskkill /F /IM CharaStudio.exe >nul 2>&1
taskkill /F /IM KoikatsuSunshine.exe >nul 2>&1
ping -n 5 127.0.0.1 >nul

cd /d "%~dp0BridgePlugin"
"%CSC%" -nologo -target:library -optimize+ -codepage:65001 -nowarn:1701,1702,1762 ^
  -out:"%OUT%" ^
  -r:"%KKS_HOME%\BepInEx\core\BepInEx.dll" ^
  -r:"%M%\UnityEngine.dll" -r:"%M%\UnityEngine.CoreModule.dll" ^
  -r:"%M%\UnityEngine.ScreenCaptureModule.dll" -r:"%M%\UnityEngine.InputLegacyModule.dll" ^
  -r:"%M%\UnityEngine.IMGUIModule.dll" -r:"%M%\UnityEngine.TextRenderingModule.dll" ^
  -r:"%M%\UnityEngine.ImageConversionModule.dll" -r:"%M%\UnityEngine.ParticleSystemModule.dll" ^
  -r:"%M%\Assembly-CSharp.dll" ^
  AICharBridge.cs
if errorlevel 1 (
  echo BUILD FAILED
  exit /b 1
)
echo BUILD OK
dir /-c "%OUT%" | findstr KKS_AICharBridge
