# -*- coding: utf-8 -*-
"""腿部问题定位：同一角色分别穿 panst=1/5/7（连裤袜B/A、厚裤袜A），各拍一张全身高清图，
   随后用 PIL 裁腿部放大对比 —— 看是"裤袜根本没渲染"还是"这个 id 不对"。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

common = {
    "base": "お嬢様.png", "sex": "female", "overwrite": True, "load": True,
    "clear_accessories": True, "smooth_skin": True,
    "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
    "face": {"pupil": {"id": 0, "baseColor": "#2E7FD4", "subColor": "#B9ECFF"},
             "lipLineId": 3, "lipLineColor": "#C0707F"},
    "body": {"skinMainColor": "#FFE7D8", "skinGlossPower": 0.28, "detailPower": 0.0,
             "shape": {"BustSize": 0.38, "WaistLowW": 0.45, "Hip": 0.5, "ThighUpW": 0.45, "ArmUpW": 0.45},
             "normalize": True},
    "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
             "front": {"id": 0, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
             "side": {"id": 7, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0}},
    "clothes": {"top": {"id": 218, "color0": "#1E2A55", "color1": "#FFFFFF", "color2": "#1E2A55", "color3": "#1E2A55",
                        "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
                "bot": {"id": 208, "color0": "#1E2A55", "color1": "#2E4A8C", "pattern0": 0},
                "shoes": {"id": 8, "color0": "#1B2249", "pattern0": 0}},
}

for pid in [0, 1, 5, 7]:
    cl = dict(common["clothes"])
    cl["panst"] = {"id": pid, "color0": "#FFFFFF", "pattern0": 0}
    g = post("/generate", dict(common, clothes=cl, save_as="AI-腿测", overwrite=True), 300)
    r = post("/capture", {"views": ["front"], "framing": "full", "panel_width": 1080,
                          "name": "澪", "file": "LEG_%d.png" % pid}, 300)
    print("panst=%-2d gen_ok=%s cap_ok=%s settled=%s" % (pid, g.get("ok"), r.get("ok"), r.get("settled")))
