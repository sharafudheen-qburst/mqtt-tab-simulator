namespace Bedrock.DigiMine.DeviceSyncService.TabletSimulator.Mqtt;

internal static class ExceptionDetailFormatter
{
    public static string Format(Exception ex)
    {
        var parts = new List<string>();
        var current = ex;
        var depth = 0;

        while (current is not null)
        {
            var label = depth == 0 ? current.GetType().Name : $"Inner[{depth}] {current.GetType().Name}";
            var msg = current.Message?.Trim();
            if (!string.IsNullOrEmpty(msg))
            {
                parts.Add($"{label}: {msg}");
            }

            if (current is System.ComponentModel.Win32Exception win32)
            {
                parts.Add($"Win32 HRESULT: 0x{win32.NativeErrorCode:X8}");
            }

            current = current.InnerException;
            depth++;
        }

        return string.Join(" | ", parts);
    }
}
