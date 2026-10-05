# -*- coding: utf-8 -*-
"""冻结物理后：1) 同参数重复渲染的一致性  2) 眼型的区分度。
   A 组：两次都用 pupil.id=0（同参数，理想情况分差应≈0）
   B 组：pupil.id=0 vs 90 交替，看指标能否拉开。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(path, body, timeout=900):
    req = urllib.request.Request(BASE + path, data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
                                 headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(req, timeout=timeout).read().decode("utf-8"))

base_params = {
    "base": "お嬢様.png", "sex": "female",
    "clear_accessories": True, "smooth_skin": True,
    "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
    "face": {"pupil": {"baseColor": "#2E7FD4", "subColor": "#B9ECFF"}, "lipLineId": 3, "lipLineColor": "#C0707F"},
    "body": {"skinMainColor": "#FFE7D8", "skinGlossPower": 0.28, "detailPower": 0.0,
             "shape": {"BustSize": 0.38, "WaistLowW": 0.45, "Hip": 0.5, "ThighUpW": 0.45, "ArmUpW": 0.45},
             "normalize": True},
    "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
             "front": {"id": 4, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
             "side": {"id": 7, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0}},
    "clothes": {"top": {"id": 218, "color0": "#1E2A55", "color1": "#FFFFFF", "color2": "#1E2A55", "color3": "#1E2A55",
                        "pattern0": 0, "pattern1": 0, "pattern2": 0, "pattern3": 0},
                "bot": {"id": 208, "color0": "#1E2A55", "color1": "#2E4A8C", "pattern0": 0},
                "panst": {"id": 1, "color0": "#F7FAFF"}, "shoes": {"id": 8, "color0": "#1B2249", "pattern0": 0}},
}

g = post("/generate", dict(base_params, save_as="AI-鲸鱼女仆", overwrite=True, load=True), 200)
print("基准角色 ok=%s" % g.get("ok"))

# 0,0,90,90 交替：同参数相邻两轮的分差 = 噪声底；不同参数的分差 = 信号
cands = [{"face": {"pupil": {"id": n}}} for n in [0, 0, 90, 90]]
d = post("/fit", {
    "mode": "reload", "ref": "whale_ref", "panel": 0, "view": "front",
    "framing": "eyes", "ref_crop": "head",
    "base_params": base_params, "candidates": cands,
    "save_shots": True,
    "weights": {"appearance": 1.0, "iou_core": 0.0, "iou_full": 0.0, "color_hist": 0.0, "band_color": 0.0},
    "top_k": 4, "budget_ms": 300000, "name": "澪",
}, 900)
print("ok=%s tested=%s ms=%s" % (d.get("ok"), d.get("tested"), d.get("elapsed_ms")))
print("board_order=%s" % d.get("board_order"))
for t in d.get("top", []):
    print("  iter=%-2s leaf=%-3s total=%.5f app=%.5f frozen=%s shot=%s" % (
        t.get("iter"), t.get("candidate_leaf"), t.get("total", 0), t.get("appearance", 0),
        t.get("frozen"), (t.get("shot") or "").split("\\")[-1]))
