@echo off
rem Builds SDRFix.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
if not exist obj mkdir obj

"%CSC%" /nologo /target:exe /out:obj\MakeIcon.exe /r:System.Drawing.dll tools\MakeIcon.cs src\Theme.cs || exit /b 1
obj\MakeIcon.exe obj\app.ico || exit /b 1

"%CSC%" /nologo /target:winexe /optimize+ /out:SDRFix.exe /win32icon:obj\app.ico /win32manifest:src\app.manifest ^
  /r:System.dll /r:System.Core.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll src\*.cs || exit /b 1

echo Built %~dp0SDRFix.exe
