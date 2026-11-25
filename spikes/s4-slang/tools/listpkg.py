import sys, zipfile, collections
for path in sys.argv[1:]:
    print("==", path)
    z = zipfile.ZipFile(path)
    rids = collections.defaultdict(list)
    for i in z.infolist():
        n = i.filename
        if n.startswith("runtimes/"):
            parts = n.split("/")
            rids[parts[1]].append((n, i.file_size))
        elif not n.endswith("/") and not n.startswith(("_rels", "package/", "[Content")):
            print("  other:", n, i.file_size)
    for rid, files in sorted(rids.items()):
        print(" RID", rid, "files:", len(files), "total MB: %.1f" % (sum(s for _, s in files) / 1e6))
        for n, s in files:
            print("    ", n, s)
