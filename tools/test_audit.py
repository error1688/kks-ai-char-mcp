# -*- coding: utf-8 -*-
"""本轮新能力的验收：
   1) /audit 在"叠了 3 个同名角色"时是否报 fail（这正是之前把我骗过去的场景）
   2) /audit 在干净场景下报 ok
   3) /txn snapshot -> list -> restore 往返
   4) ref_crop 修好没有：head 裁出来的应该是头，不是脚
"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

def get(p, t=300):
    return json.loads(urllib.request.urlopen(BASE + p, timeout=t).read().decode())

def show_audit(tag):
    a = get("/audit")
    print("%-14s kind=%-4s problems=%s" % (tag, a.get("kind"),
          [p["name"] for p in a.get("problems", [])]))
    for c in a.get("checks", []):
        if not c.get("ok"): print("      检查未过: %s -> %s" % (c["name"], c["detail"]))
    return a

GEN = {
    "base": "お嬢様.png", "sex": "female", "save_as": "AI-审计", "overwrite": True,
    "clear_accessories": True, "smooth_skin": True,
    "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
    "face": {"pupil": {"id": 0, "baseColor": "#2E7FD4", "subColor": "#B9ECFF"}},
    "body": {"shape": {"BustSize": 0.38}, "normalize": True},
    "clothes": {"top": {"id": 218, "color0": "#1E2A55", "color1": "#FFFFFF", "pattern0": 0},
                "bot": {"id": 208, "color0": "#1E2A55", "color1": "#2E4A8C", "pattern0": 0},
                "socks": {"id": 5, "color0": "#FFFFFF", "pattern0": 0}},
}

print("清场:", json.dumps(post("/clear", {}), ensure_ascii=False))
show_audit("空场景")
post("/generate", dict(GEN, load=True), 300)
a1 = show_audit("单个角色")

# 故意叠 2 个同名角色（模拟我之前的操作）
for i in range(2):
    post("/generate", dict(GEN, load=True, add=True), 300)
a3 = show_audit("叠3个同名")

# txn：对"叠着"的场景做不了干净快照，先清场再快照
post("/clear", {})
post("/generate", dict(GEN, load=True), 300)
s = post("/txn", {"op": "snapshot", "label": "before-tweak", "name": "澪"})
print("snapshot:", json.dumps({k: s.get(k) for k in ("ok", "id", "bytes")}, ensure_ascii=False))
lst = post("/txn", {"op": "list"})
print("list count=%s ids=%s" % (lst.get("count"), [x["id"] for x in lst.get("snapshots", [])][:4]))
r = post("/txn", {"op": "restore", "id": s.get("id"), "name": "澪"})
print("restore:", json.dumps({k: r.get(k) for k in ("ok", "replaced")}, ensure_ascii=False))
show_audit("回滚后")

# ref_crop 验收：head 那一格应该是头
c = post("/compare", {"ref": "whale_ref", "panel": 0, "view": "front", "framing": "head",
                      "name": "澪", "ref_crop": "head", "file": "CROP_OK.png"}, 300)
print("compare ok=%s montage=%s" % (c.get("ok"), (c.get("montage") or "").split("\\")[-1]))
