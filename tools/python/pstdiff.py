"""pstdiff.py A B - node-level diff of two PST files (nodes added / removed / changed: data length, subnode set, parent).
Used to compare Outlook-edited or SCANPST-repaired copies with the original; ~30 s for a 900 MB file."""
import sys, struct
sys.path.insert(0,'.')
import pstwrite as W
def load(p):
    w=W.PSTWriter(p); d={}
    for e in w.nbt.items():
        n,bd,bs,par,_=struct.unpack('<QQQII',e); d[n&0xFFFFFFFF]=(bd,bs,par)
    return w,d
def data(w,bid):
    if not bid: return b''
    try:
        r=bytes(w.read_block_raw(bid))
    except Exception as ex: return b'ERR'
    if bid&2 and r[:1]==b'\x01':
        out=b''
        for i in range(struct.unpack_from('<H',r,2)[0]):
            out+=data(w,struct.unpack_from('<Q',r,8+8*i)[0])
        return out
    import pstcore as P
    return r.translate(P.MPBB_I) if w.crypt==1 else r
def subs(w,bs):
    if not bs: return {}
    r=bytes(w.read_block_raw(bs))
    if r[0]!=2: return {'?':(0,0)}
    if r[1]!=0: return {'deep':(0,0)}
    return {struct.unpack_from('<Q',r,8+24*i)[0]&0xFFFFFFFF:struct.unpack_from('<QQ',r,16+24*i) for i in range(struct.unpack_from('<H',r,2)[0])}
def content(w,bd,bs):
    c=data(w,bd); s={k:content(w,a,b) for k,(a,b) in subs(w,bs).items()} if bs else {}
    return (c,s)
def diff(a,b,label):
    wa,da=load(a); wb,db=load(b)
    print('=====',label)
    for n in sorted(set(da)|set(db)):
        if n not in da: print('ADDED  0x%x type=0x%x par=0x%x'%(n,n&31,db[n][2]),'len',len(data(wb,db[n][0]))); continue
        if n not in db: print('REMOVED 0x%x'%n); continue
        ca=content(wa,*da[n][:2]); cb=content(wb,*db[n][:2])
        ch=[]
        if ca[0]!=cb[0]: ch.append('data %d->%d'%(len(ca[0]),len(cb[0])))
        if ca[1]!=cb[1]: ch.append('subnodes '+','.join('%x'%k if isinstance(k,int) else str(k) for k in set(ca[1])^set(cb[1]) or [k for k in ca[1] if ca[1][k]!=cb[1].get(k)]))
        if da[n][2]!=db[n][2]: ch.append('parent')
        if ch: print('CHANGED 0x%x type=0x%x: %s'%(n,n&31,'; '.join(ch)))

if __name__ == '__main__':
    diff(sys.argv[1], sys.argv[2], '%s -> %s' % (sys.argv[1], sys.argv[2]))
