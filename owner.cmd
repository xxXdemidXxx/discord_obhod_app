@echo off
setlocal
cd /d "%~dp0"
set "ownerCompiler=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%ownerCompiler%" (
  echo .NET Framework 4.8 is required.
  pause
  exit /b 1
)
"%ownerCompiler%" /nologo /target:winexe /main:ByPassOwner.OwnerProgram /platform:x64 /optimize+ /codepage:65001 /out:"tools\ByPass.Owner.exe" /win32manifest:"app.manifest" /win32icon:"assets\ByPass.ico" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll /reference:System.Security.dll "tools\OwnerAssistant.cs" "tools\OwnerKeyStore.cs"
if errorlevel 1 (
  echo Owner assistant build failed. Close its window if it is already open.
  pause
  exit /b 1
)
start "" "%~dp0tools\ByPass.Owner.exe" "%~dp0."
