using System.IO;
using System.Text.Json;

namespace DocuClick.Services;

/// <summary>A recording command from the Obsidian plugin (see <see cref="LocalSaveService"/>, POST /control).</summary>
/// <param name="Action">"status", "start" (record into <paramref name="File"/>), "pause" or "branch" (decision point named <paramref name="Name"/>).</param>
public sealed record RemoteCommand(string Action, string? File = null, string? Name = null);

/// <summary>The app's answer: whether the command worked and the recording state afterwards.</summary>
public sealed record RemoteStatus(bool Ok, string Message, bool Recording, bool Paused, string? File);

/// <summary>
/// Pairs the Obsidian plugin with the running app. The app keeps a random
/// token (AppConfig.RemoteControlToken) and writes it, with the port, into
/// the plugin's folder of every vault it records into
/// (<c>.obsidian/plugins/docuclick-diagrams/app-link.json</c>). Only someone
/// who can read that vault can control recording; the endpoint listens on
/// 127.0.0.1 only and refuses browser requests (Origin, content type).
/// </summary>
public static class ObsidianAppLink
{
    public const string FileName = "app-link.json";

    public static string NewToken() => Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>Writes the link file if the plugin is installed in the vault. Failures are logged only.</summary>
    public static void Write(string vaultRoot, string token, int port = LocalSaveService.Port)
    {
        try
        {
            var folder = Path.Combine(vaultRoot, ".obsidian", "plugins", ObsidianPluginInstaller.PluginId);
            if (!Directory.Exists(folder))
            {
                return;
            }

            var path = Path.Combine(folder, FileName);
            var text = JsonSerializer.Serialize(new { port, token });
            if (!File.Exists(path) || File.ReadAllText(path) != text)
            {
                File.WriteAllText(path, text);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            LogService.Log($"Verbindung zum Obsidian-Plugin konnte nicht gespeichert werden: {ex.Message}");
        }
    }
}
