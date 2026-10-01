using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace OpenOutlook.Desktop;

/// <summary>
/// File dialogs that report failure instead of escaping into the platform loop.
///
/// The pickers were awaited bare at eight call sites. On Linux they talk to a portal service, and when
/// that service is absent, slow, or shuts down mid-request the call throws -- previously past every
/// handler, which meant opening an archive or saving an export could close the application with no
/// explanation. Returning an empty result matches what "the user cancelled" already means to every
/// caller here, so a failed dialog behaves like a cancelled one while still being logged and shown.
/// </summary>
internal static class SafePick
{
    public static async Task<IReadOnlyList<IStorageFolder>> FoldersAsync(
        Window window, FolderPickerOpenOptions options, Action<string>? report = null)
    {
        try
        {
            return await window.StorageProvider.OpenFolderPickerAsync(options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error("picker", ex, "folder picker failed");
            report?.Invoke(Describe("folder chooser", ex));
            return [];
        }
    }

    public static async Task<IReadOnlyList<IStorageFile>> FilesAsync(
        Window window, FilePickerOpenOptions options, Action<string>? report = null)
    {
        try
        {
            return await window.StorageProvider.OpenFilePickerAsync(options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            AppLog.Error("picker", ex, "file picker failed");
            report?.Invoke(Describe("file chooser", ex));
            return [];
        }
    }

    /// <summary>One status-bar-sized sentence: portal errors can carry long multi-line text.</summary>
    private static string Describe(string what, Exception ex)
    {
        var detail = ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (detail.Length > 160) detail = detail[..160].TrimEnd() + "…";
        return $"Could not open the {what}. {(detail.Length == 0 ? "" : detail + " ")}" +
            "The dialog may be provided by a desktop service that is unavailable; nothing was changed.";
    }
}
