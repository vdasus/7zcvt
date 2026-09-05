@echo off
rem Builds, tags and publishes a release. Bump <Version> in src\7zcvt.csproj first.
setlocal enabledelayedexpansion

for /f "tokens=3 delims=<>" %%v in ('findstr /r "<Version>" "%~dp0src\7zcvt.csproj"') do set VER=%%v
if "%VER%"=="" echo Cannot read ^<Version^> from src\7zcvt.csproj & exit /b 1

for /f "delims=" %%s in ('git -C "%~dp0." status --porcelain') do (echo Working tree is dirty: commit or stash first. & exit /b 1)
git -C "%~dp0." rev-parse -q --verify "refs/tags/v%VER%" >nul && (echo Tag v%VER% already exists: bump ^<Version^> in src\7zcvt.csproj. & exit /b 1)

call "%~dp0build.cmd" || exit /b 1
"%~dp0dist\7zcvt.exe" --selftest || (echo Selftest failed, nothing was published. & exit /b 1)

set NOTES=%TEMP%\7zcvt-release-%VER%.md
> "%NOTES%" echo Single self-contained Windows x64 binary: no .NET runtime and no 7-Zip install required.
>>"%NOTES%" echo.
for /f %%h in ('powershell -NoProfile -Command "(Get-FileHash '%~dp0dist\7zcvt.exe' -Algorithm SHA256).Hash.ToLower()"') do set HASH=%%h
>>"%NOTES%" echo SHA-256: `!HASH!`
>>"%NOTES%" echo.
>>"%NOTES%" echo See the README for options and the data-safety rules.

git -C "%~dp0." tag -a "v%VER%" -m "7zcvt %VER%" || exit /b 1
git -C "%~dp0." push origin "v%VER%" || exit /b 1
rem gh may only be installed inside WSL; fall back to it rather than stopping half-released.
where gh >nul 2>&1
if errorlevel 1 (
  where wsl >nul 2>&1 || (echo Neither gh nor wsl is available: install GitHub CLI ^(winget install GitHub.cli^) and run 'gh release create v%VER% dist\7zcvt.exe'. & exit /b 1)
  for /f "delims=" %%p in ('wsl wslpath -a "%~dp0dist\7zcvt.exe"') do set EXE=%%p
  for /f "delims=" %%p in ('wsl wslpath -a "%NOTES%"') do set NOTESW=%%p
  wsl gh release create "v%VER%" "!EXE!" --title "7zcvt %VER%" --notes-file "!NOTESW!" || exit /b 1
) else (
  gh release create "v%VER%" "%~dp0dist\7zcvt.exe" --title "7zcvt %VER%" --notes-file "%NOTES%" || exit /b 1
)

echo Released v%VER%.
