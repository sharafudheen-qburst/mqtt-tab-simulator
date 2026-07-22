using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Libs;

public static partial class DssLibSyncService
{
    public const string DefaultDssRepoRoot = @"C:\Work\IRH_Solutions\bedrock.digimine.devicesyncservice";
    public const string GrpcSharedPackageId = "BGT.DigiMine.Grpc.Shared";

    private static readonly string[] LibFileNames =
    [
        "Bedrock.DigiMine.DeviceSyncService.Domain.dll",
        "Bedrock.DigiMine.DeviceSyncService.Domain.xml",
        "Bedrock.DigiMine.DeviceSyncService.ProtoDecoder.dll",
        "Bedrock.DigiMine.DeviceSyncService.ProtoDecoder.xml",
    ];

    public static string ResolveSimulatorRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Directory.Packages.props"))
                && File.Exists(Path.Combine(dir.FullName, "Bedrock.DigiMine.TabletSimulator.sln")))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        // Fallback: bin/Debug/net8.0 → project → src → repo
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
    }

    public static string ResolveDefaultDssRepoRoot(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured.Trim()))
        {
            return Path.GetFullPath(configured.Trim());
        }

        var sibling = Path.GetFullPath(Path.Combine(ResolveSimulatorRepoRoot(), "..", "bedrock.digimine.devicesyncservice"));
        if (Directory.Exists(sibling))
        {
            return sibling;
        }

        return DefaultDssRepoRoot;
    }

    public static string ResolveLibDir(string? simulatorRepoRoot = null) =>
        Path.Combine(simulatorRepoRoot ?? ResolveSimulatorRepoRoot(), "lib");

    public static string ResolvePackagesPropsPath(string? simulatorRepoRoot = null) =>
        Path.Combine(simulatorRepoRoot ?? ResolveSimulatorRepoRoot(), "Directory.Packages.props");

    public static DssLibStatus GetStatus(string? dssRepoRoot)
    {
        var simulatorRoot = ResolveSimulatorRepoRoot();
        var libDir = ResolveLibDir(simulatorRoot);
        var packagesProps = ResolvePackagesPropsPath(simulatorRoot);
        var resolvedDss = ResolveDefaultDssRepoRoot(dssRepoRoot);
        var pinned = ReadPinnedGrpcSharedVersion(packagesProps);
        var detected = TryDetectGrpcSharedVersion(resolvedDss, "Debug");

        var files = LibFileNames
            .Select(name =>
            {
                var path = Path.Combine(libDir, name);
                if (!File.Exists(path))
                {
                    return new DssLibFileInfo { Name = name, Exists = false };
                }

                var info = new FileInfo(path);
                return new DssLibFileInfo
                {
                    Name = name,
                    Exists = true,
                    Path = path,
                    Length = info.Length,
                    LastWriteTimeUtc = info.LastWriteTimeUtc,
                };
            })
            .ToArray();

        return new DssLibStatus
        {
            SimulatorRepoRoot = simulatorRoot,
            LibDir = libDir,
            DssRepoRoot = resolvedDss,
            DssRepoExists = Directory.Exists(resolvedDss),
            PinnedGrpcSharedVersion = pinned,
            DetectedGrpcSharedVersion = detected,
            Files = files,
        };
    }

    public static async Task<DssLibSyncResult> SyncAsync(
        string dssRepoRoot,
        string configuration = "Debug",
        CancellationToken cancellationToken = default)
    {
        var simulatorRoot = ResolveSimulatorRepoRoot();
        var libDir = ResolveLibDir(simulatorRoot);
        var packagesProps = ResolvePackagesPropsPath(simulatorRoot);
        var root = Path.GetFullPath(dssRepoRoot.Trim());

        var domainProject = Path.Combine(
            root,
            "src",
            "Bedrock.DigiMine.DeviceSyncService.Domain",
            "Bedrock.DigiMine.DeviceSyncService.Domain.csproj");
        var protoDecoderProject = Path.Combine(
            root,
            "tools",
            "Bedrock.DigiMine.DeviceSyncService.ProtoDecoder",
            "Bedrock.DigiMine.DeviceSyncService.ProtoDecoder.csproj");

        if (!File.Exists(domainProject))
        {
            throw new InvalidOperationException($"DeviceSyncService Domain project not found at {domainProject}.");
        }

        if (!File.Exists(protoDecoderProject))
        {
            throw new InvalidOperationException($"ProtoDecoder project not found at {protoDecoderProject}.");
        }

        var log = new List<string>();
        await RunDotnetBuildAsync(domainProject, configuration, useAppHost: true, log, cancellationToken)
            .ConfigureAwait(false);
        await RunDotnetBuildAsync(protoDecoderProject, configuration, useAppHost: false, log, cancellationToken)
            .ConfigureAwait(false);

        var domainOut = Path.Combine(
            root,
            "src",
            "Bedrock.DigiMine.DeviceSyncService.Domain",
            "bin",
            configuration,
            "net8.0");
        var protoBinOut = Path.Combine(
            root,
            "tools",
            "Bedrock.DigiMine.DeviceSyncService.ProtoDecoder",
            "bin",
            configuration,
            "net8.0");
        var protoObjOut = Path.Combine(
            root,
            "tools",
            "Bedrock.DigiMine.DeviceSyncService.ProtoDecoder",
            "obj",
            configuration,
            "net8.0");

        Directory.CreateDirectory(libDir);
        var copied = new List<string>();

        CopyRequired(
            Path.Combine(domainOut, "Bedrock.DigiMine.DeviceSyncService.Domain.dll"),
            libDir,
            copied,
            log);
        TryCopyOptional(
            Path.Combine(domainOut, "Bedrock.DigiMine.DeviceSyncService.Domain.xml"),
            libDir,
            copied,
            log);

        var protoDll = ResolveBuildArtifact(
            protoBinOut,
            protoObjOut,
            "Bedrock.DigiMine.DeviceSyncService.ProtoDecoder.dll",
            log);
        CopyRequired(protoDll, libDir, copied, log);
        var protoXml = ResolveBuildArtifactOptional(
            protoBinOut,
            protoObjOut,
            "Bedrock.DigiMine.DeviceSyncService.ProtoDecoder.xml",
            log);
        if (protoXml is not null)
        {
            TryCopyOptional(protoXml, libDir, copied, log);
        }

        var grpcVersion = TryDetectGrpcSharedVersion(root, configuration)
            ?? throw new InvalidOperationException(
                "Could not detect BGT.DigiMine.Grpc.Shared version from ProtoDecoder.deps.json.");
        var previousPinned = ReadPinnedGrpcSharedVersion(packagesProps);
        var packagesPropsUpdated = UpdateGrpcSharedPackageVersion(packagesProps, grpcVersion);
        if (packagesPropsUpdated)
        {
            log.Add($"Updated {GrpcSharedPackageId} in Directory.Packages.props: {previousPinned} → {grpcVersion}");
        }
        else
        {
            log.Add($"{GrpcSharedPackageId} already pinned at {grpcVersion}");
        }

        return new DssLibSyncResult
        {
            Ok = true,
            DssRepoRoot = root,
            LibDir = libDir,
            Copied = copied.ToArray(),
            GrpcSharedVersion = grpcVersion,
            PreviousGrpcSharedVersion = previousPinned,
            PackagesPropsUpdated = packagesPropsUpdated,
            Log = log.ToArray(),
            Message =
                "Libs imported. Rebuild the simulator and restart so ProtoDecoder and BGT.DigiMine.Grpc.Shared take effect.",
        };
    }

    public static string? ReadPinnedGrpcSharedVersion(string packagesPropsPath)
    {
        if (!File.Exists(packagesPropsPath))
        {
            return null;
        }

        try
        {
            var doc = XDocument.Load(packagesPropsPath);
            var match = doc
                .Descendants("PackageVersion")
                .FirstOrDefault(e =>
                    string.Equals(
                        (string?)e.Attribute("Include"),
                        GrpcSharedPackageId,
                        StringComparison.OrdinalIgnoreCase));
            return match?.Attribute("Version")?.Value?.Trim();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static bool UpdateGrpcSharedPackageVersion(string packagesPropsPath, string version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        if (!File.Exists(packagesPropsPath))
        {
            throw new FileNotFoundException("Directory.Packages.props not found.", packagesPropsPath);
        }

        var text = File.ReadAllText(packagesPropsPath);
        var pattern = GrpcSharedVersionRegex();
        var match = pattern.Match(text);
        if (!match.Success)
        {
            throw new InvalidOperationException(
                $"Could not find PackageVersion for {GrpcSharedPackageId} in {packagesPropsPath}.");
        }

        var current = match.Groups["version"].Value;
        if (string.Equals(current, version, StringComparison.Ordinal))
        {
            return false;
        }

        var updated = pattern.Replace(
            text,
            m => m.Value.Replace($"Version=\"{m.Groups["version"].Value}\"", $"Version=\"{version}\"", StringComparison.Ordinal),
            1);
        File.WriteAllText(packagesPropsPath, updated);
        return true;
    }

    public static string? TryDetectGrpcSharedVersion(string dssRepoRoot, string configuration)
    {
        var candidates = new[]
        {
            Path.Combine(
                dssRepoRoot,
                "tools",
                "Bedrock.DigiMine.DeviceSyncService.ProtoDecoder",
                "bin",
                configuration,
                "net8.0",
                "Bedrock.DigiMine.DeviceSyncService.ProtoDecoder.deps.json"),
            Path.Combine(
                dssRepoRoot,
                "tools",
                "Bedrock.DigiMine.DeviceSyncService.ProtoDecoder",
                "obj",
                configuration,
                "net8.0",
                "Bedrock.DigiMine.DeviceSyncService.ProtoDecoder.deps.json"),
            Path.Combine(dssRepoRoot, "Directory.Packages.props"),
        };

        foreach (var path in candidates)
        {
            if (!File.Exists(path))
            {
                continue;
            }

            if (path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase))
            {
                var fromDeps = ReadGrpcSharedFromDepsJson(path);
                if (!string.IsNullOrWhiteSpace(fromDeps))
                {
                    return fromDeps;
                }
            }
            else
            {
                var fromProps = ReadPinnedGrpcSharedVersion(path);
                if (!string.IsNullOrWhiteSpace(fromProps))
                {
                    return fromProps;
                }
            }
        }

        return null;
    }

    private static string? ReadGrpcSharedFromDepsJson(string depsPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(depsPath));
            if (!doc.RootElement.TryGetProperty("libraries", out var libraries))
            {
                return null;
            }

            foreach (var prop in libraries.EnumerateObject())
            {
                if (prop.Name.StartsWith($"{GrpcSharedPackageId}/", StringComparison.OrdinalIgnoreCase))
                {
                    return prop.Name[(GrpcSharedPackageId.Length + 1)..];
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    private static async Task RunDotnetBuildAsync(
        string projectPath,
        string configuration,
        bool useAppHost,
        List<string> log,
        CancellationToken cancellationToken)
    {
        var args = useAppHost
            ? $"build \"{projectPath}\" -c {configuration}"
            : $"build \"{projectPath}\" -c {configuration} /p:UseAppHost=false";
        log.Add($"Running: dotnet {args}");

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            log.Add(stdout.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            log.Add(stderr.TrimEnd());
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"dotnet build failed for {Path.GetFileName(projectPath)} (exit {process.ExitCode}).");
        }
    }

    private static string ResolveBuildArtifact(
        string binDir,
        string objDir,
        string fileName,
        List<string> log)
    {
        var path = ResolveBuildArtifactOptional(binDir, objDir, fileName, log);
        if (path is null)
        {
            throw new FileNotFoundException($"Expected build output not found: {fileName}", fileName);
        }

        return path;
    }

    private static string? ResolveBuildArtifactOptional(
        string binDir,
        string objDir,
        string fileName,
        List<string> log)
    {
        var binPath = Path.Combine(binDir, fileName);
        var objPath = Path.Combine(objDir, fileName);
        if (File.Exists(binPath) && File.Exists(objPath))
        {
            var binInfo = new FileInfo(binPath);
            var objInfo = new FileInfo(objPath);
            if (objInfo.LastWriteTimeUtc > binInfo.LastWriteTimeUtc)
            {
                log.Add($"Using obj copy of {fileName} (newer than bin; bin may be locked)");
                return objPath;
            }
        }

        if (File.Exists(binPath))
        {
            return binPath;
        }

        if (File.Exists(objPath))
        {
            log.Add($"Using obj copy of {fileName} (bin missing)");
            return objPath;
        }

        return null;
    }

    private static void CopyRequired(string source, string libDir, List<string> copied, List<string> log)
    {
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"Expected build output not found: {source}", source);
        }

        var dest = Path.Combine(libDir, Path.GetFileName(source));
        File.Copy(source, dest, overwrite: true);
        copied.Add(dest);
        log.Add($"Copied {Path.GetFileName(source)}");
    }

    private static void TryCopyOptional(string source, string libDir, List<string> copied, List<string> log)
    {
        if (!File.Exists(source))
        {
            log.Add($"Skipped missing optional file: {Path.GetFileName(source)}");
            return;
        }

        var dest = Path.Combine(libDir, Path.GetFileName(source));
        File.Copy(source, dest, overwrite: true);
        copied.Add(dest);
        log.Add($"Copied {Path.GetFileName(source)}");
    }

    public static string ResolveSyncLibsScriptPath(string? simulatorRepoRoot = null) =>
        Path.Combine(simulatorRepoRoot ?? ResolveSimulatorRepoRoot(), "scripts", "sync-libs.ps1");

    /// <summary>
    /// Runs scripts/sync-libs.ps1 (same as Settings import). Updates lib/ for the next rebuild;
    /// already-loaded assemblies in this process are unchanged.
    /// </summary>
    public static async Task<DssLibSyncResult> RunSyncLibsScriptAsync(
        string? dssRepoRoot = null,
        string configuration = "Debug",
        CancellationToken cancellationToken = default)
    {
        var simulatorRoot = ResolveSimulatorRepoRoot();
        var scriptPath = ResolveSyncLibsScriptPath(simulatorRoot);
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException($"sync-libs.ps1 not found at {scriptPath}", scriptPath);
        }

        var root = ResolveDefaultDssRepoRoot(dssRepoRoot);
        var shell = ResolvePowerShellExecutable();
        var args =
            $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\" " +
            $"-DssRepoRoot \"{root}\" -Configuration \"{configuration}\"";

        var log = new List<string> { $"Running: {shell} {args}" };
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = shell,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = simulatorRoot,
            },
        };

        process.Start();
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(stdout))
        {
            log.Add(stdout.TrimEnd());
        }

        if (!string.IsNullOrWhiteSpace(stderr))
        {
            log.Add(stderr.TrimEnd());
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"sync-libs.ps1 failed with exit code {process.ExitCode}.");
        }

        var grpcVersion = ReadPinnedGrpcSharedVersion(ResolvePackagesPropsPath(simulatorRoot)) ?? string.Empty;
        return new DssLibSyncResult
        {
            Ok = true,
            DssRepoRoot = root,
            LibDir = ResolveLibDir(simulatorRoot),
            Copied = Directory.Exists(ResolveLibDir(simulatorRoot))
                ? Directory.GetFiles(ResolveLibDir(simulatorRoot), "*.dll")
                    .Concat(Directory.GetFiles(ResolveLibDir(simulatorRoot), "*.xml"))
                    .ToArray()
                : [],
            GrpcSharedVersion = grpcVersion,
            PreviousGrpcSharedVersion = grpcVersion,
            PackagesPropsUpdated = false,
            Log = log.ToArray(),
            Message =
                "sync-libs.ps1 completed. Rebuild/restart again if Directory.Packages.props or lib/ DLLs changed.",
        };
    }

    private static string ResolvePowerShellExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            return "powershell.exe";
        }

        return "pwsh";
    }

    [GeneratedRegex(
        @"<PackageVersion\s+Include\s*=\s*""BGT\.DigiMine\.Grpc\.Shared""\s+Version\s*=\s*""(?<version>[^""]+)""\s*/?>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex GrpcSharedVersionRegex();
}

public sealed class DssLibStatus
{
    public string SimulatorRepoRoot { get; init; } = string.Empty;
    public string LibDir { get; init; } = string.Empty;
    public string DssRepoRoot { get; init; } = string.Empty;
    public bool DssRepoExists { get; init; }
    public string? PinnedGrpcSharedVersion { get; init; }
    public string? DetectedGrpcSharedVersion { get; init; }
    public DssLibFileInfo[] Files { get; init; } = [];
}

public sealed class DssLibFileInfo
{
    public string Name { get; init; } = string.Empty;
    public bool Exists { get; init; }
    public string? Path { get; init; }
    public long Length { get; init; }
    public DateTimeOffset LastWriteTimeUtc { get; init; }
}

public sealed class DssLibSyncResult
{
    public bool Ok { get; init; }
    public string DssRepoRoot { get; init; } = string.Empty;
    public string LibDir { get; init; } = string.Empty;
    public string[] Copied { get; init; } = [];
    public string GrpcSharedVersion { get; init; } = string.Empty;
    public string? PreviousGrpcSharedVersion { get; init; }
    public bool PackagesPropsUpdated { get; init; }
    public string[] Log { get; init; } = [];
    public string Message { get; init; } = string.Empty;
}
