using System.Text.Json.Nodes;
using DocuClick.Services;

namespace DocuClick.Core.Tests;

/// <summary>Automatic install of the Obsidian plugin into a vault the app records into.</summary>
public sealed class ObsidianPluginInstallerTests : IDisposable
{
    private readonly TempFolder _vault = new();

    private string Plugin(string name) => Path.Combine(_vault.Path, ".obsidian", "plugins", "docuclick-diagrams", name);

    private static string BundledVersion => (string)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "ObsidianPlugin", "manifest.json")))!["version"]!;

    public ObsidianPluginInstallerTests() => Directory.CreateDirectory(_vault.File(".obsidian"));

    [Fact]
    public void A_vault_without_the_plugin_gets_it_installed_and_enabled()
    {
        File.WriteAllText(_vault.File(".obsidian/community-plugins.json"), """["dataview"]""");

        var message = ObsidianPluginInstaller.EnsureInstalled(_vault.Path);

        Assert.Contains("installiert", message);
        var main = File.ReadAllText(Plugin("main.js"));
        Assert.StartsWith("\"use strict\";\nconst DocuClickDocument = (() =>", main);
        Assert.Contains("const VIEWER_TEMPLATE = \"", main);
        Assert.Contains("const CYTOSCAPE = \"", main);
        Assert.Contains("class DiagramView extends FileView", main);
        Assert.True(File.Exists(Plugin("styles.css")));
        Assert.Equal(BundledVersion, (string?)JsonNode.Parse(File.ReadAllText(Plugin("manifest.json")))!["version"]);
        var enabled = JsonNode.Parse(File.ReadAllText(_vault.File(".obsidian/community-plugins.json")))!.AsArray().Select(n => (string?)n).ToList();
        Assert.Equal(new[] { "dataview", "docuclick-diagrams" }, enabled);

        Assert.Null(ObsidianPluginInstaller.EnsureInstalled(_vault.Path)); // nothing to do the second time
    }

    [Fact]
    public void An_older_plugin_is_updated_without_touching_its_settings_or_enabling_it()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Plugin("x"))!);
        File.WriteAllText(Plugin("manifest.json"), """{ "id": "docuclick-diagrams", "version": "0.1.0" }""");
        File.WriteAllText(Plugin("main.js"), "alt");
        File.WriteAllText(Plugin("data.json"), """{ "themeMode": "custom" }""");

        var message = ObsidianPluginInstaller.EnsureInstalled(_vault.Path);

        Assert.Contains("aktualisiert", message);
        Assert.NotEqual("alt", File.ReadAllText(Plugin("main.js")));
        Assert.Equal("""{ "themeMode": "custom" }""", File.ReadAllText(Plugin("data.json")));
        Assert.False(File.Exists(_vault.File(".obsidian/community-plugins.json"))); // the user's choice stays
    }

    [Fact]
    public void A_newer_plugin_is_never_downgraded()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Plugin("x"))!);
        File.WriteAllText(Plugin("manifest.json"), """{ "id": "docuclick-diagrams", "version": "99.0.0" }""");
        File.WriteAllText(Plugin("main.js"), "neuer");

        Assert.Null(ObsidianPluginInstaller.EnsureInstalled(_vault.Path));
        Assert.Equal("neuer", File.ReadAllText(Plugin("main.js")));
    }

    /// <summary>The app-assembled main.js must equal what obsidian/build.py produces (CI builds the release with it).</summary>
    [Fact]
    public void The_assembled_plugin_matches_the_build_script()
    {
        var built = Path.Combine(RepoRoot(), "dist", "obsidian-docuclick", "docuclick-diagrams", "main.js");
        var sources = new[] { "obsidian/src/main.js", "obsidian/src/document.js", "core/DocuClick.Core/WebAssets/viewer.template.html" }
            .Select(path => File.GetLastWriteTimeUtc(Path.Combine(RepoRoot(), path)));
        if (!File.Exists(built) || File.GetLastWriteTimeUtc(built) < sources.Max())
        {
            return; // build.py not run (or not since the last source change) in this checkout
        }

        var assembled = ObsidianPluginInstaller.BuildMainJs(
            Path.Combine(AppContext.BaseDirectory, "ObsidianPlugin"), Path.Combine(AppContext.BaseDirectory, "WebAssets"));
        // Same code; string literals may escape differently (Python ensure_ascii vs. System.Text.Json).
        string Code(string js) => System.Text.RegularExpressions.Regex.Replace(js, @"const (VIEWER_TEMPLATE|CYTOSCAPE) = "".*"";\n", "");
        Assert.Equal(Code(File.ReadAllText(built)), Code(assembled));
    }

    private static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "global.json")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository-Wurzel nicht gefunden.");
    }

    public void Dispose() => _vault.Dispose();
}
