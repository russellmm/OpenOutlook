using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using OpenOutlook.Desktop;
using Xunit;

namespace OpenOutlook.HeadlessTests;

public sealed class DragCursorHeadlessTests
{
    [AvaloniaFact]
    public void Avalonias_drag_cursors_are_replaced_by_the_themes_and_other_cursors_are_left_alone()
    {
        var asked = new List<string>();
        var move = Themed(); var copy = Themed();
        LinuxDragCursors.SetLoaderForTests(name => { asked.Add(name); return name switch { "dnd-move" => move, "copy" => copy, _ => null }; });
        try
        {
            var window = new Window();
            window.Cursor = new Cursor(StandardCursorType.DragMove);          // what Avalonia's drag code sets over a folder that takes a move
            Assert.Same(move, window.Cursor);
            window.Cursor = new Cursor(StandardCursorType.DragCopy);          // dnd-copy missing in the theme: the next name is tried
            Assert.Same(copy, window.Cursor);
            window.Cursor = new Cursor(StandardCursorType.DragMove);          // loaded once
            Assert.Same(move, window.Cursor);
            Assert.Equal(1, asked.Count(n => n == "dnd-move"));
            var link = new Cursor(StandardCursorType.DragLink);               // nothing in the theme: Avalonia's cursor stays
            window.Cursor = link;
            Assert.Same(link, window.Cursor);
            var hand = new Cursor(StandardCursorType.Hand);                   // not a drag cursor: never touched
            window.Cursor = hand;
            Assert.Same(hand, window.Cursor);
            Assert.DoesNotContain("hand2", asked);
        }
        finally { LinuxDragCursors.SetLoaderForTests(_ => null); }
    }

    private static Cursor Themed() =>
        new(new WriteableBitmap(new PixelSize(4, 4), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul), new PixelPoint(1, 1));
}
