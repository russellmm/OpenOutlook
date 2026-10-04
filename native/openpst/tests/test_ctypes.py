"""test_ctypes.py - calls the shared library through ctypes (the same ABI a C# P/Invoke wrapper uses).
usage: python3 tests/test_ctypes.py path/to/libopenpst.so[.dll] file.pst
"""
import ctypes as C, sys

lib = C.CDLL(sys.argv[1])
path = sys.argv[2].encode()

class Folder(C.Structure):
    _fields_ = [('nid', C.c_uint32), ('parent', C.c_uint32), ('name', C.c_char_p), ('content_count', C.c_int32),
                ('unread_count', C.c_int32), ('has_subfolders', C.c_int32)]
class Row(C.Structure):
    _fields_ = [('nid', C.c_uint32), ('flags', C.c_uint32), ('sent', C.c_int64), ('received', C.c_int64), ('size', C.c_int64),
                ('importance', C.c_int32), ('has_attachments', C.c_int32), ('subject', C.c_char_p), ('sender', C.c_char_p),
                ('to', C.c_char_p), ('cc', C.c_char_p), ('topic', C.c_char_p), ('message_class', C.c_char_p)]
class Recip(C.Structure):
    _fields_ = [('name', C.c_char_p), ('email', C.c_char_p), ('type', C.c_int32)]
class Att(C.Structure):
    _fields_ = [('index', C.c_uint32), ('nid', C.c_uint32), ('filename', C.c_char_p), ('mime', C.c_char_p), ('cid', C.c_char_p),
                ('size', C.c_int64), ('method', C.c_int32)]

lib.opst_open.argtypes = [C.c_char_p, C.c_uint, C.POINTER(C.c_void_p)]
lib.opst_display_name.argtypes = [C.c_void_p]; lib.opst_display_name.restype = C.c_char_p
lib.opst_ipm_root.argtypes = [C.c_void_p]; lib.opst_ipm_root.restype = C.c_uint32
lib.opst_last_error.restype = C.c_char_p
lib.opst_folder_children.argtypes = [C.c_void_p, C.c_uint32, C.POINTER(C.POINTER(Folder)), C.POINTER(C.c_size_t)]
lib.opst_free_folders.argtypes = [C.c_void_p]
lib.opst_messages.argtypes = [C.c_void_p, C.c_uint32, C.POINTER(C.POINTER(Row)), C.POINTER(C.c_size_t)]
lib.opst_free_messages.argtypes = [C.c_void_p]
lib.opst_msg_open.argtypes = [C.c_void_p, C.c_uint32, C.POINTER(C.c_void_p)]
lib.opst_msg_close.argtypes = [C.c_void_p]
lib.opst_msg_str.argtypes = [C.c_void_p, C.c_uint16]; lib.opst_msg_str.restype = C.c_char_p
lib.opst_msg_body.argtypes = [C.c_void_p, C.c_int, C.POINTER(C.c_size_t)]; lib.opst_msg_body.restype = C.c_void_p
lib.opst_msg_recipients.argtypes = [C.c_void_p, C.POINTER(C.POINTER(Recip)), C.POINTER(C.c_size_t)]
lib.opst_free_recipients.argtypes = [C.c_void_p]
lib.opst_msg_attachments.argtypes = [C.c_void_p, C.POINTER(C.POINTER(Att)), C.POINTER(C.c_size_t)]
lib.opst_free_attachments.argtypes = [C.c_void_p]
lib.opst_attachment_data.argtypes = [C.c_void_p, C.c_uint32, C.POINTER(C.c_void_p), C.POINTER(C.c_size_t)]
lib.opst_close.argtypes = [C.c_void_p]

h = C.c_void_p()
assert lib.opst_open(path, 0, C.byref(h)) == 0, lib.opst_last_error()
print('store:', lib.opst_display_name(h).decode())
nmessages = nattach = 0
def walk(nid):
    global nmessages, nattach
    arr = C.POINTER(Folder)(); n = C.c_size_t()
    assert lib.opst_folder_children(h, nid, C.byref(arr), C.byref(n)) == 0
    kids = [(arr[i].nid, arr[i].name.decode()) for i in range(n.value)]
    lib.opst_free_folders(arr)
    for fn, name in kids:
        rows = C.POINTER(Row)(); nr = C.c_size_t()
        assert lib.opst_messages(h, fn, C.byref(rows), C.byref(nr)) == 0
        for i in range(nr.value):
            global nmessages_total
            nm = rows[i].nid
            m = C.c_void_p()
            assert lib.opst_msg_open(h, nm, C.byref(m)) == 0
            subj = lib.opst_msg_str(m, 0x37)
            assert (subj or b'') == (rows[i].subject or b''), (subj, rows[i].subject)
            ln = C.c_size_t()
            lib.opst_msg_body(m, 0, C.byref(ln))
            ra = C.POINTER(Recip)(); nrc = C.c_size_t(); lib.opst_msg_recipients(m, C.byref(ra), C.byref(nrc)); lib.opst_free_recipients(ra)
            aa = C.POINTER(Att)(); na = C.c_size_t(); lib.opst_msg_attachments(m, C.byref(aa), C.byref(na))
            for k in range(na.value):
                d = C.c_void_p(); dl = C.c_size_t()
                assert lib.opst_attachment_data(m, aa[k].index, C.byref(d), C.byref(dl)) == 0
                nattach += 1
            lib.opst_free_attachments(aa)
            lib.opst_msg_close(m)
            globals()['nmessages'] = globals().get('nmessages', 0) + 1
        lib.opst_free_messages(rows)
        walk(fn)
walk(0x122)
print('messages read through the C ABI:', globals().get('nmessages', 0), ' attachments fetched:', nattach)
lib.opst_close(h)
print('OK')
