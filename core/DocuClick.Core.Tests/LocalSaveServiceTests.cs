using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DocuClick.Services;

namespace DocuClick.Core.Tests;

/// <summary>Saving an Ablauf edited in a browser through DocuClick's loopback save service.</summary>
public sealed class LocalSaveServiceTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly int _port = FreePort();
    private readonly List<(string Path, CanvasDocument Doc)> _applied = new();
    private readonly LocalSaveService _service;
    private static readonly HttpClient Http = new();

    public LocalSaveServiceTests()
    {
        _service = new LocalSaveService((path, doc) =>
        {
            _applied.Add((path, doc));
            var writer = new CanvasFlowWriter(new AppConfig());
            writer.StartSession(path);
            writer.ReplaceDocument(doc);
            writer.Stop();
            return Task.CompletedTask;
        }, _port);
        Assert.True(_service.Start());
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private string RecordAblauf()
    {
        var path = _folder.File("Ablauf.html");
        var writer = new CanvasFlowWriter(new AppConfig());
        writer.StartSession(path);
        writer.AddClickNode("Eins", TestImages.Png(40, 20), DateTime.Now);
        writer.AddClickNode("Zwei", TestImages.Png(40, 20), DateTime.Now);
        writer.Stop();
        return path;
    }

    /// <summary>Exactly what the page's hidden form sends.</summary>
    private async Task<(HttpStatusCode Status, string Body)> PostAsync(string path, string token, CanvasDocument doc)
    {
        var payload = JsonSerializer.Serialize(new { path = new Uri(path).AbsolutePath, token, doc });
        using var response = await Http.PostAsync($"http://127.0.0.1:{_port}/save",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["payload"] = payload }));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_browser_edit_with_the_files_token_is_written_into_the_file()
    {
        var path = RecordAblauf();
        var doc = CanvasDocumentIo.Load(path);
        Assert.False(string.IsNullOrEmpty(doc.SaveToken));
        doc.Nodes.First(n => n.Text == "Eins").Text = "Anmelden";

        var (status, body) = await PostAsync(path, doc.SaveToken!, doc);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("\"ok\":true", body);
        var saved = CanvasDocumentIo.Load(path);
        Assert.Contains(saved.Nodes, n => n.Text == "Anmelden");
        Assert.Equal(doc.SaveToken, saved.SaveToken);
    }

    [Fact]
    public async Task A_wrong_token_is_rejected_and_the_file_stays_unchanged()
    {
        var path = RecordAblauf();
        var before = File.ReadAllText(path);
        var doc = CanvasDocumentIo.Load(path);
        doc.Nodes.Clear();

        var (status, _) = await PostAsync(path, "falsch", doc);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Empty(_applied);
        Assert.Equal(before, File.ReadAllText(path));
    }

    [Fact]
    public async Task Files_that_are_not_an_Ablauf_are_never_written()
    {
        var other = _folder.File("Notiz.html");
        File.WriteAllText(other, "<html>nichts</html>");

        var (status, _) = await PostAsync(other, "egal", new CanvasDocument());

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("<html>nichts</html>", File.ReadAllText(other));
    }

    [Fact]
    public void Windows_file_url_paths_are_normalized()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(@"C:\Users\a\Ablauf.html", LocalSaveService.NormalizeFilePath("/C:/Users/a/Ablauf.html"));
        }
        else
        {
            Assert.Equal("/Users/a/Ablauf.html", LocalSaveService.NormalizeFilePath("/Users/a/Ablauf.html"));
        }
    }

    public void Dispose()
    {
        _service.Dispose();
        _folder.Dispose();
    }
}
