@echo off
setlocal enabledelayedexpansion
title MLBB 1-Click LDPlayer IL2CPP Dumper

cd /d "%~dp0"

echo ==========================================================
echo       MLBB LDPlayer 1-Click Automated IL2CPP Dumper       
echo ==========================================================
echo.

where python >nul 2>nul
if %ERRORLEVEL% equ 0 (
    python "%~dp0dump_mlbb_ldplayer.py"
    goto :done
)

where py >nul 2>nul
if %ERRORLEVEL% equ 0 (
    py "%~dp0dump_mlbb_ldplayer.py"
    goto :done
)

echo [!] Python not detected in PATH.
echo Running direct batch pipeline...
echo.

:: 1. Find ADB
set "ADB=C:\LDPlayer\LDPlayer9\adb.exe"
if not exist "%ADB%" set "ADB=D:\LDPlayer\LDPlayer9\adb.exe"
if not exist "%ADB%" (
    where adb >nul 2>nul
    if %ERRORLEVEL% equ 0 (
        set "ADB=adb"
    ) else (
        echo [-] Could not locate adb.exe! Please ensure LDPlayer is installed.
        pause
        exit /b 1
    )
)
echo [*] Using ADB: %ADB%

:: 2. Find Dumper
set "DUMPER=%~dp0build\Il2CppDumper.exe"
if not exist "%DUMPER%" set "DUMPER=%~dp0src\Il2CppDumper.Cli\bin\Release\net9.0\win-x64\Il2CppDumper.exe"
if not exist "%DUMPER%" (
    echo [-] Could not find Il2CppDumper.exe!
    pause
    exit /b 1
)
echo [*] Using Dumper: %DUMPER%

:: 3. Check Device
for /f "tokens=1" %%d in ('"%ADB%" devices ^| findstr /v "List" ^| findstr "device"') do (
    set "DEVICE=%%d"
    goto :device_found
)
echo [-] No active LDPlayer emulator detected. Please start LDPlayer.
pause
exit /b 1

:device_found
echo [+] Connected to emulator: %DEVICE%

:: 4. Resolve package and pull files
set "STAGING=%USERPROFILE%\Documents\MLBB_Dumps\staging"
set "OUTPUT=%USERPROFILE%\Documents\MLBB_Dumps\output"
if not exist "%STAGING%" mkdir "%STAGING%"
if not exist "%OUTPUT%" mkdir "%OUTPUT%"

echo [*] Pulling libil2cpp.so...
for /f "tokens=2 delims=:" %%p in ('"%ADB%" -s %DEVICE% shell pm path com.mobile.legends ^| findstr "base.apk"') do (
    set "APK_PATH=%%p"
)
for %%F in ("%APK_PATH%") do set "APP_DIR=%%~dpF"
set "APP_DIR=%APP_DIR:\=/%"

"%ADB%" -s %DEVICE% pull "%APP_DIR%lib/arm64/libil2cpp.so" "%STAGING%\libil2cpp.so" >nul 2>nul
if not exist "%STAGING%\libil2cpp.so" (
    "%ADB%" -s %DEVICE% pull "%APP_DIR%lib/arm/libil2cpp.so" "%STAGING%\libil2cpp.so" >nul 2>nul
)

echo [*] Pulling partitioned metadata...
"%ADB%" -s %DEVICE% pull "/sdcard/Android/data/com.mobile.legends/files/dragon2017/assets/UnityData_NEW/Managed/Metadata/global-metadata.dat" "%STAGING%\global-metadata.dat" >nul 2>nul
"%ADB%" -s %DEVICE% pull "/sdcard/Android/data/com.mobile.legends/files/dragon2017/assets/UnityData_NEW/Managed/Metadata/global-first-metadata.dat" "%STAGING%\global-first-metadata.dat" >nul 2>nul
"%ADB%" -s %DEVICE% pull "/sdcard/Android/data/com.mobile.legends/files/dragon2017/assets/UnityData_NEW/Managed/Metadata/global-csharp-metadata.dat" "%STAGING%\global-csharp-metadata.dat" >nul 2>nul

echo [*] Running Il2CppDumper...
"%DUMPER%" "%STAGING%\libil2cpp.so" "%STAGING%\global-metadata.dat" "%OUTPUT%"

echo [+] Done! Opening output folder...
explorer "%OUTPUT%"

:done
echo.
echo Process finished.
pause
