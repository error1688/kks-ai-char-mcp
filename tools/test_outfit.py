# -*- coding: utf-8 -*-
"""腰部来源排查：怀疑"白围裙"是独立网格，和上衣在腰部有覆盖缝（收窄身体没用，因为衣服随身体一起缩）。
   试三套女仆装，看腰部哪套干净。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

CHIN = {"FaceLowW": 0.42, "ChinW": 0.47, "ChinTipW": 0.50,
        "ChinLowY": 0.30, "ChinLowZ": 0.40, "ChinY": 0.30, "ChinZ": 0.30,
        "ChinTipY": 0.40, "ChinTipZ": 0.20}

OUTFITS = {
    "cls218": {"top": {"id": 218, "color0": "#1E2A55", "color1": "#FFFFFF", "color2": "#1E2A55", "color3": "#1E2A55",
                       "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
               "bot": {"id": 208, "color0": "#1E2A55", "color1": "#2E4A8C", "pattern0": 0}},
    "clsB":   {"top": {"id": 219, "color0": "#1E2A55", "color1": "#FFFFFF", "color2": "#1E2A55", "color3": "#1E2A55",
                       "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
               "bot": {"id": 209, "color0": "#1E2A55", "color1": "#2E4A8C", "pattern0": 0}},
    "maid37": {"top": {"id": 37, "color0": "#1E2A55", "color1": "#FFFFFF", "color2": "#FFFFFF", "color3": "#1E2A55",
                       "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
               "bot": {"id": 27, "color0": "#1E2A55", "color1": "#FFFFFF", "pattern0": 0}},
}

def run(tag, outfit):
    cl = dict(outfit)
    cl["socks"] = {"id": 5, "color0": "#FFFFFF", "pattern0": 0}
    cl["shoes"] = {"id": 8, "color0": "#1B2249", "pattern0": 0}
    g = {
        "base": "お嬢様.png", "sex": "female", "save_as": "AI-装试", "overwrite": True, "load": True,
        "clear_accessories": True, "smooth_skin": True,
        "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
        "face": {"pupil": {"id": 0, "baseColor": "#2E7FD4", "subColor": "#B9ECFF"},
                 "lipLineId": 3, "lipLineColor": "#C0707F", "shape": CHIN},
        "body": {"skinMainColor": "#FFE7D8", "skinGlossPower": 0.28, "detailPower": 0.0, "normalize": True,
                 "shape": {"BustSize": 0.38, "WaistUpW": 0.42, "WaistLowW": 0.40, "BodyLowW": 0.38,
                           "BodyLowZ": 0.38, "Belly": 0.35, "Hip": 0.5, "ThighUpW": 0.45, "ArmUpW": 0.45}},
        "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
                 "front": {"id": 0, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
                 "side": {"id": 7, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0}},
        "clothes": cl,
    }
    g2 = post("/generate", g, 300)
    print(tag, "gen=", g2.get("ok"), "skipped=", json.dumps(g2.get("skipped"), ensure_ascii=False)[:160])
    post("/capture", {"views": ["front"], "framing": "full", "panel_width": 1080, "name": "澪",
                      "file": "OUT_%s.png" % tag}, 300)
    print(tag, "captured")

for k, v in OUTFITS.items():
    run(k, v)
