# -*- coding: utf-8 -*-
"""成品终版：干净场景（单个角色）+ 单调下颌 + 古典女仆A + 吊带袜。拍三视图与面部特写。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

CHIN = {"FaceLowW": 0.42, "ChinW": 0.47, "ChinTipW": 0.50,
        "ChinLowY": 0.30, "ChinLowZ": 0.40, "ChinY": 0.30, "ChinZ": 0.30,
        "ChinTipY": 0.40, "ChinTipZ": 0.20}

gen = {
    "base": "お嬢様.png", "sex": "female", "save_as": "AI-鲸鱼女仆", "overwrite": True, "load": True,
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
    "clothes": {"top": {"id": 218, "color0": "#1E2A55", "color1": "#FFFFFF", "color2": "#1E2A55", "color3": "#1E2A55",
                        "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
                "bot": {"id": 208, "color0": "#1E2A55", "color1": "#2E4A8C", "pattern0": 0},
                "socks": {"id": 5, "color0": "#FFFFFF", "pattern0": 0},
                "shoes": {"id": 8, "color0": "#1B2249", "pattern0": 0}},
}

g = post("/generate", gen, 300)
print("ok=%s verified=%s replaced=%s scene_chars=%s" % (
    g.get("ok"), g.get("verified"), g.get("replaced_same_name"), g.get("scene_chars")))
post("/capture", {"views": ["front", "right", "back"], "framing": "full", "panel_width": 620,
                  "name": "澪", "file": "OK_views.png"}, 420)
post("/capture", {"views": ["front"], "framing": "face", "panel_width": 800, "name": "澪", "file": "OK_face.png"}, 300)
post("/capture", {"views": ["front"], "framing": "eyes", "panel_width": 900, "name": "澪", "file": "OK_eyes.png"}, 300)
print("captured")
