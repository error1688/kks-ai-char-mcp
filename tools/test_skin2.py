# -*- coding: utf-8 -*-
"""脸颊那两条淡线来自哪里：smooth_skin 会把面部 detailPower 归零，
   若基座卡靠细节贴图柔化"眼睑—脸颊"过渡，归零后可能露出硬边。对照 A/B。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

CHIN = {"FaceLowW": 0.42, "ChinW": 0.47, "ChinTipW": 0.50,
        "ChinLowY": 0.30, "ChinLowZ": 0.40, "ChinY": 0.30, "ChinZ": 0.30,
        "ChinTipY": 0.40, "ChinTipZ": 0.20}

def run(tag, smooth, detail):
    g = {
        "base": "お嬢様.png", "sex": "female", "save_as": "AI-肤试", "overwrite": True, "load": True,
        "clear_accessories": True,
        "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
        "face": {"pupil": {"id": 0, "baseColor": "#2E7FD4", "subColor": "#B9ECFF"},
                 "lipLineId": 3, "lipLineColor": "#C0707F", "shape": CHIN,
                 "detailPower": detail},
        "body": {"skinMainColor": "#FFE7D8", "skinGlossPower": 0.28, "shape": {"BustSize": 0.38}, "normalize": True},
        "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
                 "front": {"id": 0, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
                 "side": {"id": 7, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0}},
        "clothes": {"top": {"id": 37, "color0": "#1E2A55", "color1": "#FFFFFF", "pattern0": 0},
                    "bot": {"id": 27, "color0": "#1E2A55", "color1": "#FFFFFF", "pattern0": 0},
                    "socks": {"id": 5, "color0": "#FFFFFF", "pattern0": 0},
                    "shoes": {"id": 8, "color0": "#1B2249", "pattern0": 0}},
    }
    if smooth:
        g["smooth_skin"] = True
    r = post("/generate", g, 300)
    print(tag, "ok=", r.get("ok"), "skipped=", json.dumps(r.get("skipped"), ensure_ascii=False)[:150])
    post("/capture", {"views": ["front"], "framing": "eyes", "panel_width": 900, "name": "澪",
                      "file": "SK_%s.png" % tag}, 300)

run("smooth", True, None)         # smooth_skin: 面部 detailPower 归零
run("rawdetail", False, 0.5)      # 保留面部细节贴图
