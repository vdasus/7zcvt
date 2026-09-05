@echo off
rem Native AOT build. Needs the MSVC toolset (Visual Studio "Desktop development with C++").
setlocal enabledelayedexpansion
set VCVARS=

for /f "usebackq tokens=*" %%i in (`"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -prerelease -products * -property installationPath 2^>nul`) do (
  if exist "%%i\VC\Auxiliary\Build\vcvarsall.bat" set VCVARS=%%i\VC\Auxiliary\Build\vcvarsall.bat
)

if "!VCVARS!"=="" (
  echo The MSVC desktop toolset is missing ^(VC\Auxiliary\Build\vcvarsall.bat and lib\x64 not found^).
  echo Add it with the Visual Studio Installer:
  echo   setup.exe modify --installPath "^<VS path^>" --add Microsoft.VisualStudio.Component.VC.Tools.x86.x64 --includeRecommended
  echo.
  echo Building the single-file fallback instead ^(~11 MB, no C++ toolset needed^).
  dotnet publish "%~dp0src\7zcvt.csproj" -c Release -r win-x64 --self-contained -o "%~dp0dist" ^
    -p:PublishAot=false -p:PublishSingleFile=true -p:PublishTrimmed=true -p:EnableCompressionInSingleFile=true %*
  exit /b !errorlevel!
)

echo Using !VCVARS!
call "!VCVARS!" x64 >nul || exit /b 1
dotnet publish "%~dp0src\7zcvt.csproj" -c Release -r win-x64 -o "%~dp0dist" %* || exit /b 1
del /q "%~dp0dist\*.pdb" 2>nul
"%~dp0dist\7zcvt.exe" --version
echo Published to %~dp0dist\7zcvt.exe
