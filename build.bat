@echo off
rem Builds WW2_LocalPlay_Unlocker.exe (full) and WW2_LocalPlay_Unlocker_Lite.exe (players' version, /define:LITE)
rem with the C# compiler that ships with Windows (.NET Framework 4).
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo .NET Framework 4 compiler not found at %CSC%
  exit /b 1
)
set REFS=/r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Web.Extensions.dll
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:app.ico %REFS% ^
  /out:WW2_LocalPlay_Unlocker.exe LocalPlayLoadout.cs
if errorlevel 1 exit /b 1
echo Built WW2_LocalPlay_Unlocker.exe
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:app.ico %REFS% /define:LITE /nowarn:169,649 ^
  /out:WW2_LocalPlay_Unlocker_Lite.exe LocalPlayLoadout.cs
if errorlevel 1 exit /b 1
echo Built WW2_LocalPlay_Unlocker_Lite.exe
