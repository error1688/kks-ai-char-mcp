# -*- coding: utf-8 -*-
"""干净场景复验：确认场景里只有一个角色（scene_chars 应为 1），再比较两套女仆装的腰部。
   之前的"腰上有皮肤三角"很可能不是穿模，而是十几个同名副本叠在原点互相穿插。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

def get(p, t=120):
    return json.loads(urllib.request.urlopen(BASE + p, timeout=t).read().decode())

CHIN = {"FaceLowW": 0.42, "ChinW": 0.47, "ChinTipW": 0.50,
        "ChinLowY": 0.30, "ChinLowZ": 0.40, "ChinY": 0.30, "ChinZ": 0.30,
        "ChinTipY": 0.40, "ChinTipZ": 0.20}

def base_gen(outfit):
    cl = dict(outfit)
    cl["socks"] = {"id": 5, "color0": "#FFFFFF", "pattern0": 0}
    cl["shoes"] = {"id": 8, "color0": "#1B2249", "pattern0": 0}
    return {
        "base": "お嬢様.png", "sex": "female", "save_as": "AI-鲸鱼女仆", "overwrite": True, "load": True,
        "clear_accessories": True, "smooth_skin": True,
        "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
        "face": {"pupil": {"id": 0, "baseColor": "#2E7FD4", "subColor": "#B9ECFF"},
                 "lipLineId": 3, "lipLineColor": "#C0707F", "shape": CHIN},
        "body": {"skinMainColor": "#FFE7D8", "skinGlossPower": 0.28, "detailPower": 0.0, "normalize": True,
                 "shape": {"BustSize": 0.38, "WaistUpW": 0.42, "WaistLowW": 0.40, "BodyLowW": 0.38,
                           "BodyLowZ": 0.38, "Belly": 0.35, "Hip": 0.5, "ThighUpW": 0.45, "ArmUpW": 0.45}},
        "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
                 "front": {"id": 0, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
                 "side": {"id": 7, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0}},
        "clothes": cl,
    }

OUT = {
    "cls218": {"top": {"id": 218, "color0": "#1E2A55", "color1": "#FFFFFF", "color2": "#1E2A55", "color3": "#1E2A55",
                       "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
               "bot": {"id": 208, "color0": "#1E2A55", "color1": "#2E4A8C", "pattern0": 0}},
    "maid37": {"top": {"id": 37, "color0": "#1E2A55", "color1": "#FFFFFF", "color2": "#FFFFFF", "color3": "#1E2A55",
                       "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
               "bot": {"id": 27, "color0": "#1E2A55", "color1": "#FFFFFF", "pattern0": 0}},
}

print("清场:", json.dumps(post("/clear", {}), ensure_ascii=False))
for tag, out in OUT.items():
    g = post("/generate", base_gen(out), 300)
    print("%-8s replaced=%s scene_chars=%s" % (tag, g.get("replaced_same_name"), g.get("scene_chars")))
    post("/capture", {"views": ["front"], "framing": "full", "panel_width": 1080, "name": "澪",
                      "file": "CLN_%s.png" % tag}, 300)
    post("/capture", {"views": ["front"], "framing": "eyes", "panel_width": 900, "name": "澪",
                      "file": "CLN_%s_eye.png" % tag}, 300)
print("final scene_chars:", get("/live?name=%E6%BE%AA").get("clothes_parts") is not None)
