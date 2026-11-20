@echo off
setlocal
cd /d "%~dp0"
set "MSBUILD=%ProgramFiles(x86)%\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
if exist "%MSBUILD%" goto build
echo MSBuild not found: "%MSBUILD%"
exit /b 1
:build
"%MSBUILD%" CliTray.csproj -t:Rebuild -p:Configuration=Release -nologo -v:minimal
if errorlevel 1 exit /b 1
rem keep a copy at bin root so the usual double-click path stays current
copy /y "bin\Release\CliTray.exe" "bin\CliTray.exe" >nul 2>&1
if errorlevel 1 echo NOTE: bin\CliTray.exe is locked, not synced this time
echo.
echo Build succeeded: bin\Release\CliTray.exe
endlocal
