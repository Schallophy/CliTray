@echo off
setlocal
cd /d "%~dp0"

where nuget >nul 2>&1
if errorlevel 1 (
    if exist "%~dp0nuget.exe" (
        set "NUGET=%~dp0nuget.exe"
    ) else (
        echo Downloading nuget.exe...
        curl -L -o "%~dp0nuget.exe" https://dist.nuget.org/win-x86-commandline/latest/nuget.exe
        if errorlevel 1 (
            echo Failed to download nuget.exe
            exit /b 1
        )
        set "NUGET=%~dp0nuget.exe"
    )
) else (
    set "NUGET=nuget"
)

set "PKGS=packages"

if not exist "%PKGS%\WPF-UI.4.3.0\lib\net472\Wpf.Ui.dll" (
    "%NUGET%" install WPF-UI -Version 4.3.0 -OutputDirectory "%PKGS%" -NonInteractive || exit /b 1
)
if not exist "%PKGS%\WPF-UI.Abstractions.4.3.0\lib\net472\Wpf.Ui.Abstractions.dll" (
    "%NUGET%" install WPF-UI.Abstractions -Version 4.3.0 -OutputDirectory "%PKGS%" -NonInteractive || exit /b 1
)
if not exist "%PKGS%\System.Memory.4.6.3\lib\net462\System.Memory.dll" (
    "%NUGET%" install System.Memory -Version 4.6.3 -OutputDirectory "%PKGS%" -NonInteractive || exit /b 1
)
if not exist "%PKGS%\System.Buffers.4.6.1\lib\net462\System.Buffers.dll" (
    "%NUGET%" install System.Buffers -Version 4.6.1 -OutputDirectory "%PKGS%" -NonInteractive || exit /b 1
)
if not exist "%PKGS%\System.Numerics.Vectors.4.6.1\lib\net462\System.Numerics.Vectors.dll" (
    "%NUGET%" install System.Numerics.Vectors -Version 4.6.1 -OutputDirectory "%PKGS%" -NonInteractive || exit /b 1
)
if not exist "%PKGS%\System.Runtime.CompilerServices.Unsafe.6.1.2\lib\net462\System.Runtime.CompilerServices.Unsafe.dll" (
    "%NUGET%" install System.Runtime.CompilerServices.Unsafe -Version 6.1.2 -OutputDirectory "%PKGS%" -NonInteractive || exit /b 1
)

rem The project points TargetFrameworkRootPath at packages\net48\build\, so the
rem reference assemblies package is renamed from its id.version folder.
if not exist "%PKGS%\net48\build\.NETFramework\v4.8" (
    if exist "%PKGS%\Microsoft.NETFramework.ReferenceAssemblies.net48.1.0.3" (
        move "%PKGS%\Microsoft.NETFramework.ReferenceAssemblies.net48.1.0.3" "%PKGS%\net48" >nul
    ) else (
        "%NUGET%" install Microsoft.NETFramework.ReferenceAssemblies.net48 -Version 1.0.3 -OutputDirectory "%PKGS%" -NonInteractive || exit /b 1
        move "%PKGS%\Microsoft.NETFramework.ReferenceAssemblies.net48.1.0.3" "%PKGS%\net48" >nul
        if errorlevel 1 exit /b 1
    )
)

if not exist "lib\Newtonsoft.Json.dll" (
    if not exist "%PKGS%\Newtonsoft.Json.13.0.3\lib\net45\Newtonsoft.Json.dll" (
        "%NUGET%" install Newtonsoft.Json -Version 13.0.3 -OutputDirectory "%PKGS%" -NonInteractive || exit /b 1
    )
    if not exist "lib" mkdir lib
    copy /y "%PKGS%\Newtonsoft.Json.13.0.3\lib\net45\Newtonsoft.Json.dll" "lib\Newtonsoft.Json.dll" >nul
    if errorlevel 1 exit /b 1
)

echo Dependencies ready.
endlocal
