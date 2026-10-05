# -*- coding: utf-8 -*-
"""下巴矫正 + 腿部替代方案：
   下巴：基座卡把"下脸宽度/下巴尖宽度"拉到了最小值(FaceLowW=0, ChinTipW=0) → 尖下巴 + 硬下颌线。
   腿部：panst(连裤袜) 在本机不渲染（实测 socks 能渲染），改用 socks 吊带袜覆盖大腿。
   拍：面部特写 + 全身（腿），然后人肉看图。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

# 只覆盖下巴/下颌相关滑条（其余继承基座卡），一次只动这一组，便于判断效果
CHIN = {
    "FaceBaseW": 0.40, "FaceLowW": 0.45, "ChinW": 0.50, "ChinTipW": 0.45,
    "ChinY": 0.25, "ChinLowY": 0.25, "ChinTipY": 0.34,
    "CheekW": 0.46, "CheekBoneW": 0.34,
}

gen = {
    "base": "お嬢様.png", "sex": "female", "save_as": "AI-鲸鱼女仆2", "overwrite": True, "load": True,
    "clear_accessories": True, "smooth_skin": True,
    "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
    "face": {"pupil": {"id": 0, "baseColor": "#2E7FD4", "subColor": "#B9ECFF"},
             "lipLineId": 3, "lipLineColor": "#C0707F", "shape": CHIN},
    "body": {"skinMainColor": "#FFE7D8", "skinGlossPower": 0.28, "detailPower": 0.0,
             "shape": {"BustSize": 0.38, "WaistLowW": 0.45, "Hip": 0.5, "ThighUpW": 0.45, "ArmUpW": 0.45},
             "normalize": True},
    "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
             "front": {"id": 0, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
             "side": {"id": 7, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0}},
    "clothes": {"top": {"id": 218, "color0": "#1E2A55", "color1": "#FFFFFF", "color2": "#1E2A55", "color3": "#1E2A55",
                        "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
                "bot": {"id": 208, "color0": "#1E2A55", "color1": "#2E4A8C", "pattern0": 0},
                "socks": {"id": 5, "color0": "#FFFFFF", "pattern0": 0},
                "shoes": {"id": 8, "color0": "#1B2249", "pattern0": 0}},
}

g = post("/generate", gen, 300)
print("生成 ok=%s verified=%s" % (g.get("ok"), g.get("verified")))
sk = [x for x in g.get("skipped", [])]
print("skipped:", json.dumps(sk, ensure_ascii=False)[:400])
face_shape = [x for x in g.get("applied", []) if "shape" in str(x.get("item", ""))]
print("shape 写入:", json.dumps(face_shape, ensure_ascii=False)[:400])

for fname, opt in [("CHIN_face.png", {"views": ["front"], "framing": "face", "panel_width": 800}),
                   ("CHIN_full.png", {"views": ["front"], "framing": "full", "panel_width": 1080})]:
    r = post("/capture", dict(opt, name="澪", file=fname), 300)
    print("%-14s ok=%s settled=%s" % (fname, r.get("ok"), r.get("settled")))
