using DocuClick.Services;

namespace DocuClick.Core.Tests;

/// <summary>Recording behavior of the (only) flow writer and its on-disk HTML format.</summary>
public sealed class CanvasFlowWriterTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly AppConfig _config = new();

    private string Ablauf => _folder.File("Ablauf.html");

    private CanvasFlowWriter StartWriter()
    {
        var writer = new CanvasFlowWriter(_config);
        writer.StartSession(Ablauf);
        return writer;
    }

    private static void Click(CanvasFlowWriter writer, string text) =>
        writer.AddClickNode(text, TestImages.Png(400, 200), DateTime.Now);

    private static List<PreviewNode> Content(FlowPreview preview) =>
        preview.Nodes.Where(n => !n.IsDecisionPoint && !n.IsPathStart).ToList();

    [Fact]
    public void Clicks_form_a_connected_chain_written_as_a_self_contained_html_file()
    {
        var writer = StartWriter();
        Click(writer, "Linksklick auf Taste „Eins“");
        Click(writer, "Linksklick auf Taste „Zwei“");
        Click(writer, "Linksklick auf Taste „Drei“");
        writer.FlushPendingSave();

        var preview = writer.GetPreview();
        Assert.Equal(3, Content(preview).Count);
        Assert.Equal(2, preview.Edges.Count(e => !e.Manual));
        Assert.Contains("Drei", Assert.Single(preview.Nodes, n => n.IsCurrent).Label);

        var html = File.ReadAllText(Ablauf);
        Assert.Contains("<script id=\"docuclick-data\" type=\"application/json\">", html);
        Assert.Contains("data:image/png;base64,", html); // screenshots embedded, file works on its own
        Assert.Equal(3, Directory.GetFiles(_folder.File("Attachments"), "*.png", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void While_clicking_the_page_links_its_screenshots_and_embeds_them_once_it_is_quiet()
    {
        var writer = StartWriter();
        Click(writer, "Eins");
        Click(writer, "Zwei");

        var quick = File.ReadAllText(Ablauf);
        Assert.DoesNotContain(";base64,", quick);        // no re-embedding of every image per click
        Assert.Contains("Attachments/Ablauf/", quick);    // images still shown, via the folder next to it

        writer.FlushPendingSave();                        // pause, stop, app exit, or 3 s without clicks
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(Ablauf), "\"imageUrl\":\"data:image/").Count);
    }

    [Fact]
    public void A_flow_survives_a_reload_from_disk()
    {
        var writer = StartWriter();
        Click(writer, "Eins");
        Click(writer, "Zwei");
        writer.Stop();

        var reloaded = StartWriter();

        Assert.Equal(2, Content(reloaded.GetPreview()).Count);
        Assert.Equal(2, CanvasDocumentIo.Load(Ablauf).Nodes.Count(n => n.Type == "text"));
    }

    [Fact]
    public void Decision_point_without_any_click_is_refused()
    {
        Assert.False(StartWriter().MarkDecisionPoint("Erfolg").Success);
    }

    [Fact]
    public void Paths_fork_from_a_decision_point_and_can_be_resumed()
    {
        var writer = StartWriter();
        Click(writer, "Anmelden");

        Assert.True(writer.MarkDecisionPoint("Erfolg").Success);
        Click(writer, "Dashboard öffnen");

        var decisionPoint = Assert.Single(writer.GetPreview().Nodes, n => n.IsDecisionPoint);
        var erfolg = Assert.Single(writer.ListPaths(decisionPoint.Id));
        Assert.Equal("Erfolg", erfolg.Name);

        Assert.True(writer.StartNewPath(decisionPoint.Id, "Fehler").Success);
        Click(writer, "Fehlermeldung schließen");
        Assert.Equal(new[] { "Erfolg", "Fehler" }, writer.ListPaths(decisionPoint.Id).Select(p => p.Name).Order());

        Assert.True(writer.ContinuePath(erfolg.PathStartNodeId).Success);
        Click(writer, "Bericht exportieren");
        Assert.Equal(erfolg.StepCount + 1, writer.ListPaths(decisionPoint.Id).Single(p => p.Name == "Erfolg").StepCount);

        var content = Content(writer.GetPreview());
        Assert.Equal("Erfolg", content.Single(n => n.Label.Contains("Bericht")).PathName);
        Assert.Equal("Fehler", content.Single(n => n.Label.Contains("Fehlermeldung")).PathName);
        Assert.Null(content.Single(n => n.Label.Contains("Anmelden")).PathName);
    }

    [Fact]
    public void Jumping_to_a_node_continues_at_the_end_of_its_path()
    {
        var writer = StartWriter();
        Click(writer, "Eins");
        Click(writer, "Zwei");
        var first = Content(writer.GetPreview()).Single(n => n.Label.Contains("Eins"));
        var second = Content(writer.GetPreview()).Single(n => n.Label.Contains("Zwei"));

        Assert.True(writer.JumpToNode(first.Id).Success);
        Click(writer, "Drei");

        var third = Content(writer.GetPreview()).Single(n => n.Label.Contains("Drei"));
        Assert.Contains(writer.GetPreview().Edges, e => e.FromId == second.Id && e.ToId == third.Id);
    }

    [Fact]
    public void Draw_io_export_contains_one_card_per_recorded_step()
    {
        var writer = StartWriter();
        Click(writer, "Eins");
        Click(writer, "Zwei");
        writer.Stop();

        var drawio = _folder.File("Ablauf.drawio");
        DrawIoConverter.Convert(Ablauf, _folder.Path, drawio);

        var xml = File.ReadAllText(drawio);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(xml, "id=\"card_[0-9a-f]+\"").Count);
        Assert.Contains("data:image/png", xml);
    }

    public void Dispose() => _folder.Dispose();
}
