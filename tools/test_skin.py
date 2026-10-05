# -*- coding: utf-8 -*-
"""修参数（smooth_skin + 规范体型）并用高分辨率局部特写验证皮肤与穿模"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(path, body, timeout=180):
    req = urllib.request.Request(BASE + path, data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
                                 headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(req, timeout=timeout).read().decode("utf-8"))

gen = {
  "sex": "female", "base": "お嬢様.png", "save_as": "AI-鲸鱼女仆", "overwrite": True, "load": True,
  "clear_accessories": True,
  "smooth_skin": True,                      # 关掉 detailPower/drawAddLine/涂装/晒痕，收敛光泽
  "parameter": {"fullname": "汐见 澪", "nickname": "澪", "personality": 1},
  "face": {"pupil": {"id": 0, "baseColor": "#2E7FD4", "subColor": "#9FE4FF"}, "eyebrowId": 1,
           "eyebrowColor": "#2A3A6E", "eyelineColor": "#20305E", "lipLineColor": "#B4687A",
           "hlUpId": 2, "hlUpColor": "#FFFFFF"},
  "body": {"skinMainColor": "#FFE7D8", "skinSubColor": "#FFD9C4",
           "skinGlossPower": 0.28, "bustSoftness": 0.45, "bustWeight": 0.45,
           "detailId": 0, "detailPower": 0.0, "drawAddLine": False,
           # 按名字规范体型：把容易穿模的滑条拉回中间区，别让身体顶出衣服
           "shape": {"BustSize": 0.35, "BustX": 0.5, "BustY": 0.5, "BustForm": 0.5,
                     "WaistLowW": 0.45, "WaistUpW": 0.5, "Hip": 0.5,
                     "ThighUpW": 0.45, "ArmUpW": 0.45, "BodyUpW": 0.45, "BodyShoulderW": 0.45}},
  "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
           "front": {"id": 10, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
           "side": {"id": 7, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0}},
  "clothes": {"top": {"id": 218, "color0": "#1E2A55", "color1": "#FFFFFF", "color2": "#1E2A55"},
              "bot": {"id": 208, "color0": "#1E2A55", "color1": "#2E4A8C"},
              "panst": {"id": 1, "color0": "#F7FAFF"}, "shoes": {"id": 8, "color0": "#1B2249"}}
}

g = post("/generate", gen, 120)
print("generate ok=%s verified=%s" % (g.get("ok"), g.get("verified")))
for a in g.get("applied", []):
    if a["item"].startswith("body.shape") or a["item"] == "smooth_skin":
        pass
print("  smooth_skin / shape 已应用：", [a["item"] for a in g.get("applied", []) if a["item"] == "smooth_skin" or a["item"].startswith("body.shape")][:6], "...")
for s in g.get("skipped", []):
    print("  SKIP", s["item"], "::", s["reason"])

# 高分辨率局部特写：躯干正面（看皮肤质感与上衣穿模）
shots = [
    ("detail_torso.png", "0,1.30,1.05", "0,1.12,0"),      # 胸口/腰
    ("detail_hip.png",   "0,1.05,1.15", "0,0.85,0"),      # 腰胯（裙腰处最容易穿模）
    ("detail_arm.png",   "0.9,1.25,0.9", "0.35,1.15,0"),  # 右臂/袖口
]
for f, frm, look in shots:
    r = post("/capture", {"views": ["front"], "from": frm, "look_at": look,
                          "panel_width": 900, "file": f, "name": "澪"}, 120)
    print(" ", f, "ok=%s" % r.get("ok"), "coverage=%.3f" % (r["panels"][0]["coverage"] if r.get("panels") else -1))
