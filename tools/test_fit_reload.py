# -*- coding: utf-8 -*-
"""验证通用搜索（mode=reload）：自动取 eye 分类前 8 个 id 作为候选，与参考图正面比对。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(path, body, timeout=600):
    req = urllib.request.Request(BASE + path, data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
                                 headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(req, timeout=timeout).read().decode("utf-8"))

# 与当前成品一致的基准参数（不含 save_as / load）
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

# 先放一个角色进场景（搜索会替换它）
g = post("/generate", dict(base_params, save_as="AI-鲸鱼女仆", overwrite=True, load=True), 200)
print("基准角色 ok=%s" % g.get("ok"))

d = post("/fit", {
    "mode": "reload", "ref": "whale_ref", "panel": 0, "view": "front", "framing": "full",
    "base_params": base_params,
    "auto_candidates": {"category": "eye", "path": "face.pupil.id", "limit": 8},
    "top_k": 4, "budget_ms": 180000,
    "name": "澪",
}, 600)
print("ok=%s 有效=%s 耗时ms=%s 最优卡=%s" % (d.get("ok"), d.get("tested"), d.get("elapsed_ms"),
                                        (d.get("best_card") or "").split("\\")[-1]))
for t in d.get("top", []):
    print("  TOP id=%-4s total=%.3f iou_core=%.3f hist=%.3f band=%.3f" % (
        t["candidate"], t.get("total", 0), t.get("iou_core", 0), t.get("color_hist", 0), t.get("band_color", 0)))
errs = [b for b in d.get("board", []) if "error" in b]
if errs:
    print("  错误 %d 条，示例:" % len(errs), errs[0])
