# -*- coding: utf-8 -*-
"""诊断脚本：读卡片结构，定位服装/发型为什么没生效"""
import json, sys, urllib.parse, urllib.request

BASE = "http://127.0.0.1:24380"

def get(path, **params):
    url = BASE + path
    if params:
        url += "?" + urllib.parse.urlencode(params, encoding="utf-8")
    with urllib.request.urlopen(url, timeout=60) as r:
        return json.loads(r.read().decode("utf-8"))

def show(name, sex="female"):
    d = get("/inspect", file=name, sex=sex)
    print("=" * 70)
    print("卡片:", name)
    if not d.get("ok"):
        print("  读取失败:", d.get("error"))
        return
    print("  parameter:", d.get("parameter"))
    print("  coordinateType:", d.get("coordinateType"))
    print("  hair:", json.dumps(d.get("hair"), ensure_ascii=False))
    for c in d.get("coordinates", []):
        idx = c["index"]
        parts = c.get("clothes", [])
        line = []
        for p in parts:
            if p.get("part") in (0, 1, 2, 6):
                cols = [x.get("base") for x in p.get("colorInfo") or []]
                line.append("部位%d id=%s 色=%s 隐藏=%s" % (p.get("part"), p.get("id"), cols, p.get("hideOpt")))
        print("  套装[%d]:" % idx, " | ".join(line))

for n in sys.argv[1:]:
    show(n)
