# -*- coding: utf-8 -*-
"""候选成品：下颌组统一 0.5（去掉凹槽）+ 腰腹收窄；拍全身/腰/脸三张核对。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

CHIN_GROUP = ["FaceBaseW", "FaceUpZ", "FaceUpY", "FaceUpSize", "FaceLowZ", "FaceLowW",
              "ChinLowY", "ChinLowZ", "ChinY", "ChinW", "ChinZ",
              "ChinTipY", "ChinTipZ", "ChinTipW", "CheekBoneW", "CheekBoneZ", "CheekW", "CheekZ", "CheekY"]
CHIN = {k: 0.5 for k in CHIN_GROUP}

gen = {
    "base": "お嬢様.png", "sex": "female", "save_as": "AI-鲸鱼女仆", "overwrite": True, "load": True,
    "clear_accessories": True, "smooth_skin": True,
    "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
    "face": {"pupil": {"id": 0, "baseColor": "#2E7FD4", "subColor": "#B9ECFF"},
             "lipLineId": 3, "lipLineColor": "#C0707F", "shape": CHIN},
    "body": {"skinMainColor": "#FFE7D8", "skinGlossPower": 0.28, "detailPower": 0.0, "normalize": True,
             "shape": {"BustSize": 0.38, "WaistLowW": 0.40, "WaistUpW": 0.42, "BodyLowW": 0.38,
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
print("ok=%s verified=%s skipped=%s" % (g.get("ok"), g.get("verified"), json.dumps(g.get("skipped"), ensure_ascii=False)[:200]))
for nm, opt in (("full", {"framing": "full", "panel_width": 1080}),
                ("bust", {"framing": "bust", "panel_width": 900}),
                ("face", {"framing": "face", "panel_width": 800})):
    r = post("/capture", dict(opt, views=["front"], name="澪", file="FIX_%s.png" % nm), 300)
    print("%-5s ok=%s settled=%s" % (nm, r.get("ok"), r.get("settled")))
