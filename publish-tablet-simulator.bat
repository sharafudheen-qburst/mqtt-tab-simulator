@echo off
setlocal

set "ROOT=%~dp0"
set "PROJECT=%ROOT%src\Bedrock.DigiMine.DeviceSyncService.TabletSimulator\Bedrock.DigiMine.DeviceSyncService.TabletSimulator.csproj"
set "NODE_BRIDGE=%ROOT%src\Bedrock.DigiMine.DeviceSyncService.TabletSimulator\NodeBridge"
set "OUTPUT=%ROOT%artifacts\tablet-simulator"

echo Publishing Bedrock DigiMine Tablet Simulator...

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: .NET SDK was not found on PATH.
    exit /b 1
)

where node >nul 2>&1
if errorlevel 1 (
    echo ERROR: Node.js was not found on PATH.
    echo Install Node.js 18 or later, then run this file again.
    exit /b 1
)

if not exist "%ROOT%lib\Bedrock.DigiMine.DeviceSyncService.Domain.dll" (
    echo ERROR: Shared DSS DLLs are missing from lib\.
    echo Run scripts\sync-libs.ps1 first, or copy the required DLLs into lib\.
    exit /b 1
)

if not exist "%ROOT%lib\Bedrock.DigiMine.DeviceSyncService.ProtoDecoder.dll" (
    echo ERROR: Shared DSS DLLs are missing from lib\.
    echo Run scripts\sync-libs.ps1 first, or copy the required DLLs into lib\.
    exit /b 1
)

pushd "%NODE_BRIDGE%"
call npm install
if errorlevel 1 (
    echo ERROR: Node MQTT bridge dependency installation failed.
    popd
    exit /b 1
)
popd

dotnet publish "%PROJECT%" -c Release -r win-x64 --self-contained true -o "%OUTPUT%"
if errorlevel 1 (
    echo ERROR: .NET publish failed.
    exit /b 1
)

echo.
echo Publish complete:
echo %OUTPUT%
echo.
echo Copy that folder to the target Windows machine and run:
echo Bedrock.DigiMine.DeviceSyncService.TabletSimulator.exe
echo.
echo Update simulator-config.json and certificate paths before connecting to MQTT.
exit /b 0