# -*- coding: utf-8 -*-
"""终版：以原版人物卡 天真爛漫 为基底（脸型不覆盖，直接用官方成品），
   只改参考图明确要求的东西：发色渐变、女仆装配色、袜子、鞋。
   注意把服装的 4 个颜色通道都写满，否则基座卡的配色会漏出来（上一版领子是青色的就是这个原因）。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

NAVY = "#1E2A55"
gen = {
    "base": "天真爛漫.png", "sex": "female", "save_as": "AI-鲸鱼女仆", "overwrite": True, "load": True,
    "clear_accessories": True, "smooth_skin": True,
    "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
    # 脸型：完全交给官方卡（不做任何 shape 覆盖）
    "face": {"pupil": {"baseColor": "#2E7FD4", "subColor": "#B9ECFF"},
             "lipLineId": 3, "lipLineColor": "#C0707F"},
    "body": {"skinMainColor": "#FFE7D8", "skinGlossPower": 0.28, "detailPower": 0.0, "normalize": True},
    "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
             "front": {"id": 0, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
             "side": {"id": 7, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0}},
    "clothes": {
        "top":   {"id": 218, "color0": NAVY, "color1": "#FFFFFF", "color2": NAVY, "color3": NAVY,
                  "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
        "bot":   {"id": 208, "color0": NAVY, "color1": "#2E4A8C", "color2": NAVY, "color3": NAVY,
                  "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
        "bra":   {"id": 0}, "shorts": {"id": 0}, "gloves": {"id": 0},
        "socks": {"id": 5, "color0": "#FFFFFF", "color1": "#FFFFFF", "color2": "#FFFFFF", "color3": "#FFFFFF", "pattern0": 0},
        "shoes": {"id": 8, "color0": "#1B2249", "color1": "#1B2249", "color2": "#1B2249", "color3": "#1B2249", "pattern0": 0},
    },
}

g = post("/generate", gen, 300)
print("ok=%s verified=%s scene=%s" % (g.get("ok"), g.get("verified"), g.get("scene_chars")))
post("/txn", {"op": "snapshot", "label": "final-officialbase", "name": "澪"})
post("/capture", {"views": ["front", "right", "back"], "framing": "full", "panel_width": 620,
                  "name": "澪", "file": "OK2_views.png"}, 420)
post("/capture", {"views": ["front"], "framing": "face", "panel_width": 800, "name": "澪", "file": "OK2_face.png"}, 300)
post("/capture", {"views": ["front"], "framing": "eyes", "panel_width": 1000, "name": "澪", "file": "OK2_eyes.png"}, 300)
print("captured")
