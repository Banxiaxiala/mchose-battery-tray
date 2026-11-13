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

rem SDK resolution: take the FIRST candidate that actually reports an SDK.
rem
rem Why a loop instead of "where dotnet":
rem   1. "where dotnet" succeeding proves nothing. C:\Program Files\dotnet
rem      often holds only the runtime host, so "dotnet build" dies with
rem      "No .NET SDKs were found". cmd does not abort on that, so this
rem      script went on to print "Done" while dist still had the old exe.
rem   2. NEVER type a backslash-t sequence here. It was literally stored as
rem      TAB for a long time ("D:" + TAB + "ool"), so the fallback path never
rem      existed. Keep the drive and the folder on separate lines below.
rem Probe rule: find an SDK DIRECTORY, never trust an exit code.
rem Measured on this machine:
rem   C:\Program Files\dotnet\dotnet.exe --version
rem   prints "No .NET SDKs were found" and STILL exits 0.
rem   --list-sdks also exits 0 with empty output when no SDK is installed.
rem   "if exist ...\sdk\*\MSBuild.dll" never matches either:
rem   cmd only expands wildcards in the LAST path segment.
rem So walk the version folders with for /d and look for MSBuild.dll inside.
set "DOTNET="
for %%H in (
    "%ProgramFiles%\dotnet"
    "D:\tool\dotnet"
    "G:\tool\dotnet"
) do (
    if not defined DOTNET (
        for /d %%V in ("%%~H\sdk\*") do (
            if not defined DOTNET (
                if exist "%%~V\MSBuild.dll" (
                    set "DOTNET=%%~H\dotnet.exe"
                )
            )
        )
    )
)
if not defined DOTNET (
    rem Last resort: whatever PATH resolves to. It is verified by step 2/4
    rem failing loudly instead of continuing, see the errorlevel checks.
    where dotnet >nul 2>&1
    if not errorlevel 1 set "DOTNET=dotnet"
)
if not defined DOTNET (
    echo [FAIL] no .NET SDK found. Install .NET SDK 8 and retry.
    exit /b 1
)
echo  Using SDK host: %DOTNET%

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
"%DOTNET%" run --project "tests\PeripheralBattery.Tests.csproj" -c Debug -v q --nologo
if errorlevel 1 (
    echo.
    echo [FAIL] unit tests failed
    exit /b 1
)

echo.
echo ============================================================
echo  3/4  Building tray app
echo ============================================================
"%DOTNET%" build "src\PeripheralBattery.csproj" -c Release -v q --nologo
if errorlevel 1 (
    echo.
    echo [FAIL] build failed
    exit /b 1
)

echo.
echo ============================================================
echo  4/4  Publishing single-file exe
echo ============================================================
del /q "dist\*.exe" >nul 2>&1
"%DOTNET%" publish "src\PeripheralBattery.csproj" -c Release -v q --nologo -p:PublishSingleFile=true -p:SelfContained=false -p:IncludeNativeLibrariesForSelfExtract=true -o "dist"
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
