@echo off
rem ---------------------------------------------------------------------------
rem  AI MAINTENANCE DIRECTIVE - DO NOT REMOVE
rem
rem  1. [ASCII only / CRLF] Do NOT put Chinese text or a UTF-8 BOM in a .bat.
rem     cmd.exe treats the BOM as part of the first command, so line 1 always
rem     fails with "is not recognized as an internal or external command".
rem  2. [Kill before build] dotnet build fails with MSB3021 when the app is
rem     running (exe is locked). Stop the process first.
rem  3. [No compression for framework-dependent] EnableCompressionInSingleFile
rem     is only supported with SelfContained=true (NETSDK1176).
rem  4. [Do not test the Chinese filename literally] The .bat is ASCII, so a
rem     Chinese path inside "if exist" cannot be matched reliably. Use a
rem     wildcard on the dist folder instead.
rem ---------------------------------------------------------------------------

setlocal enabledelayedexpansion
cd /d "%~dp0"

rem SDK resolution order: PATH -> Program Files (MSI) -> D:	ool\dotnet (green)
set "DOTNET=dotnet"
where dotnet >nul 2>&1
if errorlevel 1 (
    set "DOTNET=%ProgramFiles%\dotnet\dotnet.exe"
    if not exist "!DOTNET!" set "DOTNET=D:	ool\dotnet\dotnet.exe"
)

echo ============================================================
echo  1/4  Stopping running instance
echo ============================================================
rem  Kill any running build output. The Chinese exe name cannot be written
rem  literally in an ASCII .bat, so match by process path instead.
powershell -NoProfile -Command "Get-Process | Where-Object { $_.Path -like '*mchose-battery-tray*' } | Stop-Process -Force -ErrorAction SilentlyContinue" >nul 2>&1
timeout /t 2 /nobreak >nul

echo.
echo ============================================================
echo  2/4  Running unit tests
echo ============================================================
"%DOTNET%" run --project "src-cs.Tests\PeripheralBattery.Tests.csproj" -c Debug -v q --nologo
if errorlevel 1 (
    echo.
    echo [FAIL] unit tests failed
    exit /b 1
)

echo.
echo ============================================================
echo  3/4  Building tray app
echo ============================================================
"%DOTNET%" build "src-cs\PeripheralBattery.csproj" -c Release -v q --nologo
if errorlevel 1 (
    echo.
    echo [FAIL] build failed
    exit /b 1
)

echo.
echo ============================================================
echo  4/4  Publishing single-file exe
echo ============================================================
"%DOTNET%" publish "src-cs\PeripheralBattery.csproj" -c Release -v q --nologo -p:PublishSingleFile=true -p:SelfContained=false -p:IncludeNativeLibrariesForSelfExtract=true -o "dist"
if errorlevel 1 (
    echo.
    echo [FAIL] publish failed
    exit /b 1
)

echo.
echo ============================================================
echo  Done
echo ============================================================
set FOUND=0
for %%F in ("dist\*.exe") do (
    echo  Output: %%~fF  %%~zF bytes
    set FOUND=1
)
if "%FOUND%"=="0" echo  [WARN] no exe produced in dist

endlocal
