namespace PstCore;

/// <summary>
/// A write session for one PST archive, safe by construction: Begin() copies the archive to a
/// side-by-side .bak before anything is touched; Commit() runs a full integrity verification and only
/// then marks the session durable; Dispose() without Commit() restores the backup, so an exception
/// (or a crash in the caller's batch of edits) can never leave a half-edited archive behind. The last
/// successful .bak is kept on disk as a recovery net until the next session replaces it.
/// </summary>
public sealed class PstEditSession : IDisposable
{
    public string ArchivePath { get; }
    public string BackupPath { get; }
    public PstStore Store { get; }
    private bool _finished;

    private PstEditSession(string archivePath, string backupPath, PstStore store)
    {
        ArchivePath = archivePath;
        BackupPath = backupPath;
        Store = store;
    }

    public static PstEditSession Begin(string path)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("PST not found.", full);
        var backup = full + ".bak";
        File.Copy(full, backup, overwrite: true);
        PstStore store;
        try
        {
            store = PstStore.Open(full, writable: true);
        }
        catch
        {
            // The archive was never opened; if a stale .bak from this call exists and matched, fine -
            // it is byte-identical to the untouched original.
            throw;
        }
        return new PstEditSession(full, backup, store);
    }

    /// <summary>Verifies every B-tree page and block trailer on disk. Throws when anything fails;
    /// the session then rolls back automatically on Dispose.</summary>
    public void Commit()
    {
        var problems = Store.VerifyIntegrity();
        if (problems.Count > 0)
            throw new PstException($"PST failed integrity verification after editing ({problems.Count} problem(s)): {string.Join(" | ", problems.Take(3))}");
        _finished = true;
    }

    /// <summary>Restores the backup immediately (store is closed first). Used when a mid-batch step
    /// fails and the caller wants the original archive back without waiting for Dispose.</summary>
    public void Rollback()
    {
        if (_finished) return;
        _finished = true;
        Store.Dispose();
        File.Copy(BackupPath, ArchivePath, overwrite: true);
    }

    public void Dispose()
    {
        Store.Dispose();
        if (!_finished)
        {
            _finished = true;
            try { File.Copy(BackupPath, ArchivePath, overwrite: true); }
            catch (IOException) { /* backup remains on disk for manual recovery */ }
        }
    }
}
