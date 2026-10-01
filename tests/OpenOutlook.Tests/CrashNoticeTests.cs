using System.Text;
using OpenOutlook.Desktop;

namespace OpenOutlook.Tests;

/// <summary>
/// The notice is the user's only explanation when something escapes a handler, and its text is also
/// what lands on the clipboard for a bug report. A stack trace from a real failure can run to hundreds
/// of lines, which is unusable in a small window, so the bound is behaviour worth pinning down.
/// </summary>
public class CrashNoticeTests
{
    [Fact]
    public void Describe_names_the_type_and_message()
    {
        var text = CrashNotice.Describe(new InvalidOperationException("reader tile missing"));

        Assert.Contains("System.InvalidOperationException", text);
        Assert.Contains("reader tile missing", text);
    }

    [Fact]
    public void Describe_includes_inner_exceptions_so_the_root_cause_is_visible()
    {
        var exception = new InvalidOperationException("could not render the message",
            new IOException("chrome exited before the screenshot"));

        var text = CrashNotice.Describe(exception);

        Assert.Contains("could not render the message", text);
        Assert.Contains("caused by System.IO.IOException", text);
        Assert.Contains("chrome exited before the screenshot", text);
    }

    [Fact]
    public void Describe_bounds_a_long_stack_trace()
    {
        var exception = CaptureDeepException(200);

        var text = CrashNotice.Describe(exception);
        var traceLines = text.Split('\n').Count(line => line.Contains("in ", StringComparison.Ordinal) ||
                                                       line.TrimStart().StartsWith("at ", StringComparison.Ordinal));

        Assert.True(traceLines <= 13, $"expected a bounded trace, got {traceLines} lines:\n{text}");
        Assert.Contains("more frames in the log", text);
    }

    [Fact]
    public void Describe_survives_an_exception_with_no_stack_trace()
    {
        var text = CrashNotice.Describe(new ApplicationException("never thrown"));

        Assert.Contains("ApplicationException", text);
        Assert.DoesNotContain("more frames", text);
    }

    /// <summary>An exception with a genuinely deep trace, thrown for real so StackTrace is populated.</summary>
    private static Exception CaptureDeepException(int depth)
    {
        try
        {
            Recurse(depth);
            throw new InvalidOperationException("unreachable");
        }
        catch (Exception ex)
        {
            return ex;
        }

        static void Recurse(int remaining)
        {
            if (remaining <= 0) throw new IOException("deep failure");
            Recurse(remaining - 1);
        }
    }
}
