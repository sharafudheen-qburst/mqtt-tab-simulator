namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

public sealed class TabletInboundMessageEventArgs : EventArgs
{
    public TabletInboundMessageEventArgs(TabletInboundMessage message) => Message = message;

    public TabletInboundMessage Message { get; }
}
