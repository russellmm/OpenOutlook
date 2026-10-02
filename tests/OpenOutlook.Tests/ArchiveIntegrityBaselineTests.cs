using PstCore;

namespace OpenOutlook.Tests;

/// <summary>
/// Read-only integrity baselines for real archives, opt-in via OPENOUTLOOK_BASELINE_PST (set it to
/// any archive path; repeat per archive in CI/local runs). This NEVER opens anything writable - it
/// exists so that a future post-edit verification failure can be distinguished from damage that was
/// already present in a hand-me-down archive. Reports problem descriptions as assertion messages so
/// the operator sees exactly what Outlook would object to.
/// </summary>
public sealed class ArchiveIntegrityBaselineTests
{
    [Fact]
    public void BaselineArchivePassesFullCrcVerification()
    {
        var path = Environment.GetEnvironmentVariable("OPENOUTLOOK_BASELINE_PST");
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        var sizeBefore = new FileInfo(path).Length;
        using var store = PstStore.Open(path, writable: false);
        var problems = store.VerifyIntegrity();
        Assert.True(problems.Count == 0,
            $"{Path.GetFileName(path)} has {problems.Count} pre-existing integrity problem(s): {string.Join(" | ", problems.Take(10))}");
        Assert.NotEmpty(store.AllFolders());
        Assert.Equal(sizeBefore, new FileInfo(path).Length); // verification is strictly read-only
    }
}
