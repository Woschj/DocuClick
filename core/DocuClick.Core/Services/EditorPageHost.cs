using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DocuClick.Services;

/// <summary>
/// The app's Ablauf-Übersicht built on the shared editor template
/// (WebAssets/viewer.template.html) — the same editor as the .html file in a
/// browser and the Obsidian plugin, instead of the separate flow.js page.
/// The page edits its own copy of the document and saves it whole (as in the
/// browser); this class hands those saves to the session, sends newly
/// recorded clicks to the page, and adds the recording actions ("Hier weiter
/// aufnehmen", "Neuer Pfad ab hier") through the template's recording host.
///
/// Messages page → app (JSON): {kind:"ready"}, {kind:"save", request, canvas},
/// {kind:"jumpTo", nodeId}, {kind:"newPath", nodeId}.
/// App → page: {kind:"saved", request, error?}, {kind:"replace", canvas, flow,
/// currentId}, {kind:"setCurrent", nodeId}.
/// </summary>
public sealed class EditorPageHost
{
    private readonly SessionManager _session;
    private readonly IFlowEditorHost _host;
    private readonly Func<string, string, string?> _imageUrl;

    // The stored document the page currently shows. A save based on anything
    // else (a click was recorded meanwhile) is refused, not applied over it.
    private string? _pageCanvas;

    /// <param name="imageUrl">
    /// (session folder, file node value) → what the page loads for that
    /// screenshot: a URL the web view can reach (Windows: a virtual host
    /// mapped to the folder) or a data URI. Data URIs stored in the document
    /// pass through unchanged.
    /// </param>
    public EditorPageHost(SessionManager session, IFlowEditorHost host, Func<string, string, string?> imageUrl)
    {
        _session = session;
        _host = host;
        _imageUrl = imageUrl;
    }

    private EditorDocument? Current()
    {
        string? folder = null;
        return _session.GetEditorDocument(file =>
        {
            if (file.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                return file;
            }

            folder ??= _session.CurrentSessionFolder ?? "";
            return _imageUrl(folder, file);
        });
    }

    /// <summary>The page for the loaded flow, with the bridge to this host; null without a loaded file.</summary>
    public string? BuildPage()
    {
        if (Current() is not { } doc)
        {
            return null;
        }

        _pageCanvas = doc.CanvasJson;
        var bridge = $"<script>{BridgeScript}</script>";
        return doc.BuildPage().Replace("<head>", "<head>" + bridge, StringComparison.Ordinal);
    }

    /// <summary>The session changed (click recorded, jump, new path, file switch): bring the page up to date.</summary>
    public void OnSessionChanged()
    {
        if (Current() is not { } doc)
        {
            return;
        }

        if (doc.CanvasJson == _pageCanvas)
        {
            // Only the recording position moved (or it's the page's own save).
            _host.PostToWeb(JsonSerializer.Serialize(new { kind = "setCurrent", nodeId = doc.CurrentNodeId }));
            return;
        }

        _pageCanvas = doc.CanvasJson;
        _host.PostToWeb(ReplaceMessage(doc));
    }

    private static string ReplaceMessage(EditorDocument doc) => new JsonObject
    {
        ["kind"] = "replace",
        ["canvas"] = JsonNode.Parse(doc.CanvasJson),
        ["flow"] = JsonNode.Parse(doc.FlowJson),
        ["currentId"] = doc.CurrentNodeId,
    }.ToJsonString();

    /// <summary>Handles one message from the page. Run on the host's UI thread (it may open the name prompt).</summary>
    public async Task HandleMessageAsync(string json)
    {
        using var message = JsonDocument.Parse(json);
        var root = message.RootElement;
        switch (root.GetProperty("kind").GetString())
        {
            case "ready":
                // (Re)loaded page: make sure it shows the current state.
                if (Current() is { } doc)
                {
                    _pageCanvas = doc.CanvasJson;
                    _host.PostToWeb(ReplaceMessage(doc));
                }

                break;

            case "save":
                Save(root.GetProperty("request").GetInt32(), root.GetProperty("canvas"));
                break;

            case "jumpTo":
                _session.JumpToNode(root.GetProperty("nodeId").GetString()!);
                break;

            case "newPath":
            {
                var nodeId = root.GetProperty("nodeId").GetString()!;
                if (await _host.PromptTextAsync() is { Length: > 0 } name)
                {
                    _session.StartNewPath(nodeId, name);
                }

                break;
            }
        }
    }

    private void Save(int request, JsonElement canvas)
    {
        string? error = null;
        try
        {
            if (Current() is not { } doc)
            {
                throw new InvalidOperationException("Kein Ablauf geladen.");
            }

            if (doc.CanvasJson != _pageCanvas)
            {
                // A click was recorded after the page's last update: applying
                // this save would drop it. Show the newer state instead.
                _pageCanvas = doc.CanvasJson;
                _host.PostToWeb(ReplaceMessage(doc));
                throw new InvalidOperationException("Der Ablauf wurde inzwischen weiter aufgenommen. Die Ansicht zeigt jetzt den neuen Stand; die letzte Änderung bitte wiederholen.");
            }

            var document = CanvasDocumentIo.Parse(canvas.GetRawText(), doc.FilePath);
            // As the writer will store it (it keeps the file's save token), so
            // the preview update this save causes is recognised as the page's own.
            document.SaveToken = JsonSerializer.Deserialize<CanvasDocument>(doc.CanvasJson)?.SaveToken;
            _pageCanvas = JsonSerializer.Serialize(document);
            _session.ApplyExternalEdit(doc.FilePath, document).GetAwaiter().GetResult();
            _pageCanvas = Current()?.CanvasJson;
        }
        catch (Exception ex) when (ex is InvalidOperationException or JsonException or IOException)
        {
            error = ex.Message;
            LogService.Log($"Ablauf-Übersicht: Speichern fehlgeschlagen: {ex.Message}");
        }

        _host.PostToWeb(JsonSerializer.Serialize(new { kind = "saved", request, error }));
    }

    /// <summary>
    /// Runs in the page before the template: provides window.docuclickEditorHost
    /// over the web view's message channel (WebView2 on Windows, Avalonia's
    /// invokeCSharpAction / docuclickHostMessage on macOS).
    /// </summary>
    internal const string BridgeScript = """
(() => {
  const webview = window.chrome && window.chrome.webview;
  const toHost = (message) => {
    if (webview) webview.postMessage(message);
    else if (typeof window.invokeCSharpAction === "function") window.invokeCSharpAction(JSON.stringify(message));
  };
  const pending = new Map();
  let serial = 0, editor = null;
  const onMessage = (message) => {
    if (!message || typeof message !== "object") return;
    if (message.kind === "saved") {
      const entry = pending.get(message.request);
      if (!entry) return;
      pending.delete(message.request);
      message.error ? entry.reject(new Error(message.error)) : entry.resolve();
    } else if (message.kind === "replace" && editor) {
      editor.replace({ canvas: message.canvas, flow: message.flow });
      editor.setCurrent(message.currentId);
    } else if (message.kind === "setCurrent" && editor) {
      editor.setCurrent(message.nodeId);
    }
  };
  if (webview) webview.addEventListener("message", (e) => onMessage(e.data));
  else window.docuclickHostMessage = onMessage;
  window.docuclickEditorHost = {
    changed: () => {},
    save: (snapshot) => new Promise((resolve, reject) => {
      const request = ++serial;
      pending.set(request, { resolve, reject });
      toHost({ kind: "save", request, canvas: snapshot.canvas });
    }),
    ready: (api) => { editor = api; toHost({ kind: "ready" }); },
    recording: {
      jumpTo: (nodeId) => toHost({ kind: "jumpTo", nodeId }),
      newPath: (nodeId) => toHost({ kind: "newPath", nodeId }),
    },
  };
})();
""";
}
