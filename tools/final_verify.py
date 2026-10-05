# -*- coding: utf-8 -*-
"""终验：按搜索得到的最优参数生成成品卡，拍三视图 + 面部/胸口/躯干特写。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(path, body, timeout=300):
    req = urllib.request.Request(BASE + path, data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
                                 headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(req, timeout=timeout).read().decode("utf-8"))

final = {
    "base": "お嬢様.png", "sex": "female", "save_as": "AI-鲸鱼女仆", "overwrite": True, "load": True,
    "clear_accessories": True, "smooth_skin": True,
    "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
    "face": {"pupil": {"id": 0, "baseColor": "#2E7FD4", "subColor": "#B9ECFF"},
             "lipLineId": 3, "lipLineColor": "#C0707F"},
    "body": {"skinMainColor": "#FFE7D8", "skinGlossPower": 0.28, "detailPower": 0.0,
             "shape": {"BustSize": 0.38, "WaistLowW": 0.45, "Hip": 0.5, "ThighUpW": 0.45, "ArmUpW": 0.45},
             "normalize": True},
    "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
             "front": {"id": 0, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
             "side": {"id": 7, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0}},
    "clothes": {"top": {"id": 218, "color0": "#1E2A55", "color1": "#FFFFFF", "color2": "#1E2A55", "color3": "#1E2A55",
                        "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
                "bot": {"id": 208, "color0": "#1E2A55", "color1": "#2E4A8C", "pattern0": 0},
                "panst": {"id": 1, "color0": "#F7FAFF"}, "shoes": {"id": 8, "color0": "#1B2249", "pattern0": 0}},
}

g = post("/generate", final, 300)
print("生成 ok=%s verified=%s saved=%s" % (g.get("ok"), g.get("verified"), (g.get("saved") or "").split("\\")[-1]))

shots = [
    ("FINAL2_views.png", {"views": ["front", "right", "back"], "framing": "full", "panel_width": 620}),
    ("FINAL2_face.png",  {"views": ["front"], "framing": "face", "panel_width": 800}),
    ("FINAL2_bust.png",  {"views": ["front"], "framing": "bust", "panel_width": 800}),
    ("FINAL2_torso.png", {"views": ["front"], "framing": "torso", "panel_width": 800}),
]
for fname, opt in shots:
    r = post("/capture", dict(opt, name="澪", file=fname), 300)
    print("%-18s ok=%s settled=%s frozen=%s cover=%s warn=%s" % (
        fname, r.get("ok"), r.get("settled"), r.get("frozen_dynamics"),
        r.get("coverage"), r.get("warnings")))
