using Microsoft.Data.Sqlite;

namespace OpenOutlook.Mirror;

public sealed record MirrorFolderState(string RemoteId, uint PstNid, string? SyncToken, DateTime? LastSync);
public sealed record MirrorMessageState(string RemoteId, string FolderRemoteId, uint PstNid, string? ChangeKey, bool IsRead, bool Flagged);
public sealed record PendingOperation(long Seq, string Op, string Args, DateTime CreatedUtc, int Attempts);

/// <summary>
/// The bookkeeping of one mirrored account (SQLite, one file next to the PST): which server folder / message is which PST node, the change
/// tokens of the incremental sync, and the journal of local changes still to be sent. Every write is transactional, so a crash leaves a
/// state that can be resumed. The file can be deleted: a full resync rebuilds it.
/// </summary>
public sealed class SyncStateStore : IDisposable
{
    private readonly SqliteConnection _db;
    public string Path { get; }

    public SyncStateStore(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _db.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;");
        Exec("""
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS folder(remote_id TEXT PRIMARY KEY, pst_nid INTEGER NOT NULL, sync_token TEXT, last_sync TEXT);
            CREATE TABLE IF NOT EXISTS message(remote_id TEXT NOT NULL, folder_remote_id TEXT NOT NULL, pst_nid INTEGER NOT NULL,
                change_key TEXT, is_read INTEGER NOT NULL, flagged INTEGER NOT NULL, PRIMARY KEY(remote_id, folder_remote_id));
            CREATE INDEX IF NOT EXISTS message_folder ON message(folder_remote_id);
            CREATE TABLE IF NOT EXISTS pending(seq INTEGER PRIMARY KEY AUTOINCREMENT, op TEXT NOT NULL, args TEXT NOT NULL, created TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0);
            """);
        if (GetMeta("schema") is null) SetMeta("schema", "1");
    }

    private void Exec(string sql, params (string, object?)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private T? Scalar<T>(string sql, params (string, object?)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        var o = cmd.ExecuteScalar();
        return o is null or DBNull ? default : (T)Convert.ChangeType(o, typeof(T));
    }

    public string? GetMeta(string key) => Scalar<string>("SELECT value FROM meta WHERE key=$k", ("$k", key));
    public void SetMeta(string key, string value) => Exec("INSERT INTO meta(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v", ("$k", key), ("$v", value));

    /// <summary>Runs several writes as one transaction.</summary>
    public void InTransaction(Action work)
    {
        using var tx = _db.BeginTransaction();
        try { work(); tx.Commit(); }
        catch { tx.Rollback(); throw; }
    }

    // ---- folders
    public void UpsertFolder(MirrorFolderState f) => Exec(
        "INSERT INTO folder(remote_id,pst_nid,sync_token,last_sync) VALUES($r,$n,$t,$l) ON CONFLICT(remote_id) DO UPDATE SET pst_nid=$n, sync_token=$t, last_sync=$l",
        ("$r", f.RemoteId), ("$n", (long)f.PstNid), ("$t", f.SyncToken), ("$l", f.LastSync?.ToString("O")));

    public MirrorFolderState? GetFolder(string remoteId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT remote_id,pst_nid,sync_token,last_sync FROM folder WHERE remote_id=$r";
        cmd.Parameters.AddWithValue("$r", remoteId);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadFolder(r) : null;
    }

    public IReadOnlyList<MirrorFolderState> Folders()
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT remote_id,pst_nid,sync_token,last_sync FROM folder ORDER BY remote_id";
        using var r = cmd.ExecuteReader();
        var list = new List<MirrorFolderState>();
        while (r.Read()) list.Add(ReadFolder(r));
        return list;
    }

    private static MirrorFolderState ReadFolder(SqliteDataReader r) =>
        new(r.GetString(0), (uint)r.GetInt64(1), r.IsDBNull(2) ? null : r.GetString(2), r.IsDBNull(3) ? null : DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind));

    public void DeleteFolder(string remoteId) => InTransaction(() =>
    {
        Exec("DELETE FROM message WHERE folder_remote_id=$r", ("$r", remoteId));
        Exec("DELETE FROM folder WHERE remote_id=$r", ("$r", remoteId));
    });

    // ---- messages
    public void UpsertMessage(MirrorMessageState m) => Exec(
        "INSERT INTO message(remote_id,folder_remote_id,pst_nid,change_key,is_read,flagged) VALUES($r,$f,$n,$c,$i,$g) " +
        "ON CONFLICT(remote_id,folder_remote_id) DO UPDATE SET pst_nid=$n, change_key=$c, is_read=$i, flagged=$g",
        ("$r", m.RemoteId), ("$f", m.FolderRemoteId), ("$n", (long)m.PstNid), ("$c", m.ChangeKey), ("$i", m.IsRead ? 1 : 0), ("$g", m.Flagged ? 1 : 0));

    public IReadOnlyList<MirrorMessageState> MessagesIn(string folderRemoteId)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT remote_id,folder_remote_id,pst_nid,change_key,is_read,flagged FROM message WHERE folder_remote_id=$f";
        cmd.Parameters.AddWithValue("$f", folderRemoteId);
        using var r = cmd.ExecuteReader();
        var list = new List<MirrorMessageState>();
        while (r.Read()) list.Add(new(r.GetString(0), r.GetString(1), (uint)r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetInt32(4) != 0, r.GetInt32(5) != 0));
        return list;
    }

    public MirrorMessageState? GetMessage(string remoteId, string folderRemoteId) => MessagesIn(folderRemoteId).FirstOrDefault(m => m.RemoteId == remoteId);

    public void DeleteMessage(string remoteId, string folderRemoteId) =>
        Exec("DELETE FROM message WHERE remote_id=$r AND folder_remote_id=$f", ("$r", remoteId), ("$f", folderRemoteId));

    public int MessageCount() => Scalar<int>("SELECT COUNT(*) FROM message");

    // ---- journal of local changes
    public long Enqueue(string op, string args)
    {
        Exec("INSERT INTO pending(op,args,created) VALUES($o,$a,$c)", ("$o", op), ("$a", args), ("$c", DateTime.UtcNow.ToString("O")));
        return Scalar<long>("SELECT last_insert_rowid()");
    }

    public IReadOnlyList<PendingOperation> Pending(int max = 100)
    {
        using var cmd = _db.CreateCommand();
        cmd.CommandText = "SELECT seq,op,args,created,attempts FROM pending ORDER BY seq LIMIT $m";
        cmd.Parameters.AddWithValue("$m", max);
        using var r = cmd.ExecuteReader();
        var list = new List<PendingOperation>();
        while (r.Read()) list.Add(new(r.GetInt64(0), r.GetString(1), r.GetString(2), DateTime.Parse(r.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind), r.GetInt32(4)));
        return list;
    }

    public void Complete(long seq) => Exec("DELETE FROM pending WHERE seq=$s", ("$s", seq));
    public void Failed(long seq) => Exec("UPDATE pending SET attempts=attempts+1 WHERE seq=$s", ("$s", seq));
    public int PendingCount() => Scalar<int>("SELECT COUNT(*) FROM pending");

    public void Dispose() => _db.Dispose();
}
