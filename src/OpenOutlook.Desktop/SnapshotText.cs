namespace OpenOutlook.Desktop;

/// <summary>
/// The text of a snapshot-rendered message and where every character sits, built from the runs Chrome measured
/// (<see cref="BrowserHtmlRenderer.TextRun"/>). The snapshot reader is a bitmap, so selection has to be computed here rather than by
/// a toolkit: Avalonia does not hit-test invisible text over a picture, and the embedded web view cannot composite into an Avalonia
/// window under Wayland. Keeping this free of controls means the parts that decide what text lands on the clipboard -- caret
/// placement, word and paragraph boundaries, line breaks and tabs -- are unit testable without a display.
///
/// Positions are offsets into <see cref="Text"/>: the runs in document order with a separator between neighbours ("\n" between
/// blocks or after a line break, "\t" between table cells on one line, a space where collapsed whitespace stood). A selection is a
/// pair of offsets, exactly like a browser's selection is a pair of DOM positions, so dragging across columns follows document order.
/// </summary>
public sealed class SnapshotText
{
    public static readonly SnapshotText Empty = new([]);

    private readonly BrowserHtmlRenderer.TextRun[] _runs;
    private readonly int[] _offsets;
    private readonly double[] _left;
    private readonly double[] _right;

    public SnapshotText(IReadOnlyList<BrowserHtmlRenderer.TextRun> runs)
    {
        _runs = runs.ToArray();
        _offsets = new int[_runs.Length];
        _left = new double[_runs.Length];
        _right = new double[_runs.Length];
        var text = new System.Text.StringBuilder();
        for (var i = 0; i < _runs.Length; i++)
        {
            if (i > 0) text.Append(Separator(_runs[i - 1], _runs[i]));
            _offsets[i] = text.Length;
            text.Append(_runs[i].Text);
            _left[i] = _runs[i].X + _runs[i].Edges.Min();
            _right[i] = _runs[i].X + _runs[i].Edges.Max();
        }
        Text = text.ToString();
    }

    /// <summary>The message text in document order, as a copy of everything would produce it.</summary>
    public string Text { get; }

    public IReadOnlyList<BrowserHtmlRenderer.TextRun> Runs => _runs;

    public bool IsEmpty => _runs.Length == 0;

    /// <summary>What separates two neighbouring runs in the copied text.</summary>
    internal static string Separator(BrowserHtmlRenderer.TextRun a, BrowserHtmlRenderer.TextRun b)
    {
        var endsInSpace = a.Text.Length > 0 && char.IsWhiteSpace(a.Text[^1]);
        if (a.Block != b.Block)
        {
            if (!SameLine(a, b)) return "\n";
            if (a.Cell && b.Cell) return "\t";
            return endsInSpace || b.Text.Length > 0 && char.IsWhiteSpace(b.Text[0]) ? "" : " ";
        }
        if (b.HardBreak) return "\n";
        if (b.SpaceBefore && !endsInSpace) return " ";
        // A line that wrapped without a space (a long link, a hyphen) continues the same word; a wrap at a collapsed space was
        // reported as SpaceBefore above.
        return "";
    }

    private static bool SameLine(BrowserHtmlRenderer.TextRun a, BrowserHtmlRenderer.TextRun b)
    {
        var overlap = Math.Min(a.Y + a.Height, b.Y + b.Height) - Math.Max(a.Y, b.Y);
        return overlap > 0.5 * Math.Min(a.Height, b.Height);
    }

    /// <summary>
    /// The caret offset nearest a point in page coordinates, or -1 when there is no text. A point inside a line picks the
    /// character boundary nearest it; beside a line, the nearest end of the nearest run on that line; between lines, the nearest
    /// line -- the same choices a browser makes when a drag wanders into margins and gaps.
    /// </summary>
    public int CaretAt(double x, double y)
    {
        if (_runs.Length == 0) return -1;
        var best = -1;
        var bestScore = double.PositiveInfinity;
        for (var i = 0; i < _runs.Length; i++)
        {
            var run = _runs[i];
            var dy = y < run.Y ? run.Y - y : y > run.Y + run.Height ? y - (run.Y + run.Height) : 0;
            var dx = x < _left[i] ? _left[i] - x : x > _right[i] ? x - _right[i] : 0;
            // Vertical distance dominates: a run on the pointer's own line always beats one above or below it.
            var score = dy * 100_000 + dx;
            if (score < bestScore || score == bestScore && best >= 0 && run.Height < _runs[best].Height)
            {
                best = i;
                bestScore = score;
            }
        }
        var chosen = _runs[best];
        var nearest = 0;
        var nearestDistance = double.PositiveInfinity;
        for (var j = 0; j < chosen.Edges.Count; j++)
        {
            var distance = Math.Abs(chosen.X + chosen.Edges[j] - x);
            if (distance < nearestDistance)
            {
                nearest = j;
                nearestDistance = distance;
            }
        }
        return Snap(_offsets[best] + nearest);
    }

    /// <summary>True when the point is over a laid-out line of text (where the pointer should become an I-beam).</summary>
    public bool IsOverText(double x, double y)
    {
        for (var i = 0; i < _runs.Length; i++)
        {
            var run = _runs[i];
            if (y >= run.Y && y <= run.Y + run.Height && x >= _left[i] - 2 && x <= _right[i] + 2) return true;
        }
        return false;
    }

    /// <summary>The word (letters and digits), run of spaces, or single punctuation mark at an offset: what a double-click selects.</summary>
    public (int Start, int End) WordAt(int offset)
    {
        if (Text.Length == 0) return (0, 0);
        offset = Math.Clamp(offset, 0, Text.Length);
        var probe = offset == Text.Length || offset > 0 && Kind(Text[offset]) == 0 && Kind(Text[offset - 1]) == 1 ? offset - 1 : offset;
        var kind = Kind(Text[probe]);
        if (kind == 2) return (probe, Snap(probe + 1 + (char.IsHighSurrogate(Text[probe]) ? 1 : 0)));
        var start = probe;
        while (start > 0 && Kind(Text[start - 1]) == kind && Text[start - 1] != '\n') start--;
        var end = probe + 1;
        while (end < Text.Length && Kind(Text[end]) == kind && Text[end] != '\n') end++;
        return (start, end);
    }

    /// <summary>The paragraph (block element: paragraph, list item, table cell) holding an offset: what a triple-click selects.</summary>
    public (int Start, int End) BlockAt(int offset)
    {
        if (_runs.Length == 0) return (0, 0);
        var r = RunOf(offset);
        var first = r;
        while (first > 0 && _runs[first - 1].Block == _runs[r].Block) first--;
        var last = r;
        while (last + 1 < _runs.Length && _runs[last + 1].Block == _runs[r].Block) last++;
        return (_offsets[first], _offsets[last] + _runs[last].Text.Length);
    }

    /// <summary>The text between two offsets, ready for the clipboard (platform line endings, no trailing spaces on lines).</summary>
    public string Copy(int a, int b)
    {
        var start = Math.Clamp(Math.Min(a, b), 0, Text.Length);
        var end = Math.Clamp(Math.Max(a, b), 0, Text.Length);
        if (end <= start) return "";
        var lines = Text[start..end].Split('\n').Select(line => line.TrimEnd(' '));
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// The highlight rectangles (page coordinates) for a selection, limited to runs that overlap the vertical band
    /// [<paramref name="top"/>, <paramref name="bottom"/>) -- one snapshot tile.
    /// </summary>
    public IEnumerable<Avalonia.Rect> Highlights(int a, int b, double top = double.NegativeInfinity, double bottom = double.PositiveInfinity)
    {
        var start = Math.Min(a, b);
        var end = Math.Max(a, b);
        if (end <= start || _runs.Length == 0) yield break;
        for (var r = RunOf(start); r < _runs.Length && _offsets[r] < end; r++)
        {
            var run = _runs[r];
            if (run.Y + run.Height <= top || run.Y >= bottom) continue;
            var s = Math.Clamp(start - _offsets[r], 0, run.Text.Length);
            var e = Math.Clamp(end - _offsets[r], 0, run.Text.Length);
            if (e <= s) continue;
            var x0 = Math.Min(run.Edges[s], run.Edges[e]);
            var x1 = Math.Max(run.Edges[s], run.Edges[e]);
            for (var j = s; j <= e; j++)
            {
                x0 = Math.Min(x0, run.Edges[j]);
                x1 = Math.Max(x1, run.Edges[j]);
            }
            if (x1 - x0 < 0.5) continue;
            yield return new Avalonia.Rect(run.X + x0, run.Y, x1 - x0, run.Height);
        }
    }

    /// <summary>The run whose text, or the separator after it, holds an offset.</summary>
    private int RunOf(int offset)
    {
        var index = Array.BinarySearch(_offsets, offset);
        if (index >= 0) return index;
        return Math.Max(0, ~index - 1);
    }

    /// <summary>Keeps an offset off the middle of a surrogate pair (an emoji is one character to the reader).</summary>
    private int Snap(int offset)
    {
        offset = Math.Clamp(offset, 0, Text.Length);
        return offset > 0 && offset < Text.Length && char.IsLowSurrogate(Text[offset]) ? offset - 1 : offset;
    }

    private static int Kind(char c) => char.IsLetterOrDigit(c) || c == '_' || char.IsSurrogate(c) || c == '\'' || c == '’' ? 1
        : char.IsWhiteSpace(c) ? 0 : 2;
}

/// <summary>
/// The current selection of one snapshot-rendered message: an anchor and a focus offset into <see cref="SnapshotText.Text"/>. One
/// instance is shared by all the tiles of a message, so a drag can run from one tile into the next and Ctrl+C in any tile copies the
/// whole selection.
/// </summary>
public sealed class SnapshotSelection(SnapshotText text)
{
    public SnapshotText Text { get; } = text;

    public int Anchor { get; private set; } = -1;

    public int Focus { get; private set; } = -1;

    public int Start => Math.Min(Anchor, Focus);

    public int End => Math.Max(Anchor, Focus);

    public bool IsEmpty => Anchor < 0 || Anchor == Focus;

    public string SelectedText => IsEmpty ? "" : Text.Copy(Start, End);

    /// <summary>Raised whenever the selection changes, so every tile repaints its part of the highlight.</summary>
    public event Action? Changed;

    public void Set(int anchor, int focus)
    {
        if (anchor == Anchor && focus == Focus) return;
        Anchor = anchor;
        Focus = focus;
        Changed?.Invoke();
    }

    public void SelectAll() => Set(0, Text.Text.Length);

    public void Clear() => Set(-1, -1);
}
