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

        _ = AcceptLoopAsync(_listener, _stop.Token);
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
                var (method, path, body) = await ReadRequestAsync(stream);

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

    private static async Task<(string Method, string Path, string Body)> ReadRequestAsync(NetworkStream stream)
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
        var contentLength = lines.Skip(1)
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2 && p[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(p => int.TryParse(p[1].Trim(), out var n) ? n : 0)
            .FirstOrDefault();
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

        return (requestLine[0], requestLine.Length > 1 ? requestLine[1].Split('?')[0] : "/", Encoding.UTF8.GetString(body.ToArray()));
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
