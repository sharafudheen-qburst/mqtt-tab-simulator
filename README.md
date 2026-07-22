# Bedrock DigiMine Tablet Simulator

Standalone MQTT tablet simulator with web UI. References **DeviceSyncService** shared libraries as DLLs from `lib/`.

## Prerequisites

- .NET 8 SDK
- Node.js 18+ (for `mqtts://` / OpenSSL bridge on Windows)
- Access to Azure DevOps NuGet feed (`BGT.DigiMine.Grpc.Shared`)
- Local clone of [bedrock.digimine.devicesyncservice](../bedrock.digimine.devicesyncservice) (sibling folder) for building lib DLLs

## First-time setup

```powershell
cd bedrock.digimine.tablet-simulator

# Copy Domain + ProtoDecoder DLLs from DeviceSyncService repo
powershell -File scripts/sync-libs.ps1

# Node MQTT bridge
cd src/Bedrock.DigiMine.DeviceSyncService.TabletSimulator/NodeBridge
npm install
cd ../../../..

dotnet build
```

## Run

```powershell
dotnet run --project src/Bedrock.DigiMine.DeviceSyncService.TabletSimulator
```

Web UI: http://localhost:5055 (port from `simulator-config.json`).

## Refresh shared DLLs

After DeviceSyncService Domain or ProtoDecoder changes:

```powershell
powershell -File scripts/sync-libs.ps1
dotnet build
```

Optional custom DSS path:

```powershell
powershell -File scripts/sync-libs.ps1 -DssRepoRoot "C:\path\to\bedrock.digimine.devicesyncservice"
```

## Layout

```
bedrock.digimine.tablet-simulator/
  lib/                    # Domain + ProtoDecoder DLLs (from sync-libs.ps1)
  scripts/sync-libs.ps1
  src/Bedrock.DigiMine.DeviceSyncService.TabletSimulator/
    NodeBridge/           # OpenSSL MQTT bridge (npm install)
    www/                  # Web UI
    simulator-config.json
```

## Package versions

Pinned in `Directory.Packages.props`. Keep `BGT.DigiMine.Grpc.Shared` aligned with the DeviceSyncService repo when syncing libs.
