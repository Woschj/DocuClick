using System.Text.Json;
using DocuClick.Services;

namespace DocuClick.Core.Tests;

/// <summary>The shared Ablauf-Übersicht message protocol (C# half of flow.js).</summary>
public sealed class FlowEditorBridgeTests
{
    private sealed class FakeHost : IFlowEditorHost
    {
        public Queue<string?> PromptAnswers { get; } = new();
        public bool ConfirmAnswer { get; set; }
        public List<string> Confirmations { get; } = new();
        public List<string> Posted { get; } = new();

        public Task<string?> PromptTextAsync(string? title = null, string? label = null, string? initialValue = null) =>
            Task.FromResult(PromptAnswers.Count > 0 ? PromptAnswers.Dequeue() : null);

        public Task<string?> PickImageFileAsync() => Task.FromResult<string?>(null);

        public Task<bool> ConfirmAsync(string message)
        {
            Confirmations.Add(message);
            return Task.FromResult(ConfirmAnswer);
        }

        public void PostToWeb(string json) => Posted.Add(json);
    }

    private static readonly FlowPreview Fork = new(
        new List<PreviewNode>
        {
            new("a", "Start", 0, 0, 380, 340, IsCurrent: false, IsDecisionPoint: false, IsPathStart: false, ImagePath: "Attachments/x.png"),
            new("d", "Abzweigung", 0, 400, 380, 60, IsCurrent: false, IsDecisionPoint: true, IsPathStart: false),
            new("p1", "Erfolg", 400, 400, 380, 60, IsCurrent: false, IsDecisionPoint: false, IsPathStart: true, PathName: "Erfolg"),
            new("p2", "Fehler", 800, 400, 380, 60, IsCurrent: true, IsDecisionPoint: false, IsPathStart: true, PathName: "Fehler")
        },
        new List<PreviewEdge> { new("a", "d"), new("d", "p1"), new("d", "p2") });

    private static string Message(object payload) => JsonSerializer.Serialize(payload);

    [Fact]
    public async Task Inline_rename_needs_no_prompt()
    {
        var host = new FakeHost();
        var bridge = new FlowEditorBridge(host);
        (string, string)? renamed = null;
        bridge.RenameRequested += (id, label) => renamed = (id, label);

        await bridge.HandleMessageAsync(Message(new { type = "rename", nodeId = "a", newLabel = "Anmelden" }));

        Assert.Equal(("a", "Anmelden"), renamed);
    }

    [Fact]
    public async Task New_path_asks_for_a_name_and_cancel_does_nothing()
    {
        var host = new FakeHost();
        var bridge = new FlowEditorBridge(host);
        var requests = new List<(string, string)>();
        bridge.NewPathRequested += (id, name) => requests.Add((id, name));

        host.PromptAnswers.Enqueue(null);
        await bridge.HandleMessageAsync(Message(new { type = "newPath", nodeId = "d" }));
        host.PromptAnswers.Enqueue("Abbruch");
        await bridge.HandleMessageAsync(Message(new { type = "newPath", nodeId = "d" }));

        Assert.Equal(new[] { ("d", "Abbruch") }, requests);
    }

    [Fact]
    public async Task Deleting_a_fork_asks_first_and_names_the_subtree_size()
    {
        var host = new FakeHost { ConfirmAnswer = false };
        var bridge = new FlowEditorBridge(host);
        bridge.BuildPreviewMessage(Fork);
        var deleted = new List<string>();
        bridge.DeleteRequested += deleted.Add;

        await bridge.HandleMessageAsync(Message(new { type = "delete", nodeId = "d" }));
        Assert.Empty(deleted);
        Assert.Contains("alle 2 nachfolgenden Knoten", Assert.Single(host.Confirmations));

        await bridge.HandleMessageAsync(Message(new { type = "delete", nodeId = "p1" })); // leaf: no question
        Assert.Equal(new[] { "p1" }, deleted);
    }

    [Fact]
    public async Task Path_requests_are_answered_to_the_page()
    {
        var host = new FakeHost();
        var bridge = new FlowEditorBridge(host)
        {
            PathsProvider = _ => new List<PathInfo> { new("p1", "Erfolg", 3) }
        };

        await bridge.HandleMessageAsync(Message(new { type = "requestPaths", nodeId = "d" }));

        using var reply = JsonDocument.Parse(Assert.Single(host.Posted));
        Assert.Equal("pathsResult", reply.RootElement.GetProperty("type").GetString());
        Assert.Equal("Erfolg", reply.RootElement.GetProperty("paths")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Preview_message_marks_the_current_node_and_path_labels()
    {
        var bridge = new FlowEditorBridge(new FakeHost());

        using var message = JsonDocument.Parse(bridge.BuildPreviewMessage(Fork, isRecordedClick: true));
        var root = message.RootElement;
        Assert.Equal("preview", root.GetProperty("type").GetString());
        Assert.True(root.GetProperty("isRecordedClick").GetBoolean());

        var nodes = root.GetProperty("nodes").EnumerateArray().ToDictionary(n => n.GetProperty("id").GetString()!);
        Assert.Equal("#E63946", nodes["p2"].GetProperty("color").GetString());
        Assert.Equal("◆ Abzweigung", nodes["d"].GetProperty("permLabel").GetString());
        Assert.Equal("↳ Erfolg", nodes["p1"].GetProperty("permLabel").GetString());
        Assert.True(nodes["d"].GetProperty("hasChildren").GetBoolean());
        Assert.Equal(3, root.GetProperty("edges").GetArrayLength());
    }
}
