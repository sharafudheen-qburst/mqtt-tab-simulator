using System.Diagnostics.CodeAnalysis;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Configuration;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;
using Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Persistence;

namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator;

public sealed class TabletSimulatorContext : IAsyncDisposable
{
    public TabletSimulatorContext(
        SimulatorConfig config,
        SimulatorConfigStore configStore,
        TabletMqttClient mqttClient,
        MqttActivityLog mqttActivityLog,
        SimulatorDatabase database,
        InboundMessageStore inboundMessages,
        AppStorageStore appStorage,
        DeviceStore devices)
    {
        Config = config;
        ConfigStore = configStore;
        MqttClient = mqttClient;
        MqttActivityLog = mqttActivityLog;
        Database = database;
        InboundMessages = inboundMessages;
        AppStorage = appStorage;
        Devices = devices;
    }

    public SimulatorConfig Config { get; }
    public SimulatorConfigStore ConfigStore { get; }
    public TabletMqttClient MqttClient { get; }
    public MqttActivityLog MqttActivityLog { get; }
    public SimulatorDatabase Database { get; }
    public InboundMessageStore InboundMessages { get; }
    public AppStorageStore AppStorage { get; }
    public DeviceStore Devices { get; }

    public async ValueTask DisposeAsync()
    {
        await MqttClient.DisposeAsync().ConfigureAwait(false);
        Database.Dispose();
    }
}

public static class TabletSimulatorDependencyInjection
{
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "Ownership transferred to TabletSimulatorContext.")]
    public static TabletSimulatorContext Create(SimulatorConfig config, SimulatorConfigStore configStore)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(configStore);

        var database = new SimulatorDatabase();
        var inboundMessages = new InboundMessageStore(database);
        var appStorage = new AppStorageStore(database);
        var devices = new DeviceStore(database);
        devices.SyncWithConfig(config);
        var mqttActivityLog = new MqttActivityLog();
        var mqttClient = new TabletMqttClient(config, inboundMessages, mqttActivityLog);
        return new TabletSimulatorContext(
            config,
            configStore,
            mqttClient,
            mqttActivityLog,
            database,
            inboundMessages,
            appStorage,
            devices);
    }
}
