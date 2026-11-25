"""Print exported (defined, GLOBAL/WEAK) dynamic symbols of an ELF64 inside a nupkg, filtered by prefix.
Also DT_NEEDED. Usage: python -I elfdyn.py <nupkg> <member> <prefix> [prefix...]"""
import sys, zipfile, struct

z = zipfile.ZipFile(sys.argv[1]); d = z.read(sys.argv[2]); prefixes = sys.argv[3:]
assert d[:4] == b'\x7fELF' and d[4] == 2
shoff = struct.unpack_from('<Q', d, 0x28)[0]
shentsize, shnum, shstrndx = struct.unpack_from('<HHH', d, 0x3a)
secs = []
for i in range(shnum):
    o = shoff + i * shentsize
    name, typ, flags, addr, off, size, link, info, align, entsize = struct.unpack_from('<IIQQQQIIQQ', d, o)
    secs.append((name, typ, off, size, link, entsize))
def s(tab_off, i):
    e = d.index(b'\0', tab_off + i); return d[tab_off + i:e].decode('latin1')
dynsym = [x for x in secs if x[1] == 11][0]
strtab = secs[dynsym[4]]
dyn = [x for x in secs if x[1] == 6]
needed = []
if dyn:
    _, _, off, size, link, _ = dyn[0]
    for k in range(0, size, 16):
        tag, val = struct.unpack_from('<qQ', d, off + k)
        if tag == 1: needed.append(s(secs[link][2], val))
print('DT_NEEDED:', needed)
_, _, off, size, _, ent = dynsym
exported = []
for k in range(0, size, ent):
    st_name, st_info, st_other, st_shndx, st_value, st_size = struct.unpack_from('<IBBHQQ', d, off + k)
    bind = st_info >> 4
    if st_shndx != 0 and bind in (1, 2) and st_name:
        exported.append(s(strtab[2], st_name))
print('total exported dynamic symbols:', len(exported))
for p in prefixes:
    m = sorted(x for x in exported if x.startswith(p))
    print(f'prefix {p!r}: {len(m)}', m[:25])
