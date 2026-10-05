# -*- coding: utf-8 -*-
"""v3：定鞋=21 圆头高跟；头饰精简为（女仆头带38 白 + 打结丝带33 蓝）；
   耳用 305 狐耳染蓝近似参考图的鳍耳；尾巴两版对比（307 龙尾 / 300 狐尾）。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

NAVY, DEEP, TIP = "#1E2A55", "#16265C", "#86D9F2"
BLUE, SUB = "#3E6FC8", "#BFE8FF"

def build(tail_id, tag):
    return {
        "base": "天真爛漫.png", "sex": "female", "save_as": "AI-尾试", "overwrite": True, "load": True,
        "clear_accessories": True, "smooth_skin": True,
        "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
        "face": {"pupil": {"baseColor": BLUE, "subColor": SUB}, "lipLineId": 3, "lipLineColor": "#C0707F"},
        "body": {"skinMainColor": "#FFE7D8", "skinGlossPower": 0.28, "detailPower": 0.0, "normalize": True,
                 "shape": {"BustSize": 0.40}},
        "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": DEEP, "endColor": TIP, "length": 1.0},
                 "front": {"id": 6, "baseColor": "#33529F", "startColor": DEEP, "endColor": TIP, "length": 1.0},
                 "side": {"id": 7, "baseColor": "#33529F", "startColor": DEEP, "endColor": TIP, "length": 1.0},
                 "option": {"id": 7, "baseColor": "#33529F", "startColor": DEEP, "endColor": TIP, "length": 1.0}},
        "clothes": {
            "top":   {"id": 218, "color0": NAVY, "color1": "#FFFFFF", "color2": NAVY, "color3": NAVY,
                      "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
            "bot":   {"id": 208, "color0": NAVY, "color1": "#2E4A8C", "color2": NAVY, "color3": NAVY,
                      "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
            "socks": {"id": 17, "color0": "#FFFFFF", "color1": "#FFFFFF", "color2": "#FFFFFF", "color3": "#FFFFFF", "pattern0": 0},
            "shoes": {"id": 21, "color0": "#1B2249", "color1": "#1B2249", "color2": "#1B2249", "color3": "#1B2249",
                      "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
        },
        "accessories": [
            {"type": "head",  "id": 38, "colors": ["#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF"]},   # 女仆头带
            {"type": "head",  "id": 33, "colors": [BLUE, BLUE, BLUE, BLUE]},                        # 打结丝带
            {"type": "head",  "id": 305, "colors": [DEEP, TIP, DEEP, DEEP]},                        # 狐耳（染蓝）
            {"type": "waist", "id": tail_id, "colors": [NAVY, "#2E4A8C", NAVY, NAVY]},              # 尾巴
        ],
    }

for tid, tag in ((307, "dragon"), (300, "fox")):
    g = post("/generate", build(tid, tag), 300)
    print("%-7s gen=%s skipped=%s" % (tag, g.get("ok"), [x.get("reason") for x in (g.get("skipped") or [])][:3]))
    post("/capture", {"views": ["front", "right"], "framing": "full", "panel_width": 700, "name": "澪",
                      "file": "T_%s.png" % tag}, 300)
    print(tag, "captured")
