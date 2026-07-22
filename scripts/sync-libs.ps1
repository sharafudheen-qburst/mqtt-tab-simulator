param(
    [string]$DssRepoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..\bedrock.digimine.devicesyncservice")).Path,
    [string]$Configuration = "Debug",
    [string]$LibDir = (Join-Path (Split-Path $PSScriptRoot -Parent) "lib"),
    [string]$PackagesProps = (Join-Path (Split-Path $PSScriptRoot -Parent) "Directory.Packages.props")
)

$ErrorActionPreference = "Stop"

$domainProject = Join-Path $DssRepoRoot "src\Bedrock.DigiMine.DeviceSyncService.Domain\Bedrock.DigiMine.DeviceSyncService.Domain.csproj"
$protoDecoderProject = Join-Path $DssRepoRoot "tools\Bedrock.DigiMine.DeviceSyncService.ProtoDecoder\Bedrock.DigiMine.DeviceSyncService.ProtoDecoder.csproj"

if (-not (Test-Path $domainProject)) {
    throw "DeviceSyncService Domain project not found at $domainProject. Set -DssRepoRoot to your DSS checkout."
}

if (-not (Test-Path $protoDecoderProject)) {
    throw "ProtoDecoder project not found at $protoDecoderProject. Set -DssRepoRoot to your DSS checkout."
}

Write-Host "Building DSS libraries from $DssRepoRoot ..."
dotnet build $domainProject -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "Domain build failed with exit code $LASTEXITCODE" }

# UseAppHost=false avoids copy failures when ProtoDecoder.exe is locked by a running CLI process.
dotnet build $protoDecoderProject -c $Configuration /p:UseAppHost=false
if ($LASTEXITCODE -ne 0) { throw "ProtoDecoder build failed with exit code $LASTEXITCODE" }

$domainOut = Join-Path $DssRepoRoot "src\Bedrock.DigiMine.DeviceSyncService.Domain\bin\$Configuration\net8.0"
$protoBinOut = Join-Path $DssRepoRoot "tools\Bedrock.DigiMine.DeviceSyncService.ProtoDecoder\bin\$Configuration\net8.0"
$protoObjOut = Join-Path $DssRepoRoot "tools\Bedrock.DigiMine.DeviceSyncService.ProtoDecoder\obj\$Configuration\net8.0"

# Prefer bin output; fall back to obj when a running ProtoDecoder.exe locks bin\*.dll.
function Resolve-BuildArtifact([string]$BinDir, [string]$ObjDir, [string]$FileName) {
    $binPath = Join-Path $BinDir $FileName
    $objPath = Join-Path $ObjDir $FileName
    if ((Test-Path $binPath) -and (Test-Path $objPath)) {
        $binInfo = Get-Item $binPath
        $objInfo = Get-Item $objPath
        if ($objInfo.LastWriteTime -gt $binInfo.LastWriteTime) {
            Write-Host "Using obj copy of $FileName (newer than bin; bin may be locked by a running process)"
            return $objPath
        }
    }
    if (Test-Path $binPath) { return $binPath }
    if (Test-Path $objPath) {
        Write-Host "Using obj copy of $FileName (bin missing)"
        return $objPath
    }
    return $binPath
}

function Get-GrpcSharedVersionFromDeps([string]$DepsPath) {
    if (-not (Test-Path $DepsPath)) { return $null }
    $json = Get-Content $DepsPath -Raw | ConvertFrom-Json
    foreach ($name in $json.libraries.PSObject.Properties.Name) {
        if ($name -like "BGT.DigiMine.Grpc.Shared/*") {
            return $name.Substring("BGT.DigiMine.Grpc.Shared/".Length)
        }
    }
    return $null
}

function Update-GrpcSharedPackageVersion([string]$PropsPath, [string]$Version) {
    if (-not (Test-Path $PropsPath)) {
        throw "Directory.Packages.props not found at $PropsPath"
    }
    $text = Get-Content $PropsPath -Raw
    $pattern = '(<PackageVersion\s+Include\s*=\s*"BGT\.DigiMine\.Grpc\.Shared"\s+Version\s*=\s*")([^"]+)(")'
    $match = [regex]::Match($text, $pattern)
    if (-not $match.Success) {
        throw "Could not find PackageVersion for BGT.DigiMine.Grpc.Shared in $PropsPath"
    }
    $current = $match.Groups[2].Value
    if ($current -eq $Version) {
        Write-Host "BGT.DigiMine.Grpc.Shared already pinned at $Version"
        return $false
    }
    $updated = [regex]::Replace($text, $pattern, "`${1}$Version`${3}", 1)
    Set-Content -Path $PropsPath -Value $updated -NoNewline
    Write-Host "Updated BGT.DigiMine.Grpc.Shared in Directory.Packages.props: $current → $Version"
    return $true
}

New-Item -ItemType Directory -Force -Path $LibDir | Out-Null

$files = @(
    (Join-Path $domainOut "Bedrock.DigiMine.DeviceSyncService.Domain.dll"),
    (Join-Path $domainOut "Bedrock.DigiMine.DeviceSyncService.Domain.xml"),
    (Resolve-BuildArtifact $protoBinOut $protoObjOut "Bedrock.DigiMine.DeviceSyncService.ProtoDecoder.dll"),
    (Resolve-BuildArtifact $protoBinOut $protoObjOut "Bedrock.DigiMine.DeviceSyncService.ProtoDecoder.xml")
)

foreach ($file in $files) {
    if (-not (Test-Path $file)) {
        throw "Expected build output not found: $file"
    }
    Copy-Item $file $LibDir -Force
    Write-Host "Copied $(Split-Path $file -Leaf)"
}

$depsCandidates = @(
    (Join-Path $protoBinOut "Bedrock.DigiMine.DeviceSyncService.ProtoDecoder.deps.json"),
    (Join-Path $protoObjOut "Bedrock.DigiMine.DeviceSyncService.ProtoDecoder.deps.json")
)
$grpcVersion = $null
foreach ($deps in $depsCandidates) {
    $grpcVersion = Get-GrpcSharedVersionFromDeps $deps
    if ($grpcVersion) { break }
}
if (-not $grpcVersion) {
    $dssPackages = Join-Path $DssRepoRoot "Directory.Packages.props"
    if (Test-Path $dssPackages) {
        $dssText = Get-Content $dssPackages -Raw
        $m = [regex]::Match($dssText, 'Include\s*=\s*"BGT\.DigiMine\.Grpc\.Shared"\s+Version\s*=\s*"([^"]+)"')
        if ($m.Success) { $grpcVersion = $m.Groups[1].Value }
    }
}
if (-not $grpcVersion) {
    throw "Could not detect BGT.DigiMine.Grpc.Shared version from ProtoDecoder.deps.json"
}

Update-GrpcSharedPackageVersion -PropsPath $PackagesProps -Version $grpcVersion | Out-Null

Write-Host "Done. DLLs are in $LibDir"
Write-Host "Rebuild the simulator so BGT.DigiMine.Grpc.Shared $grpcVersion is restored."
