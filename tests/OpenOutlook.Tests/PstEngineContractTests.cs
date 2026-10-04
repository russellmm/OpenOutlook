using System.Reflection;
using OpenPst;
using PstCore;

namespace OpenOutlook.Tests;

public sealed class PstEngineContractTests
{
    [Fact]
    public void Managed_store_implements_the_engine_contract()
    {
        Assert.True(typeof(IPstEngine).IsAssignableFrom(typeof(PstStore)));
        // The interface must not drift from PstStore's public instance surface.
        var engine = typeof(IPstEngine).GetMembers().Where(m => m.MemberType is MemberTypes.Method or MemberTypes.Property)
            .Select(m => m.Name).Where(n => n != "Dispose").ToHashSet();
        var store = typeof(PstStore).GetMembers(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.MemberType is MemberTypes.Method or MemberTypes.Property)
            .Select(m => m.Name).ToHashSet();
        Assert.Empty(engine.Except(store));
    }

    [Fact]
    public void Managed_engine_opens_fixture_through_the_interface()
    {
        var path = Environment.GetEnvironmentVariable("OPENOUTLOOK_TEST_PST");
        if (string.IsNullOrWhiteSpace(path)) return;
        using IPstEngine engine = PstStore.Open(path, writable: false);
        Assert.NotEmpty(engine.AllFolders());
        Assert.NotNull(engine.Root);
    }

    [Fact]
    public void Native_library_loads_when_present_and_matches_expected_version()
    {
        if (!NativeLibraryLoader.IsAvailable) return; // native library is optional until Phase 1
        Assert.StartsWith(NativeLibraryLoader.ExpectedVersionPrefix, NativeLibraryLoader.Version);
    }

    [Fact]
    public void Native_and_managed_engines_agree_on_folder_tree()
    {
        var path = Environment.GetEnvironmentVariable("OPENOUTLOOK_TEST_PST");
        if (string.IsNullOrWhiteSpace(path) || !NativeLibraryLoader.IsAvailable) return;
        using var managed = PstStore.Open(path, writable: false);
        using var native = new PstFile(path);
        var m = managed.AllFolders().Select(f => (f.Nid, f.ContentCount)).OrderBy(x => x.Nid).ToArray();
        var n = new List<(uint, int)>();
        void Walk(uint parent) { foreach (var f in native.Children(parent)) { n.Add((f.Nid, f.ContentCount)); if (f.HasSubfolders) Walk(f.Nid); } }
        Walk(native.RootFolder);
        // Every native folder must exist in the managed tree with the same message count.
        foreach (var (nid, count) in n)
            Assert.Contains((nid, count), m);
    }
}
