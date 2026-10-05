# -*- coding: utf-8 -*-
"""面部参数复核（用户反馈：脸坑坑洼洼、下巴与太阳穴有问题）
   1) 先确认渲染质量：MSAA 修好后同取景再拍一张，和修前对比
   2) 三种脸型对照：base(不动) / v3(宽+深浅都改) / width-only(只改宽度，深浅保持基座卡)
      受控同取景，放大看下巴与太阳穴有没有凹坑
"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

V3 = {"FaceLowW": 0.42, "ChinW": 0.47, "ChinTipW": 0.50,
      "ChinLowY": 0.30, "ChinLowZ": 0.40, "ChinY": 0.30, "ChinZ": 0.30,
      "ChinTipY": 0.40, "ChinTipZ": 0.20}
WIDTHONLY = {"FaceLowW": 0.42, "ChinW": 0.47, "ChinTipW": 0.50}

def run(tag, shape):
    g = {
        "base": "お嬢様.png", "sex": "female", "save_as": "AI-脸试", "overwrite": True, "load": True,
        "clear_accessories": True, "smooth_skin": True,
        "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
        "face": {"pupil": {"id": 0, "baseColor": "#2E7FD4", "subColor": "#B9ECFF"},
                 "lipLineId": 3, "lipLineColor": "#C0707F"},
        "body": {"skinMainColor": "#FFE7D8", "skinGlossPower": 0.28, "detailPower": 0.0,
                 "shape": {"BustSize": 0.38}, "normalize": True},
        "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
                 "front": {"id": 0, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
                 "side": {"id": 7, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0}},
        "clothes": {"top": {"id": 218, "color0": "#1E2A55", "color1": "#FFFFFF", "pattern0": 0},
                    "bot": {"id": 208, "color0": "#1E2A55", "color1": "#2E4A8C", "pattern0": 0}},
    }
    if shape is not None:
        g["face"]["shape"] = shape
    r = post("/generate", g, 300)
    print(tag, "gen=", r.get("ok"), "scene=", r.get("scene_chars"))
    post("/capture", {"views": ["front"], "framing": "face", "panel_width": 900, "name": "澪",
                      "file": "FA_%s.png" % tag}, 300)
    print(tag, "captured")

run("base", None)
run("v3", V3)
run("widthonly", WIDTHONLY)
