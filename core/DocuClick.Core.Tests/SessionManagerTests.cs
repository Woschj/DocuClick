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

    private static SKBitmap White(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        bitmap.Erase(SKColors.White);
        return bitmap;
    }

    public void Dispose() => _folder.Dispose();

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
