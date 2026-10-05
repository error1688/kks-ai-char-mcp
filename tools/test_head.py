# -*- coding: utf-8 -*-
"""下巴"凹槽/坑洼"根因验证：是低模被滑条推变形，还是本来就这样。
   对照：base(不动脸型) / v3(推滑条) / headId=1 / headId=2 / headId=3
   如果换了 headId 就有平滑圆下巴，那正确做法是换脸型网格，而不是推滑条把低模推成多边形。
"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

V3 = {"FaceLowW": 0.42, "ChinW": 0.47, "ChinTipW": 0.50,
      "ChinLowY": 0.30, "ChinLowZ": 0.40, "ChinY": 0.30, "ChinZ": 0.30,
      "ChinTipY": 0.40, "ChinTipZ": 0.20}

def run(tag, shape=None, head=None):
    face = {"pupil": {"id": 0, "baseColor": "#2E7FD4", "subColor": "#B9ECFF"},
            "lipLineId": 3, "lipLineColor": "#C0707F"}
    if shape is not None: face["shape"] = shape
    if head is not None: face["headId"] = head
    g = {
        "base": "お嬢様.png", "sex": "female", "save_as": "AI-脸型", "overwrite": True, "load": True,
        "clear_accessories": True, "smooth_skin": True,
        "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
        "face": face,
        "body": {"skinMainColor": "#FFE7D8", "skinGlossPower": 0.28, "detailPower": 0.0,
                 "shape": {"BustSize": 0.38}, "normalize": True},
        "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
                 "front": {"id": 0, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
                 "side": {"id": 7, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0}},
        "clothes": {"top": {"id": 218, "color0": "#1E2A55", "color1": "#FFFFFF", "pattern0": 0},
                    "bot": {"id": 208, "color0": "#1E2A55", "color1": "#2E4A8C", "pattern0": 0}},
    }
    r = post("/generate", g, 300)
    sk = [s for s in (r.get("skipped") or []) if "headId" in str(s.get("item", ""))]
    post("/capture", {"views": ["front"], "framing": "eyes", "panel_width": 1000, "name": "澪",
                      "file": "HD_%s.png" % tag}, 300)
    print("%-10s gen=%s head_skipped=%s" % (tag, r.get("ok"), sk))

run("base")
run("v3", V3)
for h in (1, 2, 3):
    run("head%d" % h, None, h)
