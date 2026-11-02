namespace HDRControl.App.Models;

public enum HdrStatus
{
    NotSupported,
    Enabled,
    Disabled,
    Unknown
}

public sealed record MonitorInfo(
    string Name,
    uint TargetId,
    uint AdapterLowPart,
    int AdapterHighPart,
    HdrStatus Status,
    string? Diagnostic,
    bool UsesHdrSpecificApi,
    string? DevicePath = null)
{
    // The device path stays the same across restarts; the adapter LUID in LegacyIdentity does not.
    public string Identity => DevicePath is { Length: > 0 } ? DevicePath : LegacyIdentity;

    public string LegacyIdentity =>
        $"{AdapterHighPart:X8}:{AdapterLowPart:X8}:{TargetId:X8}";
}
