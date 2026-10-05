#nullable enable
using System;
using System.Diagnostics;
using System.IO;

namespace OpenOutlook.PstNative
{
    /// <summary>
    /// Single-writer guard: two windows editing one archive overwrite each other's work. The lock file
    /// (&lt;archive&gt;.lck) names the owning process; a lock whose process is gone is stale and is replaced.
    /// </summary>
    internal static class PstEditLock
    {
        // locks held by this process (a second open of the same archive in this process must be refused too)
        static readonly System.Collections.Generic.HashSet<string> Held =
            new System.Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public static void Acquire(string archivePath)
        {
            lock (Held)
            {
                if (Held.Contains(archivePath))
                    throw new PstCore.PstException(
                        "This archive is already open for editing in this OpenOutlook window. Two writers on one file would overwrite each other's changes.");
                AcquireFile(archivePath);
                Held.Add(archivePath);
            }
        }

        static void AcquireFile(string archivePath)
        {
            var lockPath = archivePath + ".lck";
            if (File.Exists(lockPath))
            {
                string owner = "";
                try { owner = File.ReadAllText(lockPath).Trim(); } catch (IOException) { }
                if (int.TryParse(owner, out var pid))
                {
                    if (pid != Environment.ProcessId)
                    {
                        bool alive;
                        try { Process.GetProcessById(pid); alive = true; } catch (ArgumentException) { alive = false; }
                        if (alive)
                            throw new PstCore.PstException(
                                "This archive is already open for editing in another OpenOutlook window (process " + pid +
                                "). Close that window first - two writers on one file would overwrite each other's changes.");
                    }
                }
                else throw new PstCore.PstException("This archive has a leftover edit lock (" + lockPath + "); delete it only if no OpenOutlook window has the file open.");
            }
            try { File.WriteAllText(lockPath, Environment.ProcessId.ToString()); }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new PstCore.PstException("The archive's folder is not writable, so it cannot be opened for editing: " + e.Message, e);
            }
        }

        public static void Release(string archivePath)
        {
            lock (Held) Held.Remove(archivePath);
            var lockPath = archivePath + ".lck";
            try
            {
                if (File.ReadAllText(lockPath).Trim() == Environment.ProcessId.ToString()) File.Delete(lockPath);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
