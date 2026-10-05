# -*- coding: utf-8 -*-
"""按用户建议：直接用原版人物卡当基底，而不是自己推滑条。
   A = お嬢様 + 我推的下巴滑条（ChinTipW 0.5 —— 原版 44 张里全是 0）
   B = 天真爛漫 原版脸型，完全不覆盖 face.shape
   同取景对比下颌轮廓。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

V3 = {"FaceLowW": 0.42, "ChinW": 0.47, "ChinTipW": 0.50,
      "ChinLowY": 0.30, "ChinLowZ": 0.40, "ChinY": 0.30, "ChinZ": 0.30,
      "ChinTipY": 0.40, "ChinTipZ": 0.20}

def run(tag, base, shape):
    face = {"pupil": {"id": 0, "baseColor": "#2E7FD4", "subColor": "#B9ECFF"},
            "lipLineId": 3, "lipLineColor": "#C0707F"}
    if shape is not None: face["shape"] = shape
    g = {
        "base": base, "sex": "female", "save_as": "AI-基试", "overwrite": True, "load": True,
        "clear_accessories": True, "smooth_skin": True,
        "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
        "face": face,
        "body": {"skinMainColor": "#FFE7D8", "skinGlossPower": 0.28, "detailPower": 0.0, "normalize": True},
        "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
                 "front": {"id": 0, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
                 "side": {"id": 7, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0}},
        "clothes": {"top": {"id": 218, "color0": "#1E2A55", "color1": "#FFFFFF", "pattern0": 0},
                    "bot": {"id": 208, "color0": "#1E2A55", "color1": "#2E4A8C", "pattern0": 0},
                    "socks": {"id": 5, "color0": "#FFFFFF", "pattern0": 0},
                    "shoes": {"id": 8, "color0": "#1B2249", "pattern0": 0}},
    }
    r = post("/generate", g, 300)
    print("%-12s gen=%s" % (tag, r.get("ok")))
    post("/capture", {"views": ["front"], "framing": "eyes", "panel_width": 1000, "name": "澪",
                      "file": "BA_%s.png" % tag}, 300)
    print(tag, "captured")

run("ojou_v3", "お嬢様.png", V3)
run("tenranman", "天真爛漫.png", None)
