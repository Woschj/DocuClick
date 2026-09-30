using DocuClick.Platform;
using DocuClick.Services;
using SkiaSharp;

namespace DocuClick.Core.Tests;

/// <summary>End-to-end capture pipeline against fake platform services.</summary>
public sealed class SessionManagerTests : IDisposable
{
    private readonly TempFolder _folder = new();
    private readonly AppConfig _config = new();
    private readonly FakeInput _input = new();
    private readonly FakeCapture _capture = new();
    private readonly FakeSounds _sounds = new();
    private readonly FakeElements _elements = new();

    private SessionManager CreateSession(AppConfig config) =>
        new(config, new PlatformServices(_input, _capture, _elements, new FakeForeground(), _sounds));

    private static byte[] WaitForCapture(SessionManager session, Action trigger)
    {
        var captured = new TaskCompletionSource<byte[]>();
        session.LastScreenshotCaptured += png => captured.TrySetResult(png);
        session.ErrorOccurred += message => captured.TrySetException(new Exception(message));
        trigger();
        Assert.True(captured.Task.Wait(TimeSpan.FromSeconds(10)), "Keine Aufnahme innerhalb von 10 s.");
        return captured.Task.Result;
    }

    [Fact]
    public void Retina_click_is_highlighted_at_the_click_point_and_downscaled()
    {
        var config = _config;
        config.EnableClickSound = true;
        using var session = CreateSession(config);
        session.Start(_folder.File("Test.html"));

        // Window at (100, 100), 200×100 points, captured at 2× (400×200 px).
        _capture.NextFrame = () => new CapturedFrame(White(400, 200), new ScreenRect(100, 100, 200, 100), 2.0);
        var png = WaitForCapture(session, () => _input.Click(new ScreenPoint(150, 150)));

        using var saved = SKBitmap.Decode(png);
        Assert.Equal((200, 100), (saved.Width, saved.Height));

        // Circle radius 24 pt around local (50, 50): the ring is red, the far corner untouched.
        var ring = saved.GetPixel(50 + 24, 50);
        Assert.True(ring.Red > 150 && ring.Green < 150, $"Ring nicht rot: {ring}");
        Assert.Equal(SKColors.White, saved.GetPixel(195, 5));

        Assert.Contains(CanvasDocumentIo.Load(_folder.File("Test.html")).Nodes, n => n.Text == "Linksklick auf Taste „OK“ im Fenster „Dialog“");
        Assert.Equal(1, _sounds.Captured);
    }

    [Theory]
    [InlineData("WebP", "webp", "image/webp")]
    [InlineData("Jpeg", "jpg", "image/jpeg")]
    [InlineData("Png", "png", "image/png")]
    public void Screenshots_are_saved_in_the_configured_format(string format, string extension, string mime)
    {
        var config = _config;
        config.ScreenshotFormat = format;
        using var session = CreateSession(config);
        session.Start(_folder.File("Test.html"));

        _capture.NextFrame = () => new CapturedFrame(White(200, 100), new ScreenRect(0, 0, 200, 100), 1.0);
        var bytes = WaitForCapture(session, () => _input.Click(new ScreenPoint(10, 10)));
        session.Stop();

        Assert.Equal(mime, ImageData.MimeType(bytes));
        var file = Assert.Single(Directory.GetFiles(_folder.File("Attachments"), "*", SearchOption.AllDirectories));
        Assert.EndsWith("." + extension, file);
        Assert.Contains($"data:{mime};base64,", File.ReadAllText(_folder.File("Test.html"))); // right type in the page
    }

    [Fact]
    public void Recording_into_a_vault_installs_the_plugin_and_pairs_it_with_the_app()
    {
        Directory.CreateDirectory(_folder.File(".obsidian"));
        var config = _config;
        config.RemoteControlToken = "geheim";
        using var session = CreateSession(config);
        session.Start(_folder.File("Ablauf.md"));

        var plugin = _folder.File(".obsidian/plugins/docuclick-diagrams");
        Assert.True(File.Exists(Path.Combine(plugin, "main.js")));
        var link = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path.Combine(plugin, "app-link.json"))).RootElement;
        Assert.Equal("geheim", link.GetProperty("token").GetString());
        Assert.Equal(LocalSaveService.Port, link.GetProperty("port").GetInt32());
    }

    [Fact]
    public void Retina_capture_is_kept_at_full_resolution_when_downscaling_is_off()
    {
        var config = _config;
        config.DownscaleHiDpiScreenshots = false;
        using var session = CreateSession(config);
        session.Start(_folder.File("Test.html"));

        _capture.NextFrame = () => new CapturedFrame(White(400, 200), new ScreenRect(0, 0, 200, 100), 2.0);
        var png = WaitForCapture(session, () => _input.Click(new ScreenPoint(10, 10)));

        using var saved = SKBitmap.Decode(png);
        Assert.Equal((400, 200), (saved.Width, saved.Height));
    }

    [Fact]
    public void Before_click_timing_uses_and_releases_the_pre_click_frame()
    {
        var config = _config;
        config.CaptureTiming = "BeforeClick";
        using var session = CreateSession(config);
        session.Start(_folder.File("Test.html"));

        _capture.NextFrame = () => new CapturedFrame(White(20, 20), new ScreenRect(0, 0, 20, 20), 1.0);
        WaitForCapture(session, () => _input.Click(new ScreenPoint(5, 5)));

        var frame = Assert.IsType<FakePreClickFrame>(_capture.LastPreClick);
        Assert.True(frame.Disposed);
        Assert.Equal(1, _capture.SessionsBegun);
    }

    [Fact]
    public void Clicks_on_own_ui_and_with_the_skip_modifier_are_not_recorded()
    {
        var config = _config;
        config.SkipRecordingModifier = "Alt";
        config.EnableClickSound = true;
        using var session = CreateSession(config);
        session.IsPointOnOwnUi = p => p.Y < 10;
        session.Start(_folder.File("Test.html"));

        _input.Click(new ScreenPoint(5, 5));
        _input.Click(new ScreenPoint(50, 50), alt: true);

        Assert.Equal(0, _capture.Captures);
        Assert.Equal(1, _sounds.Skipped);
        Assert.Empty(Directory.GetFiles(_folder.Path, "*.png", SearchOption.AllDirectories));
    }

    [Fact]
    public void Password_fields_are_never_captured()
    {
        using var session = CreateSession(_config);
        session.Start(_folder.File("Test.html"));
        _elements.Password = true;

        _input.Click(new ScreenPoint(10, 10));
        Thread.Sleep(300); // processed on the writer thread

        Assert.Equal(0, _capture.Captures);
        Assert.Equal(1, _sounds.Skipped);
    }

    [Fact]
    public async Task A_browser_edit_of_the_loaded_Ablauf_is_taken_over_by_the_session()
    {
        using var session = CreateSession(_config);
        var path = _folder.File("Test.html");
        session.Start(path);
        _capture.NextFrame = () => new CapturedFrame(White(20, 20), new ScreenRect(0, 0, 20, 20), 1.0);
        WaitForCapture(session, () => _input.Click(new ScreenPoint(5, 5)));

        var edited = CanvasDocumentIo.Load(path);
        edited.Nodes.First(n => n.Type == "text").Text = "Im Browser umbenannt";
        await session.ApplyExternalEdit(path, edited);

        // The next recorded click must not bring back the old text.
        WaitForCapture(session, () => _input.Click(new ScreenPoint(6, 6)));
        Assert.Contains(CanvasDocumentIo.Load(path).Nodes, n => n.Text == "Im Browser umbenannt");
    }

    [Fact]
    public async Task Redacted_areas_are_burnt_into_the_html_but_the_original_file_stays()
    {
        var path = _folder.File("Test.html");
        using (var session = CreateSession(_config))
        {
            session.Start(path);
            _capture.NextFrame = () => new CapturedFrame(White(40, 40), new ScreenRect(0, 0, 40, 40), 1.0);
            WaitForCapture(session, () => _input.Click(new ScreenPoint(5, 5)));

            var edited = CanvasDocumentIo.Load(path);
            var image = edited.Nodes.First(n => n.Type == "file");
            image.Redactions = new List<ImageRedaction> { new() { X = 0, Y = 0, W = 1, H = 1, Mode = "black" } };
            await session.ApplyExternalEdit(path, edited);
            session.Stop();
        }

        var html = File.ReadAllText(path);
        var doc = CanvasDocumentIo.Load(path);
        var file = doc.Nodes.Single(n => n.Type == "file");
        Assert.Equal("black", Assert.Single(file.Redactions!).Mode);
        var original = File.ReadAllBytes(Path.Combine(_folder.Path, file.File!));
        Assert.InRange(SKBitmap.Decode(original).GetPixel(20, 20).Red, 235, 255);
        Assert.DoesNotContain(Convert.ToBase64String(original), html);

        var flow = System.Text.Json.Nodes.JsonNode.Parse(System.Text.RegularExpressions.Regex.Match(html, @"const flowData = (\{.*?\});\r?\n").Groups[1].Value)!;
        var shown = flow["nodes"]!.AsArray().Select(n => n!["data"]!).Single(d => d["imageUrl"] is not null);
        Assert.Equal(1, (int)shown["redactionsBaked"]!);
        var src = (string)shown["imageUrl"]!;
        Assert.InRange(SKBitmap.Decode(Convert.FromBase64String(src[(src.IndexOf(',') + 1)..])).GetPixel(20, 20).Red, 0, 10);
    }

    [Fact]
    public async Task An_embedded_image_with_areas_keeps_its_original_as_file_not_in_the_html()
    {
        var path = _folder.File("Test.html");
        using var white = White(30, 30);
        using var png = SKImage.FromBitmap(white).Encode(SKEncodedImageFormat.Png, 100);
        var dataUri = ImageData.DataUri(png.ToArray());
        using (var session = CreateSession(_config))
        {
            session.Start(path);
            _capture.NextFrame = () => new CapturedFrame(White(20, 20), new ScreenRect(0, 0, 20, 20), 1.0);
            WaitForCapture(session, () => _input.Click(new ScreenPoint(5, 5)));

            var edited = CanvasDocumentIo.Load(path);
            var image = edited.Nodes.First(n => n.Type == "file");
            image.File = dataUri;
            image.Redactions = new List<ImageRedaction> { new() { X = 0.5, Y = 0.5, W = 0.5, H = 0.5, Mode = "blur" } };
            await session.ApplyExternalEdit(path, edited);
            await session.ApplyExternalEdit(path, CanvasDocumentIo.Load(path)); // saving again reuses the stored original
            session.Stop();
        }

        var html = File.ReadAllText(path);
        Assert.DoesNotContain(dataUri[(dataUri.IndexOf(',') + 1)..], html);
        var file = CanvasDocumentIo.Load(path).Nodes.Single(n => n.Type == "file");
        Assert.StartsWith("Attachments/Originale/", file.File);
        Assert.Single(Directory.GetFiles(Path.Combine(_folder.Path, "Attachments", "Originale")));
        Assert.Equal(png.ToArray(), File.ReadAllBytes(Path.Combine(_folder.Path, file.File!)));
    }

    [Fact]
    public void Blurred_areas_make_fine_detail_unreadable()
    {
        using var stripes = new SKBitmap(240, 120);
        using (var canvas = new SKCanvas(stripes))
        {
            canvas.Clear(SKColors.White);
            using var black = new SKPaint { Color = SKColors.Black };
            for (var x = 0; x < 240; x += 4)
            {
                if (x % 8 != 0) canvas.DrawRect(x, 0, 4, 120, black);
            }
        }

        using var png = SKImage.FromBitmap(stripes).Encode(SKEncodedImageFormat.Png, 100);
        var result = ImageRedactor.Apply(png.ToArray(), new List<ImageRedaction>
        {
            new() { X = 0, Y = 0, W = 0.5, H = 1, Mode = "blur" },
            new() { X = 2, Y = double.NaN, W = 1, H = 1, Mode = "evil" }, // ignored
        });

        using var redacted = SKBitmap.Decode(result);
        var blurred = redacted.GetPixel(50, 60);
        Assert.InRange(blurred.Red, 60, 200);
        Assert.InRange(redacted.GetPixel(201, 60).Red, 235, 255); // outside the area: unchanged
    }

    private static SKBitmap White(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.White);
        return bitmap;
    }

    public void Dispose() => _folder.Dispose();

    private sealed class PageHost : IFlowEditorHost
    {
        public Queue<string?> Answers { get; } = new();
        public List<System.Text.Json.JsonElement> Posted { get; } = new();
        public Task<string?> PromptTextAsync(string? title = null, string? label = null, string? initialValue = null) => Task.FromResult(Answers.Count > 0 ? Answers.Dequeue() : null);
        public void PostToWeb(string json) => Posted.Add(System.Text.Json.JsonDocument.Parse(json).RootElement.Clone());
    }

    private static string Save(int request, string canvasJson) =>
        $$"""{ "kind": "save", "request": {{request}}, "canvas": {{canvasJson}} }""";

    [Fact]
    public async Task The_template_overview_saves_page_edits_and_receives_new_clicks()
    {
        using var session = CreateSession(_config);
        session.Start(_folder.File("Test.html"));
        _capture.NextFrame = () => new CapturedFrame(White(40, 20), new ScreenRect(0, 0, 40, 20), 1.0);
        WaitForCapture(session, () => _input.Click(new ScreenPoint(5, 5)));

        var web = new PageHost();
        var host = new EditorPageHost(session, web, (folder, file) => "https://docuclick.session/" + file);
        var page = host.BuildPage()!;
        Assert.Contains("window.docuclickEditorHost", page);
        Assert.Contains("https://docuclick.session/Attachments/Test/", page); // screenshots by URL, not embedded

        // An edit in the page (rename) is saved into the file; the resulting update is not echoed back as a replace.
        var canvas = session.GetEditorDocument(f => f)!.CanvasJson;
        await host.HandleMessageAsync(Save(1, canvas.Replace("Linksklick", "Umbenannt")));
        Assert.Null(web.Posted.Last().GetProperty("error").GetString());
        Assert.Contains("Umbenannt", File.ReadAllText(_folder.File("Test.html")));
        host.OnSessionChanged();
        Assert.Equal("setCurrent", web.Posted.Last().GetProperty("kind").GetString());

        // A new click reaches the page as a replace with the new step marked as current.
        WaitForCapture(session, () => _input.Click(new ScreenPoint(6, 6)));
        host.OnSessionChanged();
        var replace = web.Posted.Last();
        Assert.Equal("replace", replace.GetProperty("kind").GetString());
        Assert.Equal(2, replace.GetProperty("flow").GetProperty("nodes").GetArrayLength());
        var newest = replace.GetProperty("flow").GetProperty("nodes")[1].GetProperty("data").GetProperty("id").GetString();
        Assert.Equal(newest, replace.GetProperty("currentId").GetString());

        // "Hier weiter aufnehmen" goes through the session (which resumes at the end
        // of that step's path) and only moves the marker in the page.
        var first = replace.GetProperty("flow").GetProperty("nodes")[0].GetProperty("data").GetProperty("id").GetString();
        await host.HandleMessageAsync($$"""{ "kind": "jumpTo", "nodeId": "{{first}}" }""");
        host.OnSessionChanged();
        Assert.Equal("setCurrent", web.Posted.Last().GetProperty("kind").GetString());
        Assert.Equal(newest, web.Posted.Last().GetProperty("nodeId").GetString());
    }

    [Fact]
    public async Task A_new_path_can_start_at_any_step_and_be_continued_later_from_the_page()
    {
        using var session = CreateSession(_config);
        var path = _folder.File("Test.html");
        session.Start(path);
        _capture.NextFrame = () => new CapturedFrame(White(40, 20), new ScreenRect(0, 0, 40, 20), 1.0);
        WaitForCapture(session, () => _input.Click(new ScreenPoint(5, 5)));
        WaitForCapture(session, () => _input.Click(new ScreenPoint(6, 6)));
        var web = new PageHost();
        var host = new EditorPageHost(session, web, (folder, file) => file);
        host.BuildPage();
        string Step(string text) => CanvasDocumentIo.Load(path).Nodes.Single(n => n.Type == "text" && n.Text == text).Id;

        // "Neuer Pfad ab hier" on an ordinary recorded step (not a decision point).
        var steps = CanvasDocumentIo.Load(path).Nodes.Where(n => n.Type == "text").OrderBy(n => n.Y).ToList();
        web.Answers.Enqueue("Fehlerfall");
        await host.HandleMessageAsync($$"""{ "kind": "newPath", "nodeId": "{{steps[0].Id}}" }""");
        WaitForCapture(session, () => _input.Click(new ScreenPoint(7, 7)));
        var pathStart = Step("↳ Pfad: Fehlerfall");
        var doc = CanvasDocumentIo.Load(path);
        Assert.Contains(doc.Edges, e => e.FromNode == steps[0].Id && e.ToNode == pathStart);
        var pathStep = doc.Edges.Single(e => e.FromNode == pathStart).ToNode;

        // Back to the main line, then "Pfad „Fehlerfall“ fortsetzen": the next click follows that path's last step.
        await host.HandleMessageAsync($$"""{ "kind": "jumpTo", "nodeId": "{{steps[1].Id}}" }""");
        await host.HandleMessageAsync($$"""{ "kind": "continuePath", "nodeId": "{{pathStart}}" }""");
        WaitForCapture(session, () => _input.Click(new ScreenPoint(8, 8)));
        Assert.Single(CanvasDocumentIo.Load(path).Edges, e => e.FromNode == pathStep);
    }

    [Fact]
    public async Task Undo_in_the_page_takes_back_a_recorded_click_and_recording_continues_from_there()
    {
        using var session = CreateSession(_config);
        session.Start(_folder.File("Test.html"));
        _capture.NextFrame = () => new CapturedFrame(White(40, 20), new ScreenRect(0, 0, 40, 20), 1.0);
        WaitForCapture(session, () => _input.Click(new ScreenPoint(5, 5)));
        var web = new PageHost();
        var host = new EditorPageHost(session, web, (folder, file) => file);
        host.BuildPage();
        var beforeSecondClick = session.GetEditorDocument(f => f)!.CanvasJson;
        WaitForCapture(session, () => _input.Click(new ScreenPoint(6, 6)));
        host.OnSessionChanged(); // the page shows the second click

        await host.HandleMessageAsync(Save(3, beforeSecondClick)); // "Zurück" in the page

        Assert.Null(web.Posted.Last().GetProperty("error").GetString());
        Assert.Single(session.GetPreview()!.Nodes);
        WaitForCapture(session, () => _input.Click(new ScreenPoint(7, 7)));
        var preview = session.GetPreview()!;
        Assert.Equal(2, preview.Nodes.Count);
        Assert.Single(preview.Edges); // the next click attaches to the remaining step
    }

    [Fact]
    public async Task A_page_save_based_on_an_older_state_never_drops_a_recorded_click()
    {
        using var session = CreateSession(_config);
        session.Start(_folder.File("Test.html"));
        _capture.NextFrame = () => new CapturedFrame(White(40, 20), new ScreenRect(0, 0, 40, 20), 1.0);
        WaitForCapture(session, () => _input.Click(new ScreenPoint(5, 5)));
        var web = new PageHost();
        var host = new EditorPageHost(session, web, (folder, file) => file);
        host.BuildPage();
        var stale = session.GetEditorDocument(f => f)!.CanvasJson;

        WaitForCapture(session, () => _input.Click(new ScreenPoint(6, 6))); // recorded, page not updated yet
        await host.HandleMessageAsync(Save(7, stale.Replace("Linksklick", "Umbenannt")));

        Assert.Equal("replace", web.Posted[^2].GetProperty("kind").GetString()); // page gets the newer state
        Assert.Contains("weiter aufgenommen", web.Posted[^1].GetProperty("error").GetString());
        Assert.Equal(2, session.GetPreview()!.Nodes.Count); // the second click is still there
    }

    private sealed class FakeInput : IInputMonitor
    {
        public event EventHandler<MouseClickEventArgs>? LeftButtonDown;
#pragma warning disable CS0067 // part of the interface, not raised by these tests
        public event EventHandler<MouseClickEventArgs>? RightButtonDown;
        public event EventHandler<EnterKeyEventArgs>? EnterPressed;
#pragma warning restore CS0067

        public void Click(ScreenPoint point, bool alt = false) => LeftButtonDown?.Invoke(this, new MouseClickEventArgs
        {
            Point = point, Timestamp = DateTime.Now, ShiftDown = false, ControlDown = false, AltDown = alt
        });

        public ModifierState CurrentModifiers => default;
        public void Start(bool captureEnter) { }
        public void Stop() { }
        public void Dispose() { }
    }

    private sealed class FakePreClickFrame : PreClickFrame
    {
        public bool Disposed { get; private set; }
        public override void Dispose() => Disposed = true;
    }

    private sealed class FakeCapture : IScreenCapture
    {
        public Func<CapturedFrame> NextFrame { get; set; } = () => throw new InvalidOperationException("kein Bild");
        public PreClickFrame? LastPreClick { get; private set; }
        public int Captures { get; private set; }
        public int SessionsBegun { get; private set; }

        public void BeginSession() => SessionsBegun++;
        public PreClickFrame? GrabPreClickFrame() => new FakePreClickFrame();

        public CapturedFrame CaptureWindowAt(ScreenPoint point, PreClickFrame? preClick) => Capture(preClick);
        public CapturedFrame CaptureAroundPoint(ScreenPoint point, int radius, PreClickFrame? preClick) => Capture(preClick);
        public CapturedFrame CaptureForegroundWindow(PreClickFrame? preClick) => Capture(preClick);

        private CapturedFrame Capture(PreClickFrame? preClick)
        {
            Captures++;
            LastPreClick = preClick;
            return NextFrame();
        }
    }

    private sealed class FakeElements : IElementInspector
    {
        // Bounding box as large as the window: must fall back to the click circle.
        public bool Password { get; set; }
        public ElementInfo? GetElementAt(ScreenPoint point) => new("OK", "Taste", "Dialog", new ScreenRect(0, 0, 10_000, 10_000), Password);
        public ElementInfo? GetFocusedElement() => null;
    }

    private sealed class FakeForeground : IForegroundWindow
    {
        public string? GetTitle() => "Dialog";
    }

    private sealed class FakeSounds : IFeedbackSounds
    {
        public int Captured, Skipped, Errors;
        public void PlayCaptured() => Captured++;
        public void PlaySkipped() => Skipped++;
        public void PlayError() => Errors++;
    }
}
