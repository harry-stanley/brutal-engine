"""List a managed nupkg: files (filtered), nuspec licence, and metadata-string hints in lib DLLs.
Usage: python -I managed.py <nupkg> [tfm-substring]"""
import sys, zipfile, re
z = zipfile.ZipFile(sys.argv[1]); tfm = sys.argv[2] if len(sys.argv) > 2 else ''
ns = [n for n in z.namelist() if n.endswith('.nuspec')][0]
t = z.read(ns).decode('utf-8', 'replace')
print('nuspec:', re.findall(r'<(?:id|version|license[^>]*|licenseUrl|repository[^>]*)>?[^<]*', t))
for i in z.infolist():
    if i.filename.startswith(('lib/', 'runtimes/', 'build')) and not i.filename.endswith(('.xml', '.pdb')):
        print(f'{i.file_size:>10}  {i.filename}')
HINTS = [b'IsTrimmable', b'IsAotCompatible', b'DllImportAttribute', b'LibraryImportAttribute', b'GetDelegateForFunctionPointer',
         b'MakeGenericType', b'Activator', b'Reflection.Emit', b'RequiresUnreferencedCode', b'RequiresDynamicCode', b'ArrayPool',
         b'stackalloc', b'Span`1', b'GC.AllocateUninitializedArray', b'DisableRuntimeMarshalling', b'UnmanagedCallersOnly',
         b'Marshal', b'Vector`1', b'Vector128', b'ThreadLocal', b'ConcurrentQueue', b'IsSupported']
for i in z.infolist():
    n = i.filename
    if n.startswith('lib/') and n.endswith('.dll') and tfm in n:
        d = z.read(i)
        print(f'\n## {n} ({len(d)} bytes)')
        print('  hints:', [h.decode() for h in HINTS if h in d])
        dll = sorted(set(m.decode() for m in re.findall(rb'(?:lib)?(?:HarfBuzzSharp|harfbuzz|libHarfBuzzSharp)[\w.]*', d)))
        print('  native names:', dll[:10])
