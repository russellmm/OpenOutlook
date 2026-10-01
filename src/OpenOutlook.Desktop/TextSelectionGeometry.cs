namespace OpenOutlook.Desktop;

/// <summary>
/// Chooses which text blocks a drag selected, given rectangles that Chrome measured.
///
/// The snapshot reader is a bitmap, so selection has to be computed here rather than by the toolkit:
/// Avalonia will not hit-test the invisible text placed over the picture, and the embedded web view
/// cannot composite into an Avalonia window under Wayland. Keeping this class free of controls means
/// the ordering and containment rules -- the parts that decide what text lands on the clipboard --
/// are unit testable without a display.
/// </summary>
public static class TextSelectionGeometry
{
    /// <summary>A drag's start and end rectangles, in page coordinates.</summary>
    public sealed record Drag(Avalonia.Rect From, Avalonia.Rect To);

    /// <summary>The blocks a drag covers, in reading order, and the text they produce.</summary>
    public sealed record Selection(IReadOnlyList<int> BlockIndexes, string Text)
    {
        public static readonly Selection Empty = new([], "");
    }

    /// <summary>
    /// The block under a point, or -1. Wholly contains the point; when blocks overlap (a wrapper and
    /// its child both carry text), the smallest wins so clicking a bold word inside a paragraph picks
    /// the word rather than the whole paragraph.
    /// </summary>
    public static int BlockAt(IReadOnlyList<Avalonia.Rect> blocks, double x, double y)
    {
        var best = -1;
        var bestArea = double.PositiveInfinity;
        for (var i = 0; i < blocks.Count; i++)
        {
            var rect = blocks[i];
            if (x < rect.X || y < rect.Y || x > rect.Right || y > rect.Bottom) continue;
            var area = rect.Width * rect.Height;
            if (area < bestArea)
            {
                best = i;
                bestArea = area;
            }
        }
        return best;
    }

    /// <summary>
    /// Every block the drag touched, ordered as a person reads: top to bottom, then left to right.
    /// A block counts when it intersects the swept rectangle; reading order is taken from the blocks'
    /// own coordinates rather than their document index because table cells can appear in any order.
    /// </summary>
    public static Selection Select(IReadOnlyList<Avalonia.Rect> blocks, IReadOnlyList<string> texts, Drag drag)
    {
        if (blocks.Count == 0 || blocks.Count != texts.Count) return Selection.Empty;

        var swept = Sweep(drag.From, drag.To);
        if (swept.Width <= 0 && swept.Height <= 0) return Selection.Empty;

        var chosen = new List<int>();
        for (var i = 0; i < blocks.Count; i++)
        {
            if (!blocks[i].Intersects(swept)) continue;
            // A block that only the pointer's hairline grazed should not be dragged along when the
            // drag is essentially a click: require real overlap for near-point drags.
            if (swept.Width < 2 && swept.Height < 2 && blocks[i].Width * blocks[i].Height > 400) continue;
            chosen.Add(i);
        }
        if (chosen.Count == 0) return Selection.Empty;

        var ordered = chosen
            .OrderBy(i => Math.Round(blocks[i].Y, 1))
            .ThenBy(i => Math.Round(blocks[i].X, 1))
            .ToList();
        var text = string.Join(" ", ordered.Select(i => texts[i].Trim()).Where(t => t.Length > 0));
        return new Selection(ordered, text);
    }

    /// <summary>All blocks in reading order -- what Ctrl+A means inside the reading pane.</summary>
    public static Selection SelectAll(IReadOnlyList<Avalonia.Rect> blocks, IReadOnlyList<string> texts)
    {
        if (blocks.Count == 0 || blocks.Count != texts.Count) return Selection.Empty;
        var ordered = Enumerable.Range(0, blocks.Count)
            .OrderBy(i => Math.Round(blocks[i].Y, 1))
            .ThenBy(i => Math.Round(blocks[i].X, 1))
            .ToList();
        return new Selection(ordered, string.Join(" ", ordered.Select(i => texts[i].Trim())));
    }

    /// <summary>The axis-aligned region swept between two rectangles.</summary>
    public static Avalonia.Rect Sweep(Avalonia.Rect a, Avalonia.Rect b)
    {
        var x = Math.Min(a.X, b.X);
        var y = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.Right, b.Right);
        var bottom = Math.Max(a.Bottom, b.Bottom);
        return new Avalonia.Rect(x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }
}
