# -*- coding: utf-8 -*-
"""按关键词在游戏资源里找候选 id"""
import json, sys, urllib.parse, urllib.request

BASE = "http://127.0.0.1:24380"

def get(path, **p):
    url = BASE + path + ("?" + urllib.parse.urlencode(p, encoding="utf-8") if p else "")
    return json.loads(urllib.request.urlopen(url, timeout=90).read().decode("utf-8"))

d = get("/options")
cats = d
if len(sys.argv) > 1 and sys.argv[1] == "--dump":
    for k, v in cats.items():
        if isinstance(v, list):
            print("[%s] %d 项" % (k, len(v)))
    sys.exit(0)

kws = sys.argv[1].split(",") if len(sys.argv) > 1 else []
only = sys.argv[2].split(",") if len(sys.argv) > 2 else None
for k, v in cats.items():
    if not isinstance(v, list):
        continue
    if only and k not in only:
        continue
    hits = [x for x in v if any(w.lower() in (x.get("name") or "").lower() for w in kws)]
    if hits:
        print("=== %s ===" % k)
        for h in hits[:40]:
            print("   id=%-5s %s" % (h["id"], h["name"]))
