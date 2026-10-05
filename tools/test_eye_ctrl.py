# -*- coding: utf-8 -*-
"""受控对比：同一个"眼部特写"取景（锚在头部骨骼，尺度一致），只改脸型开关，看眼睛是否被影响。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

CHIN_V3 = {"FaceLowW": 0.42, "ChinW": 0.47, "ChinTipW": 0.50,
           "ChinLowY": 0.30, "ChinLowZ": 0.40, "ChinY": 0.30, "ChinZ": 0.30,
           "ChinTipY": 0.40, "ChinTipZ": 0.20}

def run(tag, shape):
    g = {
        "base": "お嬢様.png", "sex": "female", "save_as": "AI-眼试", "overwrite": True, "load": True,
        "clear_accessories": True, "smooth_skin": True,
        "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
        "face": {"pupil": {"id": 0, "baseColor": "#2E7FD4", "subColor": "#B9ECFF"},
                 "lipLineId": 3, "lipLineColor": "#C0707F"},
        "body": {"skinMainColor": "#FFE7D8", "shape": {"BustSize": 0.38}, "normalize": True},
        "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
                 "front": {"id": 0, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
                 "side": {"id": 7, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0}},
        "clothes": {"top": {"id": 218, "color0": "#1E2A55", "color1": "#FFFFFF", "pattern0": 0},
                    "bot": {"id": 208, "color0": "#1E2A55", "color1": "#2E4A8C", "pattern0": 0}},
    }
    if shape is not None:
        g["face"]["shape"] = shape
    post("/generate", g, 300)
    post("/capture", {"views": ["front"], "framing": "eyes", "panel_width": 900, "name": "澪",
                      "file": "EYE_%s.png" % tag}, 300)
    print(tag, "done")

run("base", None)
run("v3", CHIN_V3)
