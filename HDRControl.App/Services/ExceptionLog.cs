namespace HDRControl.App.Services;

public static class ExceptionLog
{
    // The UI tests point the log elsewhere so they never write into the user's log.
    public static readonly string LogDirectory =
        Environment.GetEnvironmentVariable("HDRCONTROL_LOG_DIRECTORY") is { Length: > 0 } testDirectory
            ? testDirectory
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HDRControl",
                "Logs");

    private static readonly object Gate = new();

    public static void Write(string context, object details)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(
                    Path.Combine(LogDirectory, "ExceptionLog.txt"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {context}{Environment.NewLine}{details}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // Logging must never take the app down.
        }
    }
}
