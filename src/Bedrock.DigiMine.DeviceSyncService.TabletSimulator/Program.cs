using System.Security.Authentication;
using System.Security.Cryptography;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Configuration;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Libs;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Web;
using MQTTnet.Exceptions;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        var store = new SimulatorConfigStore();
        var config = SimulatorConfigStore.LoadFromArgs(args, store);
        config.EnsureDevicesMigrated();

        await TrySyncLibsOnStartupAsync(config, args).ConfigureAwait(false);

        await using var context = TabletSimulatorDependencyInjection.Create(config, store);
        // Keep JSON mirrored after SQLite device sync (names / migrated list).
        store.Save(config);

        Console.WriteLine("Tablet Simulator");
        Console.WriteLine($"DeviceId: {config.Device.DeviceId}");
        Console.WriteLine($"Environment: {config.ActiveEnvironment}");
        Console.WriteLine($"Database: {Path.Combine(AppContext.BaseDirectory, "simulator.db")}");
        Console.WriteLine();

        try
        {
            await context.MqttClient.ConnectAndSubscribeAsync().ConfigureAwait(false);
            var active = config.GetActiveEnvironment();
            active.NormalizeHost();
            Console.WriteLine($"Connected to {active.GetBrokerUrl()}"
                + (context.MqttClient.UsesNodeBridge ? " (Node OpenSSL bridge)" : ""));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or CryptographicException
            or MqttCommunicationException or AuthenticationException)
        {
            var detail = ex.InnerException?.Message ?? ex.Message;
            Console.WriteLine($"MQTT connect failed: {detail}");
            Console.WriteLine("Open the web UI to change environment settings.");
        }

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };

        await using var webHost = new TabletSimulatorWebHost(context);
        try
        {
            await webHost.RunAsync(config.Web.Port, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cts.Token.IsCancellationRequested)
        {
        }

        await context.MqttClient.DisconnectAsync().ConfigureAwait(false);
        return 0;
    }

    private static async Task TrySyncLibsOnStartupAsync(SimulatorConfig config, string[] args)
    {
        if (args.Any(a => string.Equals(a, "--skip-lib-sync", StringComparison.OrdinalIgnoreCase)))
        {
            Console.WriteLine("Skipping lib sync (--skip-lib-sync).");
            return;
        }

        config.Libs ??= new LibsOptions();
        if (!config.Libs.SyncOnStartup)
        {
            return;
        }

        try
        {
            Console.WriteLine("Syncing DSS libs (scripts/sync-libs.ps1)...");
            var result = await DssLibSyncService.RunSyncLibsScriptAsync(
                config.Libs.DssRepoRoot,
                configuration: "Debug").ConfigureAwait(false);
            config.Libs.DssRepoRoot = result.DssRepoRoot;
            Console.WriteLine($"Lib sync OK → {result.LibDir}");
            if (!string.IsNullOrWhiteSpace(result.GrpcSharedVersion))
            {
                Console.WriteLine($"BGT.DigiMine.Grpc.Shared: {result.GrpcSharedVersion}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Lib sync skipped/failed: {ex.Message}");
            Console.WriteLine("Continue startup. Fix DSS path in Settings or run scripts/sync-libs.ps1 manually.");
        }
    }
}
