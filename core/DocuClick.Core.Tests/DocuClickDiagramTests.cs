using System.Text.Json.Nodes;
using DocuClick.Services;

namespace DocuClick.Core.Tests;

/// <summary>Recording into an Obsidian vault: the .docuclick format shared with the DocuClick Diagrams plugin.</summary>
public sealed class DocuClickDiagramTests : IDisposable
{
    private static readonly string Png = "data:image/png;base64," + Convert.ToBase64String(TestImages.Png(3, 2).Png);

    private readonly TempFolder _vault = new();
    private readonly AppConfig _config = new();

    public DocuClickDiagramTests() => Directory.CreateDirectory(_vault.File(".obsidian"));

    private string Diagram => Path.Combine(_vault.Path, "Prozesse", "Ablauf.docuclick");

    private CanvasFlowWriter StartWriter()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Diagram)!);
        var writer = new CanvasFlowWriter(_config);
        writer.StartSession(Diagram);
        return writer;
    }

    private static void Click(CanvasFlowWriter writer, string text) =>
        writer.AddClickNode(text, TestImages.Png(40, 20), DateTime.Now);

    private JsonObject ReadJson() => (JsonObject)JsonNode.Parse(File.ReadAllText(Diagram))!;

    [Fact]
    public void A_folder_inside_a_vault_is_recognized_and_gets_the_diagram_extension()
    {
        var nested = Path.Combine(_vault.Path, "01 Prozesse", "IT");
        Directory.CreateDirectory(nested);

        Assert.Equal(Path.GetFullPath(_vault.Path), ObsidianVault.FindRoot(nested));
        Assert.Equal(".docuclick", ObsidianVault.OutputExtensionFor(nested));

        using var outside = new TempFolder();
        Assert.Null(ObsidianVault.FindRoot(outside.Path));
        Assert.Equal(".html", ObsidianVault.OutputExtensionFor(outside.Path));
    }

    [Fact]
    public void Clicks_are_written_as_a_diagram_with_screenshots_as_vault_files()
    {
        var writer = StartWriter();
        Click(writer, "Linksklick auf „Öffnen“");
        Click(writer, "Linksklick auf „Speichern“");
        writer.Stop();

        var text = File.ReadAllText(Diagram);
        Assert.DoesNotContain("base64", text); // small file: images are not embedded on every click
        Assert.Contains("„Öffnen“", text);     // readable, not \u-escaped

        var json = ReadJson();
        Assert.Equal("docuclick-diagram", (string?)json["format"]);
        Assert.Equal(1, (int?)json["version"]);
        var textIds = json["canvas"]!["nodes"]!.AsArray().Where(n => (string?)n!["type"] == "text").Select(n => (string)n!["id"]!).ToList();
        var flowIds = json["flow"]!["nodes"]!.AsArray().Select(n => (string)n!["data"]!["id"]!).ToList();
        Assert.Equal(textIds.Order(), flowIds.Order()); // the plugin requires one rendered node per text node
        Assert.Single(json["flow"]!["edges"]!.AsArray());
        Assert.DoesNotContain(json["canvas"]!["nodes"]!.AsArray(), n => n!["file"] is not null);

        var images = json["images"]!.AsObject();
        Assert.Equal(2, images.Count);
        foreach (var (_, value) in images)
        {
            var vaultPath = (string)value!;
            Assert.StartsWith("Prozesse/Attachments/Ablauf/", vaultPath); // relative to the vault root, as the plugin expects
            Assert.True(File.Exists(Path.Combine(_vault.Path, vaultPath)));
        }
    }

    [Fact]
    public void A_recorded_diagram_is_continued_after_a_reload()
    {
        var writer = StartWriter();
        Click(writer, "Eins");
        writer.Stop();

        var reloaded = StartWriter();
        Click(reloaded, "Zwei");
        reloaded.Stop();

        var preview = new CanvasFlowWriter(_config);
        preview.StartSession(Diagram);
        var nodes = preview.GetPreview().Nodes;
        Assert.Equal(2, nodes.Count);
        Assert.All(nodes, n => Assert.NotNull(n.ImagePath));
        Assert.Single(preview.GetPreview().Edges);
    }

    /// <summary>
    /// What the plugin writes after an edit: text node positions from the
    /// graph, file nodes without "file", one screenshot moved to its image
    /// folder ("Als Dateien im Vault") and one embedded ("In der Datei").
    /// </summary>
    private void WritePluginStyleDiagram()
    {
        Directory.CreateDirectory(_vault.File("DocuClick-Bilder"));
        File.WriteAllBytes(_vault.File("DocuClick-Bilder/abc.png"), TestImages.Png(4, 4).Png);
        Directory.CreateDirectory(Path.GetDirectoryName(Diagram)!);
        File.WriteAllText(Diagram, $$"""
            {
              "format": "docuclick-diagram", "version": 1,
              "canvas": {
                "nodes": [
                  { "id": "a", "type": "text", "text": "Anmelden", "x": 0, "y": 0, "width": 380, "height": 60 },
                  { "id": "fa", "type": "file", "x": 0, "y": 70, "width": 380, "height": 270 },
                  { "id": "b", "type": "text", "text": "Speichern", "x": 0, "y": 400, "width": 380, "height": 60 }
                ],
                "edges": [ { "id": "edge-0", "fromNode": "a", "toNode": "b", "fromSide": "bottom", "toSide": "top", "docuClickManual": false, "color": "#2563EB", "lineStyle": "solid" } ]
              },
              "flow": {
                "nodes": [
                  { "data": { "id": "a", "label": "Anmelden", "color": "#2563EB", "shape": "round-rectangle", "stepIndex": 1 }, "position": { "x": 0, "y": 0 } },
                  { "data": { "id": "b", "label": "Speichern", "color": "#2563EB", "shape": "round-rectangle", "stepIndex": 2, "imageUrl": "{{Png}}" }, "position": { "x": 0, "y": 400 } }
                ],
                "edges": [ { "data": { "id": "a->b", "source": "a", "target": "b", "color": "#2563EB", "manual": false, "lineStyle": "solid" } } ]
              },
              "images": { "a": "DocuClick-Bilder/abc.png", "b-missing": "../../outside.png" }
            }
            """);
    }

    [Fact]
    public void A_diagram_saved_by_the_plugin_is_read_with_both_kinds_of_screenshots()
    {
        WritePluginStyleDiagram();

        var doc = CanvasDocumentIo.Load(Diagram);
        var files = doc.Nodes.Where(n => n.Type == "file").ToDictionary(n => n.Y, n => n.File);
        Assert.Equal("../DocuClick-Bilder/abc.png", files[70]);  // vault path, relative to the diagram
        Assert.Equal(Png, files[470]);                           // embedded image gets a file node again

        var writer = StartWriter();
        Click(writer, "Neu");
        writer.Stop();

        var json = ReadJson();
        Assert.Equal("DocuClick-Bilder/abc.png", (string?)json["images"]!["a"]); // kept where the plugin put it
        var b = json["flow"]!["nodes"]!.AsArray().Single(n => (string?)n!["data"]!["id"] == "b")!;
        Assert.Equal(Png, (string?)b["data"]!["imageUrl"]);                      // embedded stays embedded
        Assert.Equal(3, json["flow"]!["nodes"]!.AsArray().Count);
    }

    [Fact]
    public void Draw_io_export_includes_vault_and_embedded_screenshots()
    {
        WritePluginStyleDiagram();
        var target = _vault.File("Export.drawio");

        DrawIoConverter.Convert(Diagram, Path.GetDirectoryName(Diagram)!, target);

        // Tooltip images keep their real width: 4 px from the vault file, 3 px embedded (a missing image would be a 1×1 placeholder).
        var xml = File.ReadAllText(target);
        Assert.Contains("width=&quot;4&quot;", xml);
        Assert.Contains("width=&quot;3&quot;", xml);
    }

    [Fact]
    public void Paths_outside_the_vault_are_ignored()
    {
        WritePluginStyleDiagram();
        File.WriteAllText(Diagram, File.ReadAllText(Diagram).Replace("DocuClick-Bilder/abc.png", "../../../etc/passwd"));

        var doc = CanvasDocumentIo.Load(Diagram);

        Assert.Null(doc.Nodes.Single(n => n.Id == "fa").File);
    }

    [Fact]
    public void An_edit_made_in_Obsidian_during_recording_is_kept()
    {
        var writer = StartWriter();
        Click(writer, "Eins");
        writer.FlushPendingSave();

        // The plugin renames the step while the app still has the file loaded.
        var edited = File.ReadAllText(Diagram).Replace("\"Eins\"", "\"Eins (in Obsidian umbenannt)\"");
        File.WriteAllText(Diagram, edited);
        File.SetLastWriteTimeUtc(Diagram, DateTime.UtcNow.AddSeconds(5));

        Click(writer, "Zwei");
        writer.Stop();

        var labels = new CanvasFlowWriter(_config);
        labels.StartSession(Diagram);
        var nodes = labels.GetPreview().Nodes.Select(n => n.Label).ToList();
        Assert.Contains("Eins (in Obsidian umbenannt)", nodes);
        Assert.Contains("Zwei", nodes);
        Assert.Single(labels.GetPreview().Edges); // "Zwei" still follows the renamed step
    }

    [Fact]
    public void A_file_that_is_not_a_diagram_is_refused_instead_of_overwritten()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Diagram)!);
        File.WriteAllText(Diagram, """{ "format": "something-else", "version": 1 }""");

        Assert.Throws<InvalidOperationException>(() => new CanvasFlowWriter(_config).StartSession(Diagram));
        Assert.Contains("something-else", File.ReadAllText(Diagram));
    }

    /// <summary>
    /// obsidian/tests/fixtures/app-recording.docuclick is a real recording
    /// by this writer; the plugin's tests validate it with document.js, so
    /// both sides are held to the same format. Regenerate with
    /// DOCUCLICK_WRITE_FIXTURES=1 after a deliberate format change.
    /// </summary>
    [Fact]
    public void The_shared_fixture_is_a_current_recording()
    {
        var fixture = Path.Combine(RepoRoot(), "obsidian", "tests", "fixtures", "app-recording.docuclick");
        if (Environment.GetEnvironmentVariable("DOCUCLICK_WRITE_FIXTURES") == "1")
        {
            var writer = StartWriter();
            Click(writer, "Linksklick auf „Anmelden“");
            Assert.True(writer.MarkDecisionPoint("Erfolg").Success);
            Click(writer, "Linksklick auf „Weiter“");
            writer.Stop();
            Directory.CreateDirectory(Path.GetDirectoryName(fixture)!);
            File.Copy(Diagram, fixture, overwrite: true);
        }

        var doc = DocuClickDiagramIo.Parse(File.ReadAllText(fixture), Diagram);
        Assert.Equal(4, doc.Nodes.Count(n => n.Type == "text"));
        Assert.Equal(2, doc.Nodes.Count(n => n.Type == "file" && n.File is not null));
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
