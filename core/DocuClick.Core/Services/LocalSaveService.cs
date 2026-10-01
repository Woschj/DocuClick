using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DocuClick.Services;

/// <summary>
/// Lets an Ablauf opened in any browser save its edits straight back into
/// its own .html file — without the File System Access API's "Mit Datei
/// verbinden" step, which only Chrome/Edge offer at all (Safari and Firefox
/// can't write local files from a page). While DocuClick runs, the page
/// submits its edited document to this loopback-only endpoint and DocuClick
/// rebuilds the file exactly as its own writer would.
///
/// The page sends a regular (hidden) HTML form — the one cross-origin
/// request every browser allows from a file:// page (Safari blocks fetch
/// there; verified in Safari, Firefox and Brave). The response answers via
/// <c>parent.postMessage</c> into the page's hidden iframe.
///
/// Security: listens on 127.0.0.1 only; a save must name an existing
/// DocuClick Ablauf (.html with the embedded data block) and carry that
/// file's own <see cref="CanvasDocument.SaveToken"/>. A website could post a
/// form to this port too, but can't know the token — it can't read local
/// files — so only someone able to read the file can have it rewritten.
/// </summary>
public sealed class LocalSaveService : IDisposable
{
    /// <summary>Fixed so every Ablauf file knows where to send its edits (embedded into the page by <see cref="HtmlViewerBuilder"/>).</summary>
    public const int Port = 47811;

    private const int MaxRequestBytes = 64 * 1024 * 1024;

    private readonly Func<string, CanvasDocument, Task> _apply;
    private readonly int _port;
    private readonly CancellationTokenSource _stop = new();
    private TcpListener? _listener;

    /// <param name="apply">Writes the validated document into the file at the given path (SessionManager.ApplyExternalEdit).</param>
    public LocalSaveService(Func<string, CanvasDocument, Task> apply, int port = Port)
    {
        _apply = apply;
        _port = port;
    }

    /// <summary>
    /// Handles commands from the Obsidian plugin (POST /control); set by the
    /// app, which runs them on its UI thread. Null: control is off.
    /// </summary>
    public Func<RemoteCommand, Task<RemoteStatus>>? RemoteControl { get; set; }

    /// <summary>
    /// The app's Ablauf-Übersicht page on the shared template (macOS: its web
    /// view cannot read the session folder directly): GET /editor/page and
    /// GET /editor/image?p=… serve it and its screenshots, both only with
    /// <see cref="EditorToken"/> (random per app start) in the query.
    /// </summary>
    public Func<string?>? EditorPage { get; set; }

    /// <summary>Folder the editor page's screenshots are served from (vault root or session folder); null: none.</summary>
    public Func<string?>? EditorImageRoot { get; set; }

    /// <summary>Secret in the editor page's URLs (see <see cref="EditorPage"/>).</summary>
    public string EditorToken { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();

    /// <summary>URL of the editor page (with the token and a changing version to force a reload).</summary>
    public string EditorPageUrl(int version) => $"http://127.0.0.1:{_port}/editor/page?t={EditorToken}&v={version}";

    /// <summary>Page-relative URL of a screenshot (path relative to <see cref="EditorImageRoot"/>, forward slashes).</summary>
    public string EditorImageUrl(string relativePath) => $"/editor/image?t={EditorToken}&p={Uri.EscapeDataString(relativePath)}";

    /// <summary>The token the plugin must send (AppConfig.RemoteControlToken, written into the vault by ObsidianAppLink).</summary>
    public string? RemoteToken { get; set; }

    /// <summary>Starts listening; false (logged) if the port is taken — DocuClick then simply works without browser saving.</summary>
    public bool Start()
    {
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            LogService.Log($"Speicherdienst für Browser-Abläufe nicht verfügbar (Port {_port}): {ex.Message}");
            _listener = null;
            return false;
        }

        // On the thread pool, not on the caller's thread: the apps start the
        // service on their UI thread, and an accept loop started there would
        // carry the UI thread's SynchronizationContext into every request.
        // A request that then waits for the UI thread (the macOS app builds
        // the editor page there) would wait for itself — the whole app froze.
        var listener = _listener;
        _ = Task.Run(() => AcceptLoopAsync(listener, _stop.Token));
        LogService.Log($"Speicherdienst für Browser-Abläufe läuft auf 127.0.0.1:{_port}.");
        return true;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellation);
            }
            catch (Exception) when (cancellation.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogService.Log($"Speicherdienst: Verbindung fehlgeschlagen: {ex.Message}");
                continue;
            }

            _ = HandleClientAsync(client);
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = client.SendTimeout = 10_000;
                var stream = client.GetStream();
                var (method, target, headers, body) = await ReadRequestAsync(stream);
                var path = target.Split('?')[0];

                if (method == "GET" && path.StartsWith("/editor/", StringComparison.Ordinal))
                {
                    await HandleEditorAsync(stream, path, target);
                    return;
                }

                if (method == "POST" && path == "/control")
                {
                    var (controlStatus, result) = await HandleControlAsync(headers, body);
                    await WriteJsonAsync(stream, controlStatus, result);
                    return;
                }

                var (status, ok, message) = method == "POST" && path == "/save"
                    ? await HandleSaveAsync(body)
                    : (404, false, "Nicht gefunden.");
                await WriteResponseAsync(stream, status, ok, message);
            }
            catch (Exception ex)
            {
                LogService.Log($"Speicherdienst: Anfrage fehlgeschlagen: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// One recording command from the Obsidian plugin. Refused unless it
    /// carries the pairing token, comes as JSON (a web page cannot send that
    /// cross-origin without a preflight this service never answers) and has
    /// no foreign Origin; "start" only accepts a diagram in an Obsidian vault.
    /// </summary>
    private async Task<(int Status, RemoteStatus Result)> HandleControlAsync(IReadOnlyDictionary<string, string> headers, string body)
    {
        static RemoteStatus Fail(string message) => new(false, message, false, false, null);
        if (headers.TryGetValue("origin", out var origin) && origin != "app://obsidian.md")
        {
            return (403, Fail("Nicht erlaubt."));
        }

        if (!headers.TryGetValue("content-type", out var type) || !type.StartsWith("application/json", StringComparison.OrdinalIgnoreCase))
        {
            return (415, Fail("JSON erwartet."));
        }

        if (RemoteControl is not { } control || RemoteToken is not { Length: > 0 } expected)
        {
            return (503, Fail("Steuerung aus Obsidian ist in DocuClick nicht verfügbar."));
        }

        RemoteCommand command;
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            var token = root.GetProperty("token").GetString() ?? "";
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(token)))
            {
                return (403, Fail("Verbindung ungültig: bitte in DocuClick einmal eine Aufnahme in diesem Vault starten."));
            }

            string? Optional(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            command = new RemoteCommand(root.GetProperty("action").GetString() ?? "", Optional("file"), Optional("name"));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return (400, Fail("Ungültige Anfrage."));
        }

        if (command.Action is not ("status" or "start" or "pause" or "branch"))
        {
            return (400, Fail("Unbekannter Befehl."));
        }

        if (command.Action == "start")
        {
            var file = command.File is { Length: > 0 } given ? Path.GetFullPath(given) : "";
            if (!Path.IsPathRooted(file) || !ObsidianVault.IsDiagramFile(file) || ObsidianVault.FindRoot(Path.GetDirectoryName(file)) is null)
            {
                return (403, Fail("Nur Diagramme in einem Obsidian-Vault können so aufgenommen werden."));
            }

            command = command with { File = file };
        }

        return (200, await control(command));
    }

    /// <summary>The editor page and its screenshots (see <see cref="EditorPage"/>). Only with the token; images only below the image root.</summary>
    private async Task HandleEditorAsync(NetworkStream stream, string path, string target)
    {
        var query = target.Contains('?') ? ParseForm(target[(target.IndexOf('?') + 1)..]) : new Dictionary<string, string>();
        if (!query.TryGetValue("t", out var token) || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(EditorToken)))
        {
            await WriteBytesAsync(stream, 403, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Nicht erlaubt."));
            return;
        }

        if (path == "/editor/page")
        {
            var html = EditorPage?.Invoke()
                ?? "<!doctype html><meta charset=\"utf-8\"><body style=\"background:#0b0f19;color:#94a3b8;font:13px system-ui;padding:24px\">Noch kein Ablauf geladen.</body>";
            await WriteBytesAsync(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
            return;
        }

        if (path == "/editor/image" && EditorImageRoot?.Invoke() is { } root && query.TryGetValue("p", out var relative)
            && !Path.IsPathRooted(relative) && !relative.Split('/', '\\').Any(segment => segment == ".."))
        {
            var full = Path.GetFullPath(Path.Combine(root, relative));
            var inside = full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar, StringComparison.Ordinal);
            var isImage = Path.GetExtension(full).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif" or ".bmp";
            if (inside && isImage && File.Exists(full))
            {
                var bytes = await File.ReadAllBytesAsync(full);
                await WriteBytesAsync(stream, 200, ImageData.MimeType(bytes), bytes);
                return;
            }
        }

        await WriteBytesAsync(stream, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Nicht gefunden."));
    }

    private static async Task WriteBytesAsync(NetworkStream stream, int status, string contentType, byte[] bodyBytes)
    {
        var header = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n" +
                     $"Content-Type: {contentType}\r\n" +
                     $"Content-Length: {bodyBytes.Length}\r\n" +
                     "Cache-Control: no-store\r\n" +
                     "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(bodyBytes);
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int status, RemoteStatus result)
    {
        var bodyBytes = JsonSerializer.SerializeToUtf8Bytes(new { ok = result.Ok, message = result.Message, recording = result.Recording, paused = result.Paused, file = result.File });
        var header = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n" +
                     "Content-Type: application/json; charset=utf-8\r\n" +
                     $"Content-Length: {bodyBytes.Length}\r\n" +
                     "Cache-Control: no-store\r\n" +
                     "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(bodyBytes);
    }

    /// <summary>Validates and applies one save. Returns (HTTP status, ok, user-facing message).</summary>
    private async Task<(int Status, bool Ok, string Message)> HandleSaveAsync(string body)
    {
        var payloadJson = ParseForm(body).GetValueOrDefault("payload");
        if (string.IsNullOrEmpty(payloadJson))
        {
            return (400, false, "Leere Anfrage.");
        }

        string path, token;
        JsonElement docElement;
        try
        {
            using var payload = JsonDocument.Parse(payloadJson);
            var root = payload.RootElement;
            path = root.GetProperty("path").GetString() ?? "";
            token = root.GetProperty("token").GetString() ?? "";
            docElement = root.GetProperty("doc").Clone();
        }
        catch (Exception)
        {
            return (400, false, "Ungültige Anfrage.");
        }

        path = NormalizeFilePath(path);
        if (!Path.IsPathRooted(path) || !path.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || !CanvasDocumentIo.IsAblaufFile(path))
        {
            return (403, false, "Keine DocuClick-Ablaufdatei.");
        }

        var existing = CanvasDocumentIo.Load(path);
        if (existing.SaveToken is not { Length: > 0 } expected
            || !CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(token)))
        {
            return (403, false, "Diese Datei wurde noch nicht mit DocuClick gespeichert (einmal in DocuClick öffnen) oder der Schlüssel passt nicht.");
        }

        var document = CanvasDocumentIo.Parse(docElement.GetRawText(), path);
        document.SaveToken = expected;
        await _apply(path, document);
        LogService.Log($"Ablauf aus dem Browser gespeichert: {path}");
        return (200, true, "Gespeichert.");
    }

    /// <summary>
    /// location.pathname of a file:// page, decoded: "/Users/…" on macOS,
    /// "/C:/Users/…" on Windows (leading slash dropped there).
    /// </summary>
    internal static string NormalizeFilePath(string path)
    {
        if (path.Length >= 4 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':' && path[3] == '/')
        {
            path = path[1..];
        }

        return Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
    }

    private static Dictionary<string, string> ParseForm(string body) => body
        .Split('&', StringSplitOptions.RemoveEmptyEntries)
        .Select(pair => pair.Split('=', 2))
        .GroupBy(parts => WebUtility.UrlDecode(parts[0]))
        .ToDictionary(g => g.Key, g => WebUtility.UrlDecode(g.First().ElementAtOrDefault(1) ?? ""));

    private static async Task<(string Method, string Path, Dictionary<string, string> Headers, string Body)> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int headerEnd;
        while ((headerEnd = IndexOfHeaderEnd(buffer)) < 0)
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0 || buffer.Length > 64 * 1024)
            {
                throw new InvalidOperationException("Unvollständige Anfrage.");
            }

            buffer.Write(chunk, 0, read);
        }

        var all = buffer.ToArray();
        var headerText = Encoding.ASCII.GetString(all, 0, headerEnd);
        var lines = headerText.Split("\r\n");
        var requestLine = lines[0].Split(' ');
        var headers = lines.Skip(1)
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2)
            .GroupBy(p => p[0].Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => g.First()[1].Trim());
        var contentLength = headers.TryGetValue("content-length", out var lengthText) && int.TryParse(lengthText, out var n) ? n : 0;
        if (contentLength is < 0 or > MaxRequestBytes)
        {
            throw new InvalidOperationException("Anfrage zu groß.");
        }

        var body = new MemoryStream();
        body.Write(all, headerEnd + 4, all.Length - headerEnd - 4);
        while (body.Length < contentLength)
        {
            var read = await stream.ReadAsync(chunk);
            if (read == 0)
            {
                break;
            }

            body.Write(chunk, 0, read);
        }

        return (requestLine[0], requestLine.Length > 1 ? requestLine[1] : "/", headers, Encoding.UTF8.GetString(body.ToArray()));
    }

    private static int IndexOfHeaderEnd(MemoryStream buffer)
    {
        var bytes = buffer.GetBuffer();
        for (var i = 0; i + 3 < buffer.Length; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A tiny page for the form's hidden iframe that reports the result to the Ablauf page.</summary>
    private static async Task WriteResponseAsync(NetworkStream stream, int status, bool ok, string message)
    {
        var result = JsonSerializer.Serialize(new { docuclick = "save-result", ok, message });
        var html = $"<!doctype html><meta charset=\"utf-8\"><script>parent.postMessage({result}, \"*\");</script>";
        var bodyBytes = Encoding.UTF8.GetBytes(html);
        var header = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\n" +
                     "Content-Type: text/html; charset=utf-8\r\n" +
                     $"Content-Length: {bodyBytes.Length}\r\n" +
                     "Cache-Control: no-store\r\n" +
                     "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header));
        await stream.WriteAsync(bodyBytes);
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener?.Stop();
    }
}
