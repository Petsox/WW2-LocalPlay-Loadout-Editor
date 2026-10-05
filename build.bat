@echo off
rem Builds WW2_LocalPlay_Loadout_Editor.exe with the C# compiler that ships with Windows (.NET Framework 4).
setlocal
cd /d "%~dp0"
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" (
  echo .NET Framework 4 compiler not found at %CSC%
  exit /b 1
)
"%CSC%" /nologo /target:winexe /platform:x64 /optimize+ /win32icon:app.ico ^
  /out:WW2_LocalPlay_Loadout_Editor.exe ^
  /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Core.dll /r:System.Web.Extensions.dll ^
  LocalPlayLoadout.cs
if errorlevel 1 exit /b 1
echo Built WW2_LocalPlay_Loadout_Editor.exe
