"""pstactions.py - GUI-free editing layer (stage F). Every public method is ONE atomic, journalled write:
open the file for writing -> do the operation -> commit -> close. Nothing is kept open between calls, so the
read-only viewer (pstcore) and the writer never hold the file at the same time.

    s = Session(path)
    s.folder_info()                       -> {nid: FolderInfo} for the whole tree (name, parent, protected, ...)
    s.create_folder(parent_nid, name)     -> new nid
    s.rename_folder(nid, name)
    s.move_folder(nid, dest_nid)
    s.delete_folder(nid)                  -> ('moved'|'purged', stats)
    s.move_messages(nids, dest_nid) / copy_messages / delete_messages(nids)
    s.copy_to_pst(nids, other_pst_path, dest_nid)   -> copy into a folder of another PST file
All raise pstedit.EditError (user-readable text) for refused operations; nothing is written in that case.
"""
import os, sys
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import pstedit as E
import pstops as O
import pstfolders as F

# English names of the structural folders directly under "Top of Personal Folders". They are protected by
# name as well as by the ids recorded in the store, because not every file records all of them.
WELL_KNOWN = {n.lower() for n in (
    'Inbox', 'Outbox', 'Sent Items', 'Deleted Items', 'Drafts', 'Junk Email', 'Junk E-mail', 'Calendar',
    'Contacts', 'Tasks', 'Notes', 'Journal', 'RSS Feeds', 'Conversation History', 'Sync Issues',
    'Search Root', 'Quick Step Settings', 'Suggested Contacts', 'Conversation Action Settings')}


class FolderInfo:
    __slots__ = ('nid', 'name', 'parent', 'protected', 'in_deleted', 'children')

    def __init__(self, nid, name, parent):
        self.nid, self.name, self.parent = nid, name, parent
        self.protected = False
        self.in_deleted = False
        self.children = []

    def __repr__(self):
        return 'FolderInfo(0x%x %r%s)' % (self.nid, self.name, ' protected' if self.protected else '')


class Session:
    def __init__(self, path):
        self.path = path
        self.ipm_root = None            # set by folder_info(): "Top of Outlook data file"

    # ---- reading structure (write-library view; used for protection / validation in the GUI) ---------------
    def folder_info(self):
        ops = F.FolderOps(self.path)
        try:
            prot = ops.protected()
            top = ops.deleted_nid and ops.parent_of(ops.deleted_nid)       # the IPM subtree root
            self.ipm_root = top
            out = {}

            def walk(nid, parent, name, in_del):
                fi = FolderInfo(nid, name, parent)
                fi.in_deleted = in_del
                fi.protected = nid in prot or (parent == top and name.lower() in WELL_KNOWN)
                out[nid] = fi
                for c, n in ops.children(nid):
                    if c not in out:
                        fi.children.append(c)
                        walk(c, nid, n, in_del or c == ops.deleted_nid)
            walk(F.NID_ROOT, 0, '', False)
            return out
        finally:
            ops.close()

    def _by_name_guard(self, nids):
        info = self.folder_info()
        for n in nids:
            fi = info.get(n)
            if fi is None:
                raise E.EditError('folder 0x%x not found' % n)
            if fi.protected:
                raise E.EditError('"%s" is a special folder and cannot be changed' % fi.name)
        return info

    # ---- transaction wrapper ---------------------------------------------------------------------------
    def _run(self, cls, fn):
        ops = cls(self.path)
        try:
            r = fn(ops)
            ops.commit()
            return r
        except Exception:
            try:
                ops.w.rollback_pending()
            except Exception:
                pass
            raise
        finally:
            ops.close()

    # ---- folders ----------------------------------------------------------------------------------------
    def create_folder(self, parent, name, cls='IPF.Note'):
        name = name.strip()
        return self._run(F.FolderOps, lambda o: o.create(parent, name, cls))

    def rename_folder(self, nid, name):
        name = name.strip()
        self._by_name_guard([nid])
        return self._run(F.FolderOps, lambda o: o.rename(nid, name))

    def move_folder(self, nid, dest):
        self._by_name_guard([nid])
        return self._run(F.FolderOps, lambda o: o.move(nid, dest))

    def delete_folder(self, nid):
        self._by_name_guard([nid])
        return self._run(F.FolderOps, lambda o: o.delete(nid))

    # ---- messages ---------------------------------------------------------------------------------------
    def move_messages(self, nids, dest):
        return self._run(O.Ops, lambda o: o.move(list(nids), dest))

    def copy_messages(self, nids, dest):
        return self._run(O.Ops, lambda o: o.copy(list(nids), dest))

    def delete_messages(self, nids):
        return self._run(O.Ops, lambda o: o.delete(list(nids)))

    def delete_messages_permanently(self, nids):
        return self._run(O.Ops, lambda o: o.purge(list(nids)))

    def copy_to_pst(self, nids, dest_path, dest_nid):
        """Copy messages of THIS file into folder dest_nid of ANOTHER PST file (only the other file is written).
        Named properties are translated, attachments kept. Returns the new NIDs (in the other file)."""
        import pstxcopy as X
        return X.copy_messages(self.path, list(nids), dest_path, dest_nid)
