@echo off
setlocal
cd /d "%~dp0"

set "FW=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319"
if not exist "%FW%\csc.exe" set "FW=%WINDIR%\Microsoft.NET\Framework\v4.0.30319"
if not exist "%FW%\csc.exe" (
  echo [!] csc.exe not found. .NET Framework 4.x is built into Windows 10/11 - check your system.
  pause
  exit /b 1
)

echo Building LimixProtocol.exe ...
"%FW%\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /out:LimixProtocol.exe ^
  /win32icon:limix.ico /win32manifest:app.manifest /resource:ui.xaml,ui.xaml ^
  /lib:"%FW%\WPF" /r:PresentationFramework.dll /r:PresentationCore.dll /r:WindowsBase.dll ^
  /r:System.Xaml.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll Limix.cs
if errorlevel 1 (
  echo.
  echo [!] Build failed. Copy the error text above and send it back.
  pause
  exit /b 1
)

echo.
echo Done: LimixProtocol.exe
if not exist "bin\winws.exe" (
  echo.
  echo [!] Note: the bin folder is not next to this file. Before running the program put the
  echo     bin folder ^(with winws.exe inside^) next to LimixProtocol.exe.
)
echo Run LimixProtocol.exe - it will ask for administrator rights ^(needed by WinDivert^).
pause
