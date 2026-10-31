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
    bool UsesHdrSpecificApi)
{
    public string Identity =>
        $"{AdapterHighPart:X8}:{AdapterLowPart:X8}:{TargetId:X8}";
}
