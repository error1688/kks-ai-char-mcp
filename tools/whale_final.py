# -*- coding: utf-8 -*-
"""终版：官方卡 天真爛漫 为基底（不覆盖 face.shape）
   发：300 自然波浪长发 + 6 M字刘海 + 7 呆毛
   衣：古典女仆A 218/208（深蓝+白围裙）袜 17 长筒白袜 鞋 21 圆头高跟（玛丽珍）
   饰：head 38 女仆头带(白) / 33 打结丝带(蓝) / 305 狐耳(染蓝，近似鳍耳) / waist 300 狐尾(染蓝，近似鲸尾)"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

NAVY, DEEP, TIP = "#1E2A55", "#16265C", "#86D9F2"
BLUE, SUB = "#3E6FC8", "#BFE8FF"

final = {
    "base": "天真爛漫.png", "sex": "female",
    "save_as": "AI-鲸鱼女仆", "overwrite": True, "load": True,
    "clear_accessories": True, "smooth_skin": True,
    "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
    "face": {"pupil": {"baseColor": BLUE, "subColor": SUB}, "lipLineId": 3, "lipLineColor": "#C0707F"},
    "body": {"skinMainColor": "#FFE7D8", "skinGlossPower": 0.28, "detailPower": 0.0, "normalize": True,
             "shape": {"BustSize": 0.40}},
    "hair": {
        "back":   {"id": 300, "baseColor": "#33529F", "startColor": DEEP, "endColor": TIP, "length": 1.0},
        "front":  {"id": 6,   "baseColor": "#33529F", "startColor": DEEP, "endColor": TIP, "length": 1.0},
        "side":   {"id": 7,   "baseColor": "#33529F", "startColor": DEEP, "endColor": TIP, "length": 1.0},
        "option": {"id": 7,   "baseColor": "#33529F", "startColor": DEEP, "endColor": TIP, "length": 1.0},
    },
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
        {"type": "head",  "id": 38,  "colors": ["#FFFFFF", "#FFFFFF", "#FFFFFF", "#FFFFFF"]},
        {"type": "head",  "id": 33,  "colors": [BLUE, BLUE, BLUE, BLUE]},
        {"type": "head",  "id": 305, "colors": [DEEP, TIP, DEEP, DEEP]},
        {"type": "waist", "id": 300, "colors": [NAVY, "#2E4A8C", NAVY, NAVY]},
    ],
}

g = post("/generate", final, 300)
print("ok=%s verified=%s scene=%s skipped=%s" % (
    g.get("ok"), g.get("verified"), g.get("scene_chars"), [x.get("reason") for x in (g.get("skipped") or [])]))

for nm, opt in (("views", {"views": ["front", "right", "back"], "framing": "full", "panel_width": 620}),
                ("face",  {"views": ["front"], "framing": "face", "panel_width": 800}),
                ("eyes",  {"views": ["front"], "framing": "eyes", "panel_width": 1000})):
    r = post("/capture", dict(opt, name="澪", file="FIN_%s.png" % nm), 400)
    print("%-6s cov=%s warn=%s" % (nm, [round(p.get("coverage",0),3) for p in r.get("panels",[])], r.get("warnings")))

print("快照:", json.dumps(post("/txn", {"op": "snapshot", "label": "whale-final", "name": "澪"}), ensure_ascii=False)[:120])
