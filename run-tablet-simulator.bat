@echo off
setlocal

set "ROOT=%~dp0"
set "PROJECT=%ROOT%src\Bedrock.DigiMine.DeviceSyncService.TabletSimulator\Bedrock.DigiMine.DeviceSyncService.TabletSimulator.csproj"
set "NODE_BRIDGE=%ROOT%src\Bedrock.DigiMine.DeviceSyncService.TabletSimulator\NodeBridge"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo ERROR: .NET 8 SDK was not found on PATH.
    exit /b 1
)

if not exist "%ROOT%lib\Bedrock.DigiMine.DeviceSyncService.Domain.dll" (
    echo ERROR: Shared DSS DLLs are missing from lib\.
    echo Copy the lib DLLs from the development machine before running.
    exit /b 1
)

if not exist "%ROOT%lib\Bedrock.DigiMine.DeviceSyncService.ProtoDecoder.dll" (
    echo ERROR: Shared DSS DLLs are missing from lib\.
    echo Copy the lib DLLs from the development machine before running.
    exit /b 1
)

where node >nul 2>&1
if not errorlevel 1 (
    pushd "%NODE_BRIDGE%"
    if not exist node_modules (
        echo Installing Node MQTT bridge dependencies...
        call npm install
        if errorlevel 1 (
            echo ERROR: npm install failed.
            popd
            exit /b 1
        )
    )
    popd
)

echo Starting Tablet Simulator...
dotnet run --project "%PROJECT%" -- --skip-lib-sync
exit /b %errorlevel%