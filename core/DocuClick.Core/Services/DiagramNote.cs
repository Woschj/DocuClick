using System.Text;
using System.Text.RegularExpressions;

namespace DocuClick.Services;

/// <summary>
/// A flow stored as one Markdown note in an Obsidian vault — the plugin's
/// "Diagramm-Notiz" (<c>obsidian/src/document.js</c>, same markers and step
/// list): own text of the note, a generated step list (searchable in
/// Obsidian) and the diagram JSON in a hidden <c>%%</c> comment. The plugin
/// opens such a note as a diagram tab.
/// <code>
/// ---
/// docuclick: diagramm
/// ---
/// ## Schritte
/// %% DocuClick: Schritte … %%
/// 1. Linksklick auf …
/// %% DocuClick: Ende der Schritte %%
///
/// %% DocuClick-Diagrammdaten (nicht von Hand ändern)
/// { "format": "docuclick-diagram", … }
/// %%
/// </code>
/// </summary>
public static class DiagramNote
{
    public const string StepsStart = "%% DocuClick: Schritte werden aus dem Diagramm erzeugt, Änderungen hier gehen verloren. %%";
    public const string StepsEnd = "%% DocuClick: Ende der Schritte %%";
    public const string DataStart = "%% DocuClick-Diagrammdaten (nicht von Hand ändern)";
    private const string DataEnd = "\n%%";

    /// <summary>The diagram JSON inside a note, or null if it has none.</summary>
    public static string? ExtractData(string text)
    {
        var start = text.IndexOf(DataStart, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var end = text.IndexOf(DataEnd, start + DataStart.Length, StringComparison.Ordinal);
        return end < 0 ? null : text[(start + DataStart.Length)..end].Trim();
    }

    /// <summary>
    /// The note with this diagram: a new note, or <paramref name="previous"/>
    /// with only the step list and the data block replaced — the note's own
    /// text and properties stay. <paramref name="dataJson"/> must not contain
    /// "%" (see <see cref="EscapeForNote"/>), so it can never close the comment.
    /// </summary>
    public static string Compose(string? previous, string dataJson, string steps)
    {
        var data = $"{DataStart}\n{dataJson}\n%%";
        if (string.IsNullOrWhiteSpace(previous))
        {
            return $"---\ndocuclick: diagramm\n---\n\n## Schritte\n\n{StepsStart}\n{steps}\n{StepsEnd}\n\n{data}\n";
        }

        var text = previous;
        var stepsStart = text.IndexOf(StepsStart, StringComparison.Ordinal);
        var stepsEnd = stepsStart < 0 ? -1 : text.IndexOf(StepsEnd, stepsStart + 1, StringComparison.Ordinal);
        if (stepsEnd >= 0)
        {
            text = $"{text[..stepsStart]}{StepsStart}\n{steps}\n{text[stepsEnd..]}";
        }

        var dataStart = text.IndexOf(DataStart, StringComparison.Ordinal);
        var dataEnd = dataStart < 0 ? -1 : text.IndexOf(DataEnd, dataStart + DataStart.Length, StringComparison.Ordinal);
        return dataEnd < 0
            ? $"{text.TrimEnd()}\n\n{data}\n"
            : $"{text[..dataStart]}{data}{text[(dataEnd + DataEnd.Length)..]}";
    }

    /// <summary>"%" only occurs inside JSON strings; as % it stays the same text but can't end the comment.</summary>
    public static string EscapeForNote(string json) => json.Replace("%", "\\u0025", StringComparison.Ordinal);

    private const string DecisionPoint = "◆ Abzweigung";
    private const string PathStart = "↳ Pfad: ";
    private static readonly Regex Whitespace = new(@"\s+");
    private static readonly Regex MarkdownSpecial = new(@"[\\`*_\[\]#<>|~=$^%]");

    /// <summary>Step text safe for a Markdown list item: one line, no tags, links, formatting or comments.</summary>
    private static string MarkdownText(string? value)
    {
        var text = Whitespace.Replace(value ?? "", " ").Trim();
        text = MarkdownSpecial.Replace(text, m => "\\" + m.Value);
        return text.Length > 0 ? text : "(ohne Beschreibung)";
    }

    /// <summary>
    /// The flow as nested Markdown lists — main line numbered, each path of a
    /// decision point as its own sub-list; manual cross-connections left out.
    /// Same output as stepsMarkdown in the plugin (obsidian/src/document.js) —
    /// change both together; obsidian/tests/document.test.js compares them on
    /// the shared fixture (regenerate it with DOCUCLICK_WRITE_FIXTURES=1).
    /// </summary>
    public static string Steps(CanvasDocument doc)
    {
        var texts = doc.Nodes.Where(n => n.Type == "text").GroupBy(n => n.Id).ToDictionary(g => g.Key, g => g.First());
        var children = new Dictionary<string, List<string>>();
        var hasParent = new HashSet<string>();
        foreach (var edge in doc.Edges.Where(e => !e.Manual && texts.ContainsKey(e.FromNode) && texts.ContainsKey(e.ToNode)))
        {
            if (!children.TryGetValue(edge.FromNode, out var list))
            {
                children[edge.FromNode] = list = new List<string>();
            }

            list.Add(edge.ToNode);
            hasParent.Add(edge.ToNode);
        }

        foreach (var list in children.Values)
        {
            // Stable, like Array.prototype.sort in the plugin.
            var sorted = list.OrderBy(id => texts[id].X).ThenBy(id => texts[id].Y).ToList();
            list.Clear();
            list.AddRange(sorted);
        }

        var lines = new List<string>();
        var seen = new HashSet<string>();
        List<string> Next(string id) => children.TryGetValue(id, out var list) ? list : new List<string>();

        void ContinueWith(List<string> next, int depth)
        {
            if (next.Count == 1)
            {
                Chain(next[0], depth);
                return;
            }

            foreach (var branch in next)
            {
                if ((texts[branch].Text ?? "").StartsWith(PathStart, StringComparison.Ordinal))
                {
                    Chain(branch, depth);
                }
                else if (!seen.Contains(branch))
                {
                    lines.Add($"{new string('\t', depth)}- **Weiter mit:**");
                    Chain(branch, depth + 1);
                }
            }
        }

        void Chain(string start, int depth)
        {
            var number = 1;
            string? id = start;
            while (id is not null && seen.Add(id))
            {
                var text = texts[id].Text ?? "";
                var next = Next(id);
                var indent = new string('\t', depth);
                if (text.StartsWith(PathStart, StringComparison.Ordinal))
                {
                    lines.Add($"{indent}- **Pfad: {MarkdownText(text[PathStart.Length..])}**");
                    ContinueWith(next, depth + 1);
                    return;
                }

                if (text == DecisionPoint || next.Count > 1)
                {
                    lines.Add($"{indent}{number++}. {(text == DecisionPoint ? "Abzweigung:" : $"{MarkdownText(text)}, dann je nach Fall:")}");
                    ContinueWith(next, depth + 1);
                    return;
                }

                lines.Add($"{indent}{number++}. {MarkdownText(text)}");
                id = next.Count > 0 ? next[0] : null;
            }
        }

        var roots = texts.Keys.Where(id => !hasParent.Contains(id))
            .OrderBy(id => texts[id].Y).ThenBy(id => texts[id].X).ToList();
        for (var i = 0; i < roots.Count; i++)
        {
            if (i > 0)
            {
                lines.Add("");
            }

            Chain(roots[i], 0);
        }

        // Anything only reachable through a cycle.
        foreach (var id in texts.Keys.Where(id => !seen.Contains(id)).ToList())
        {
            if (seen.Contains(id))
            {
                continue;
            }

            lines.Add("");
            Chain(id, 0);
        }

        return lines.Count > 0 ? string.Join("\n", lines) : "_Noch keine Schritte._";
    }
}
