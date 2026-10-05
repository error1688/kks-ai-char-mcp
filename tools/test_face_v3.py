# -*- coding: utf-8 -*-
"""第三版：保留基座卡妆容 id，只改颜色 + 加唇线 + 中分刘海；规范化体型；平滑皮肤。
拍 face / torso 两张特写用于人眼验收。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(path, body, timeout=200):
    req = urllib.request.Request(BASE + path, data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
                                 headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(req, timeout=timeout).read().decode("utf-8"))

gen = {
  "sex": "female", "base": "お嬢様.png", "save_as": "AI-鲸鱼女仆", "overwrite": True, "load": True,
  "clear_accessories": True, "smooth_skin": True,
  "parameter": {"fullname": "汐见 澪", "nickname": "澪", "personality": 1},
  "face": {
    # 不覆盖 pupil.id / hlUpId / eyebrowId —— 保留基座卡原本自然的妆容造型，只调颜色
    "pupil": {"baseColor": "#2E7FD4", "subColor": "#B9ECFF"},
    "eyebrowColor": "#33406B",
    "eyelineColor": "#1E2A4A",
    "lipLineId": 3,                 # 给嘴唇加轮廓（基座卡这里是 0，嘴几乎看不见）
    "lipLineColor": "#C0707F"
  },
  "body": {"skinMainColor": "#FFE7D8", "skinSubColor": "#FFD9C4", "skinGlossPower": 0.28,
           "detailPower": 0.0, "drawAddLine": False, "areolaSize": 0.55,
           "shape": {"BustSize": 0.38, "BustX": 0.5, "BustY": 0.5, "BustForm": 0.5, "BustSharp": 0.5,
                     "WaistLowW": 0.45, "WaistUpW": 0.5, "Hip": 0.5, "ThighUpW": 0.45, "ArmUpW": 0.45,
                     "AreolaBulge": 0.35, "NipWeight": 0.3, "NipStand": 0.3},
           "normalize": True},
  "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
           "front": {"id": 39, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
           "side": {"id": 7, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0}},
  "clothes": {"top": {"id": 218, "color0": "#1E2A55", "color1": "#FFFFFF", "color2": "#1E2A55"},
              "bot": {"id": 208, "color0": "#1E2A55", "color1": "#2E4A8C"},
              "panst": {"id": 1, "color0": "#F7FAFF"}, "shoes": {"id": 8, "color0": "#1B2249"}}
}

g = post("/generate", gen, 120)
print("gen ok=%s verified=%s" % (g.get("ok"), g.get("verified")))
for s in g.get("skipped", []):
    if "shape" in s["item"] or "lipLine" in s["item"]:
        print("  SKIP", s["item"], "::", s["reason"])

for f, framing in [("v3_face.png", "face"), ("v3_torso.png", "torso"), ("v3_full.png", "full")]:
    r = post("/capture", {"views": ["front"], "framing": framing, "panel_width": 800 if framing != "full" else 560,
                          "file": f, "name": "澪"}, 150)
    cov = r["panels"][0]["coverage"] if r.get("panels") else -1
    print("  %-14s ok=%s settled=%s coverage=%.3f" % (f, r.get("ok"), r.get("settled"), cov))
