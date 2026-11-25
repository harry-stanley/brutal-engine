"""List nupkg contents and scan native binaries in-memory (never extracts/executes).
Usage: python -I inspect.py <nupkg> [keyword ...]"""
import sys, zipfile, re, struct

def pe_exports_imports(data):
    # Minimal PE parser: export names + imported DLL names.
    out_exp, out_imp = [], []
    if data[:2] != b'MZ':
        return None
    pe = struct.unpack_from('<I', data, 0x3c)[0]
    if data[pe:pe+4] != b'PE\0\0':
        return None
    nsec = struct.unpack_from('<H', data, pe+6)[0]
    optsz = struct.unpack_from('<H', data, pe+20)[0]
    opt = pe + 24
    magic = struct.unpack_from('<H', data, opt)[0]
    ddir = opt + (112 if magic == 0x20b else 96)
    exp_rva, _ = struct.unpack_from('<II', data, ddir)
    imp_rva, _ = struct.unpack_from('<II', data, ddir+8)
    secs = []
    so = opt + optsz
    for i in range(nsec):
        va, vs, raw_sz, raw = struct.unpack_from('<IIII', data, so + i*40 + 8)[0:4] if False else (
            struct.unpack_from('<I', data, so+i*40+12)[0], struct.unpack_from('<I', data, so+i*40+8)[0],
            struct.unpack_from('<I', data, so+i*40+16)[0], struct.unpack_from('<I', data, so+i*40+20)[0])
        secs.append((va, max(vs, raw_sz), raw))
    def off(rva):
        for va, sz, raw in secs:
            if va <= rva < va + sz:
                return rva - va + raw
        return None
    def cstr(o):
        e = data.index(b'\0', o)
        return data[o:e].decode('latin1')
    if exp_rva:
        eo = off(exp_rva)
        nnames = struct.unpack_from('<I', data, eo+24)[0]
        names_rva = struct.unpack_from('<I', data, eo+32)[0]
        no = off(names_rva)
        for i in range(nnames):
            out_exp.append(cstr(off(struct.unpack_from('<I', data, no+4*i)[0])))
    if imp_rva:
        io = off(imp_rva)
        while True:
            name_rva = struct.unpack_from('<I', data, io+12)[0]
            if name_rva == 0:
                break
            out_imp.append(cstr(off(name_rva)))
            io += 20
    return out_exp, out_imp

def elf_needed_and_dynsyms(data):
    # Use strings heuristics for ELF/Mach-O: report lib*.so* / *.dylib references.
    libs = sorted(set(m.decode() for m in re.findall(rb'(?:lib[\w\-\.+]+\.so(?:\.\d+)*|@rpath/[\w\-\.+]+\.dylib|/usr/lib/[\w\-\./+]+\.dylib|[\w\-\.+]+\.dylib)', data)))
    return libs

def main():
    path = sys.argv[1]
    kws = [k.encode() for k in sys.argv[2:]]
    z = zipfile.ZipFile(path)
    print('## files in', path)
    for i in z.infolist():
        print(f'{i.file_size:>12}  {i.filename}')
    for i in z.infolist():
        n = i.filename.lower()
        if not (n.endswith('.dll') or '.so' in n.rsplit('/',1)[-1] or n.endswith('.dylib')):
            continue
        if n.startswith('lib/') :
            continue  # managed
        data = z.read(i)
        print(f'\n### {i.filename} ({len(data)} bytes)')
        r = pe_exports_imports(data)
        if r:
            exp, imp = r
            print('PE imports:', imp)
            print(f'PE exports ({len(exp)}):')
            for e in exp:
                print('   ', e)
        else:
            print('linked libs (string scan):', elf_needed_and_dynsyms(data))
        for k in kws:
            hits = sorted(set(m.decode('latin1') for m in re.findall(rb'[\x20-\x7e]{0,40}' + re.escape(k) + rb'[\x20-\x7e]{0,40}', data, re.I)))
            print(f'  keyword {k.decode()!r}: {len(hits)} distinct strings; sample: {hits[:12]}')

main()
