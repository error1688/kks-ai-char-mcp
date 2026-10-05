# -*- coding: utf-8 -*-
"""下巴 + 腰部修正试验。
下巴：上次只推了几个滑条，结果脸颊与下巴之间出现"两个凹槽"（过渡不连续）。
      这次试"把下颌/脸型一整组统一设成中性值"，让过渡连续；并留一版做对照。
腰部：腰部两侧有皮肤从裙装里透出来（身体比衣服宽）。试两组收窄的腰腹滑条。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

# 下颌/脸型一整组（脸颊→下颌→下巴尖），统一取值以保证过渡连续
CHIN_GROUP = ["FaceBaseW", "FaceUpZ", "FaceUpY", "FaceUpSize", "FaceLowZ", "FaceLowW",
              "ChinLowY", "ChinLowZ", "ChinY", "ChinW", "ChinZ",
              "ChinTipY", "ChinTipZ", "ChinTipW", "CheekBoneW", "CheekBoneZ", "CheekW", "CheekZ", "CheekY"]

def chin(v):
    return {k: v for k in CHIN_GROUP}

WAIST = {
    "A": {"WaistLowW": 0.40, "BodyLowW": 0.38, "Belly": 0.35, "BodyLowZ": 0.38, "WaistUpW": 0.42},
    "B": {"WaistLowW": 0.36, "BodyLowW": 0.34, "Belly": 0.32, "BodyLowZ": 0.36, "WaistUpW": 0.40},
}

def gen(fc_shape, waist, tag):
    g = {
        "base": "お嬢様.png", "sex": "female", "save_as": "AI-试", "overwrite": True, "load": True,
        "clear_accessories": True, "smooth_skin": True,
        "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
        "face": {"pupil": {"id": 0, "baseColor": "#2E7FD4", "subColor": "#B9ECFF"},
                 "lipLineId": 3, "lipLineColor": "#C0707F"},
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
    if fc_shape is not None:
        g["face"]["shape"] = fc_shape
    bd = dict(WAIST[waist]); bd["BustSize"] = 0.38; bd["ThighUpW"] = 0.45; bd["ArmUpW"] = 0.45
    g["body"]["shape"] = bd
    post("/generate", g, 300)
    for nm, opt in (("face", {"framing": "face", "panel_width": 800}),
                    ("torso", {"framing": "torso", "panel_width": 800})):
        post("/capture", dict(opt, views=["front"], name="澪", file="V_%s_%s.png" % (tag, nm)), 300)
    print("%s done" % tag)

gen(chin(0.5), "A", "chin50_wA")      # 下颌组统一 0.5 + 腰收窄A
gen(chin(0.58), "B", "chin58_wB")     # 下颌组统一 0.58（更圆）+ 腰收窄B
gen(None, "A", "base_wA")             # 对照组：不动脸型
