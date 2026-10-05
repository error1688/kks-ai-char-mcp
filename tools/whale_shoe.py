# -*- coding: utf-8 -*-
"""鞋的候选对比：参考图是深蓝圆头有跟的玛丽珍。
   上一版 21 圆头高跟渲染成灰白（颜色没吃进去），所以逐个试并拍脚部特写。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

NAVY, DEEP, TIP = "#1E2A55", "#16265C", "#86D9F2"
BLUE, SUB = "#3E6FC8", "#BFE8FF"

def gen_with_shoe(sid):
    return {
        "base": "天真爛漫.png", "sex": "female", "save_as": "AI-鞋试", "overwrite": True, "load": True,
        "clear_accessories": True, "smooth_skin": True,
        "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
        "face": {"pupil": {"baseColor": BLUE, "subColor": SUB}},
        "body": {"skinMainColor": "#FFE7D8", "detailPower": 0.0, "normalize": True, "shape": {"BustSize": 0.40}},
        "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": DEEP, "endColor": TIP, "length": 1.0},
                 "front": {"id": 6, "baseColor": "#33529F", "startColor": DEEP, "endColor": TIP, "length": 1.0}},
        "clothes": {
            "top":   {"id": 218, "color0": NAVY, "color1": "#FFFFFF", "color2": NAVY, "color3": NAVY,
                      "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
            "bot":   {"id": 208, "color0": NAVY, "color1": "#2E4A8C", "pattern0": 0},
            "socks": {"id": 17, "color0": "#FFFFFF", "pattern0": 0},
            "shoes": {"id": sid, "color0": "#1B2249", "color1": "#1B2249", "color2": "#1B2249", "color3": "#1B2249",
                      "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
        },
    }

for sid in (1, 5, 8, 21):
    g = post("/generate", gen_with_shoe(sid), 300)
    sk = [x for x in (g.get("skipped") or []) if "shoes" in str(x.get("item", ""))]
    r = post("/capture", {"views": ["front"], "framing": "full", "panel_width": 900, "name": "澪",
                          "file": "SH_%d.png" % sid}, 300)
    print("shoes=%-3d ok=%s cov=%s skipped=%s" % (sid, r.get("ok"), round(r.get("panels",[{}])[0].get("coverage",0),3), sk))
