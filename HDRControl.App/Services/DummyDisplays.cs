using HDRControl.App.Models;

namespace HDRControl.App.Services;

// Lets the UI tests run the real executable with many displays. Never touches real displays or saved settings.
public static class DummyDisplays
{
    private const string VariableName = "HDRCONTROL_DUMMY_DISPLAYS";
    private const uint FirstTargetId = 0xD0000000;

    private static readonly int Count =
        int.TryParse(Environment.GetEnvironmentVariable(VariableName), out var count) && count is >= 1 and <= 100
            ? count
            : 0;

    public static bool IsActive => Count > 0;

    public static IReadOnlyList<MonitorInfo> Create() =>
        Enumerable.Range(1, Count)
            .Select(number => new MonitorInfo(
                $"Dummy Display {number}",
                FirstTargetId + (uint)number,
                0,
                0,
                HdrStatus.Disabled,
                null,
                false))
            .ToList();
}
