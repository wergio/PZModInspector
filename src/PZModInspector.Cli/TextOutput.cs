using PZModInspector.Core.Localization;

namespace PZModInspector.Cli;

/// <summary>Plain text for a terminal or for Claude: titles, aligned label/value rows, tables with columns cut to a width.</summary>
internal sealed class TextOutput(TextWriter writer)
{
    public void Line(string text = "") => writer.WriteLine(text);

    public void Title(string text)
    {
        writer.WriteLine();
        writer.WriteLine(text);
    }

    public void Rows(IEnumerable<InfoRow> rows, string indent = "  ")
    {
        var list = rows.ToList();
        var width = list.Count == 0 ? 0 : list.Max(r => r.Label.Length);
        foreach (var row in list)
            writer.WriteLine($"{indent}{row.Label.PadRight(width)}  {row.Value}");
    }

    /// <param name="maxWidths">Per column, 0 = no limit; longer values end with "…".</param>
    public void Table(IReadOnlyList<string> headers, IEnumerable<string[]> rows, IReadOnlyList<int> maxWidths, string indent = "")
    {
        var cells = rows.Select(r => r.Select((c, i) => Cut(c, maxWidths[i])).ToArray()).ToList();
        var widths = headers.Select((h, i) => Math.Max(h.Length, cells.Count == 0 ? 0 : cells.Max(r => r[i].Length))).ToArray();
        writer.WriteLine(indent + Join(headers.ToArray(), widths));
        foreach (var row in cells)
            writer.WriteLine(indent + Join(row, widths));
    }

    public static string Cut(string text, int max) => max <= 0 || text.Length <= max ? text : text[..(max - 1)] + "…";

    private static string Join(string[] cells, int[] widths) =>
        string.Join("  ", cells.Select((c, i) => i == cells.Length - 1 ? c : c.PadRight(widths[i]))).TrimEnd();
}
