using System.Linq;
using System.Reflection;

namespace OpenOutlook.Desktop;

/// <summary>
/// The version a build was stamped with, for File > Office Account > About and the log: "0.1.41 (commit 1a2b3c4)".
/// scripts/package-linux-x64.sh passes -p:Version and -p:SourceRevisionId (the commit, "-modified" when the build has uncommitted changes),
/// which the SDK joins into the informational version "0.1.41+1a2b3c4". An unstamped developer build shows the project's default.
/// </summary>
internal static class AppVersion
{
    public static string Display { get; } = Describe(
        typeof(AppVersion).Assembly.GetCustomAttributes<AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion,
        typeof(AppVersion).Assembly.GetName().Version?.ToString(3));

    internal static string Describe(string? informational, string? assemblyVersion)
    {
        if (string.IsNullOrWhiteSpace(informational)) return assemblyVersion ?? "unknown";
        var plus = informational.IndexOf('+');
        if (plus < 0) return informational;
        var commit = informational[(plus + 1)..];
        var modified = commit.EndsWith("-modified", System.StringComparison.Ordinal);
        if (modified) commit = commit[..^"-modified".Length];
        if (commit.Length > 7) commit = commit[..7];
        return $"{informational[..plus]} (commit {commit}{(modified ? " + changes" : "")})";
    }
}
