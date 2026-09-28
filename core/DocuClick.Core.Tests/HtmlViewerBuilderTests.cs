using System.Text.Json;
using DocuClick.Services;

namespace DocuClick.Core.Tests;

public sealed class HtmlViewerBuilderTests
{
    [Fact]
    public void Shared_template_preserves_literal_placeholders_and_escapes_user_content()
    {
        const string label = "</script><script>alert(1)</script> @@FLOW@@";
        var nodes = new[] { new HtmlViewerBuilder.NodeSpec("a", label, 0, 0, "#123456", "rectangle", null) };
        var document = new CanvasDocument();
        document.Nodes.Add(new CanvasNode { Id = "a", Text = label });

        var html = HtmlViewerBuilder.BuildPage("Title @@FLOW@@ <unsafe>", nodes,
            Array.Empty<HtmlViewerBuilder.EdgeSpec>(), JsonSerializer.Serialize(document));

        Assert.Contains("<title>Title @@FLOW@@ &lt;unsafe&gt; – Ablauf</title>", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
        Assert.Contains("docuclick-data", html);
        Assert.Contains("function restoreHistory", html);
        Assert.DoesNotContain("@@CYTOSCAPE@@", html);
        Assert.DoesNotContain("@@DOCUMENT@@", html);
    }
}
