@echo off
rem Builds LoginChanger.exe with the C# compiler that ships with .NET Framework (nothing to install)
set CSC=%windir%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%windir%\Microsoft.NET\Framework\v4.0.30319\csc.exe
"%CSC%" /nologo /target:winexe /optimize+ /codepage:65001 /out:LoginChanger.exe /win32manifest:src\app.manifest /win32icon:src\app.ico /r:System.dll /r:System.Core.dll /r:System.Data.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll src\Program.cs
if errorlevel 1 (echo BUILD FAILED & exit /b 1) else (echo OK: LoginChanger.exe)