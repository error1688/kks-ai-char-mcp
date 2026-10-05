# -*- coding: utf-8 -*-
"""下巴精修 v3：只动"下颌→下巴尖"这一串，且取单调递增，避免脸颊与下巴之间出现凹槽。
   关键：不碰 FaceUp*（会把眼睛/额头往上挪，上版就是因此把眼睑压厚、眉毛看着变粗）与 Cheek*。
   顺带试收紧腰腹（腰部两侧有皮肤从衣服缝里透出来）。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

# 基座卡：FaceLowW=0.000 ChinW=0.286 ChinTipW=0.000（最尖最窄）→ 往上抬，但保持单调递增
CHIN = {"FaceLowW": 0.42, "ChinW": 0.47, "ChinTipW": 0.50,
        "ChinLowY": 0.30, "ChinLowZ": 0.40, "ChinY": 0.30, "ChinZ": 0.30,
        "ChinTipY": 0.40, "ChinTipZ": 0.20}

WAISTS = {
    "w0": {"WaistUpW": 0.42, "WaistLowW": 0.40, "BodyLowW": 0.38, "BodyLowZ": 0.38, "Belly": 0.35},
    "w1": {"WaistUpW": 0.34, "WaistLowW": 0.34, "BodyLowW": 0.33, "BodyLowZ": 0.36, "Belly": 0.30},
}

def run(tag, waist):
    g = {
        "base": "お嬢様.png", "sex": "female", "save_as": "AI-试", "overwrite": True, "load": True,
        "clear_accessories": True, "smooth_skin": True,
        "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
        "face": {"pupil": {"id": 0, "baseColor": "#2E7FD4", "subColor": "#B9ECFF"},
                 "lipLineId": 3, "lipLineColor": "#C0707F", "shape": CHIN},
        "body": {"skinMainColor": "#FFE7D8", "skinGlossPower": 0.28, "detailPower": 0.0, "normalize": True},
        "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
                 "front": {"id": 0, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
                 "side": {"id": 7, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0}},
        "clothes": {"top": {"id": 218, "color0": "#1E2A55", "color1": "#FFFFFF", "color2": "#1E2A55", "color3": "#1E2A55",
                            "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
                    "bot": {"id": 208, "color0": "#1E2A55", "color1": "#2E4A8C", "pattern0": 0},
                    "socks": {"id": 5, "color0": "#FFFFFF", "pattern0": 0},
                    "shoes": {"id": 8, "color0": "#1B2249", "pattern0": 0}},
    }
    bd = dict(WAISTS[waist]); bd["BustSize"] = 0.38; bd["Hip"] = 0.5; bd["ThighUpW"] = 0.45; bd["ArmUpW"] = 0.45
    g["body"]["shape"] = bd
    post("/generate", g, 300)
    for nm, opt in (("face", {"framing": "face", "panel_width": 800}),
                    ("full", {"framing": "full", "panel_width": 1080})):
        post("/capture", dict(opt, views=["front"], name="澪", file="W3_%s_%s.png" % (tag, nm)), 300)
    print(tag, "done")

run("chinv3_w0", "w0")
run("chinv3_w1", "w1")
