namespace DocuClick.Services;

/// <summary>
/// Where config and log live. Windows: %APPDATA%\DocuClick\ (unchanged
/// from earlier versions). macOS: the platform-conventional
/// ~/Library/Application Support/DocuClick/ and ~/Library/Logs/DocuClick/.
/// </summary>
public static class AppPaths
{
    public static string ConfigDirectory { get; } = OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "Application Support", "DocuClick")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DocuClick");

    public static string LogFile { get; } = OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "Logs", "DocuClick", "log.txt")
        : Path.Combine(ConfigDirectory, "log.txt");

    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
