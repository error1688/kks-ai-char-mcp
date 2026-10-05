# -*- coding: utf-8 -*-
"""鲸鱼女仆 v1：基底用官方卡 天真爛漫（圆脸，不覆盖 face.shape），
   只改参考图明确要求的项：波浪长发渐变 / 女仆装 / 白袜 / 玛丽珍鞋 / 头饰 / 鳍耳 / 龙尾。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

NAVY = "#1E2A55"        # 主深蓝
DEEP = "#16265C"        # 发根
TIP  = "#86D9F2"        # 发梢
BLUE = "#3E6FC8"        # 蝴蝶结/瞳色
PUPIL_SUB = "#BFE8FF"

gen = {
    "base": "天真爛漫.png", "sex": "female",
    "save_as": "AI-鲸鱼女仆", "overwrite": True, "load": True,
    "clear_accessories": True, "smooth_skin": True,
    "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
    "face": {"pupil": {"baseColor": BLUE, "subColor": PUPIL_SUB},
             "lipLineId": 3, "lipLineColor": "#C0707F"},
    "body": {"skinMainColor": "#FFE7D8", "skinGlossPower": 0.28, "detailPower": 0.0, "normalize": True,
             "shape": {"BustSize": 0.40}},
    "hair": {
        "back":   {"id": 45,  "baseColor": "#33529F", "startColor": DEEP, "endColor": TIP, "length": 1.0},
        "front":  {"id": 6,   "baseColor": "#33529F", "startColor": DEEP, "endColor": TIP, "length": 1.0},
        "side":   {"id": 7,   "baseColor": "#33529F", "startColor": DEEP, "endColor": TIP, "length": 1.0},
        "option": {"id": 7,   "baseColor": "#33529F", "startColor": DEEP, "endColor": TIP, "length": 1.0},
    },
    "clothes": {
        "top":    {"id": 218, "color0": NAVY, "color1": "#FFFFFF", "color2": NAVY, "color3": NAVY,
                   "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
        "bot":    {"id": 208, "color0": NAVY, "color1": "#2E4A8C", "color2": NAVY, "color3": NAVY,
                   "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
        "socks":  {"id": 17,  "color0": "#FFFFFF", "color1": "#FFFFFF", "color2": "#FFFFFF", "color3": "#FFFFFF", "pattern0": 0},
        "shoes":  {"id": 21,  "color0": "#1B2249", "color1": "#1B2249", "color2": "#1B2249", "color3": "#1B2249", "pattern0": 0},
    },
    "accessories": [
        {"type": "hair", "id": 38,  "colors": ["#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF"]},   # 女仆头带
        {"type": "hair", "id": 0,   "colors": [BLUE, BLUE, BLUE, BLUE]},                         # 丝带（右侧）
        {"type": "hair", "id": 313, "colors": [BLUE, "#7EC8E8", BLUE, BLUE]},                    # サイバーフィン（鳍）
        {"type": "body", "id": 307, "colors": [NAVY, "#2E4A8C", NAVY, NAVY]},                    # ドラゴンの尻尾（龙尾）
    ],
}

g = post("/generate", gen, 300)
print("ok=%s verified=%s scene=%s" % (g.get("ok"), g.get("verified"), g.get("scene_chars")))
for it in (g.get("applied") or []):
    if any(k in str(it.get("item", "")) for k in ("accessor", "hair", "clothes", "shape")):
        print("   ✓", it.get("item"), "=", str(it.get("value"))[:40])
for it in (g.get("skipped") or []):
    print("   ✗", it.get("item"), "->", it.get("reason"))
