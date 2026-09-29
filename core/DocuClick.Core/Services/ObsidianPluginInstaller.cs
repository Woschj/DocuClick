using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocuClick.Services;

/// <summary>
/// Installs the DocuClick Diagrams plugin into a vault the apps record into,
/// so a diagram note opens as a diagram without a manual plugin install. The
/// apps ship the plugin sources (<c>ObsidianPlugin/</c> next to the app, from
/// <c>obsidian/</c>) and assemble <c>main.js</c> exactly like
/// <c>obsidian/build.py</c>, with the same editor template the app uses.
/// Never downgrades, never touches the plugin's settings (<c>data.json</c>),
/// and enables it only on first install — a plugin the user disabled stays
/// disabled.
/// </summary>
public static class ObsidianPluginInstaller
{
    public const string PluginId = "docuclick-diagrams";

    private static string DefaultSourceFolder => Path.Combine(AppContext.BaseDirectory, "ObsidianPlugin");
    private static string DefaultAssetsFolder => Path.Combine(AppContext.BaseDirectory, "WebAssets");

    /// <summary>
    /// Makes sure <paramref name="vaultRoot"/> has the bundled plugin version
    /// (or a newer one). Returns a message for the user when something was
    /// installed or updated, else null. Failures are logged, not thrown: the
    /// recording itself works without the plugin.
    /// </summary>
    public static string? EnsureInstalled(string vaultRoot, string? sourceFolder = null, string? assetsFolder = null)
    {
        try
        {
            var source = sourceFolder ?? DefaultSourceFolder;
            var bundledVersion = ReadVersion(Path.Combine(source, "manifest.json"));
            if (bundledVersion is null)
            {
                LogService.Log($"Obsidian-Plugin nicht im App-Paket gefunden ({source}); keine automatische Installation.");
                return null;
            }

            var target = Path.Combine(vaultRoot, ".obsidian", "plugins", PluginId);
            var installedVersion = ReadVersion(Path.Combine(target, "manifest.json"));
            if (installedVersion is not null && Compare(installedVersion, bundledVersion) >= 0)
            {
                return null;
            }

            Directory.CreateDirectory(target);
            WriteAtomically(Path.Combine(target, "main.js"), BuildMainJs(source, assetsFolder ?? DefaultAssetsFolder));
            WriteAtomically(Path.Combine(target, "styles.css"), File.ReadAllText(Path.Combine(source, "styles.css")));
            // Last: a complete set of files is what makes it "installed".
            WriteAtomically(Path.Combine(target, "manifest.json"), File.ReadAllText(Path.Combine(source, "manifest.json")));
            LogService.Log($"Obsidian-Plugin {PluginId} {bundledVersion} installiert in {target} (vorher: {installedVersion ?? "keins"}).");

            if (installedVersion is not null)
            {
                return $"Obsidian-Plugin „DocuClick Diagrams“ im Vault auf Version {bundledVersion} aktualisiert. Obsidian einmal neu laden (Befehl „App neu laden“ oder neu starten), damit die neue Version aktiv wird.";
            }

            EnableCommunityPlugin(vaultRoot);
            return $"Obsidian-Plugin „DocuClick Diagrams“ {bundledVersion} wurde im Vault installiert und eingeschaltet. Obsidian einmal neu starten. Fragt Obsidian nach dem eingeschränkten Modus, „Community-Plugins aktivieren“ wählen – sonst zeigt Obsidian die Abläufe nur als Text.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            LogService.Log($"Obsidian-Plugin konnte nicht installiert werden: {ex.Message}");
            return $"Das Obsidian-Plugin „DocuClick Diagrams“ konnte nicht automatisch installiert werden ({ex.Message}). Die Aufnahme funktioniert trotzdem; das Plugin lässt sich von Hand installieren.";
        }
    }

    /// <summary>The plugin's main.js, assembled like obsidian/build.py: document module, editor template, Cytoscape, plugin code.</summary>
    public static string BuildMainJs(string sourceFolder, string assetsFolder)
    {
        var document = File.ReadAllText(Path.Combine(sourceFolder, "document.js"));
        var main = File.ReadAllText(Path.Combine(sourceFolder, "main.js"));
        var template = File.ReadAllText(Path.Combine(assetsFolder, "viewer.template.html"));
        var cytoscape = File.ReadAllText(Path.Combine(assetsFolder, "vendor", "cytoscape.min.js"));
        return "\"use strict\";\nconst DocuClickDocument = (() => { const module = {exports: {}};\n" + document + "\nreturn module.exports; })();\n"
            + "const VIEWER_TEMPLATE = " + JsonSerializer.Serialize(template) + ";\n"
            + "const CYTOSCAPE = " + JsonSerializer.Serialize(cytoscape) + ";\n"
            + main;
    }

    /// <summary>Adds the plugin to the vault's enabled community plugins (keeps the others).</summary>
    private static void EnableCommunityPlugin(string vaultRoot)
    {
        var path = Path.Combine(vaultRoot, ".obsidian", "community-plugins.json");
        var list = File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonArray existing ? existing : new JsonArray();
        if (list.Any(node => node?.GetValueKind() == JsonValueKind.String && (string?)node == PluginId))
        {
            return;
        }

        list.Add(PluginId);
        WriteAtomically(path, list.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string? ReadVersion(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        try
        {
            return (string?)JsonNode.Parse(File.ReadAllText(manifestPath))?["version"];
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return null; // damaged install: treated as missing and replaced
        }
    }

    /// <summary>Semantic version order of "major.minor.patch" (pre-release suffixes ignored).</summary>
    private static int Compare(string a, string b)
    {
        static int[] Parts(string version) => version.Split('-', '+')[0].Split('.')
            .Select(part => int.TryParse(part, out var number) ? number : 0).Concat(new[] { 0, 0, 0 }).Take(3).ToArray();
        var (x, y) = (Parts(a), Parts(b));
        for (var i = 0; i < 3; i++)
        {
            if (x[i] != y[i])
            {
                return x[i].CompareTo(y[i]);
            }
        }

        return 0;
    }

    private static void WriteAtomically(string path, string text)
    {
        var temp = path + ".docuclick-tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}
