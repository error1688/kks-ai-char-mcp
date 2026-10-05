# -*- coding: utf-8 -*-
"""完整数值流程测试：生成 -> 对标打分 -> 搜索发型候选"""
import json, os, urllib.request, base64

BASE = "http://127.0.0.1:24380"

def post(path, body, timeout=300):
    req = urllib.request.Request(BASE + path, data=json.dumps(body, ensure_ascii=False).encode("utf-8"),
                                 headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(req, timeout=timeout).read().decode("utf-8"))

gen = {
  "sex": "female", "base": "お嬢様.png", "save_as": "AI-鲸鱼女仆", "overwrite": True, "load": True,
  "clear_accessories": True,
  "parameter": {"fullname": "汐见 澪", "nickname": "澪", "personality": 1},
  "face": {"pupil": {"id": 0, "baseColor": "#2E7FD4", "subColor": "#9FE4FF"}, "eyebrowId": 1,
           "eyebrowColor": "#2A3A6E", "eyelineColor": "#20305E", "lipLineColor": "#B4687A",
           "hlUpId": 2, "hlUpColor": "#FFFFFF"},
  "body": {"skinMainColor": "#FFE7D8", "skinSubColor": "#FFD9C4", "bustSoftness": 0.5, "bustWeight": 0.5},
  "hair": {"back": {"id": 45, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
           "front": {"id": 10, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0},
           "side": {"id": 7, "baseColor": "#33529F", "startColor": "#16265C", "endColor": "#86D9F2", "length": 1.0}},
  "clothes": {"top": {"id": 218, "color0": "#1E2A55", "color1": "#FFFFFF", "color2": "#1E2A55"},
              "bot": {"id": 208, "color0": "#1E2A55", "color1": "#2E4A8C"},
              "panst": {"id": 1, "color0": "#F7FAFF"}, "shoes": {"id": 8, "color0": "#1B2249"}}
}

g = post("/generate", gen, 120)
print("generate ok=%s verified=%s" % (g.get("ok"), g.get("verified")))

for panel, view in [(0, "front"), (1, "right"), (2, "back")]:
    c = post("/compare", {"ref": "whale_ref", "panel": panel, "view": view, "name": "澪"}, 120)
    s = c.get("score", {})
    print("panel%d(%s) vs %-5s → total=%.3f iou_core=%.3f iou_full=%.3f hist=%.3f band=%.3f  [%s]"
          % (panel, view, s.get("iou_core"), s.get("total"), s.get("iou_core"), s.get("iou_full"),
             s.get("color_hist"), s.get("band_color"), s.get("verdict"))
          if False else
          "panel%d vs %-5s → total=%.3f (iou_core=%.3f iou_full=%.3f hist=%.3f band=%.3f) %s"
          % (panel, view, s.get("total", 0), s.get("iou_core", 0), s.get("iou_full", 0),
             s.get("color_hist", 0), s.get("band_color", 0), s.get("verdict")))
    print("   分带色 ref=%s mine=%s" % (c.get("band_colors", {}).get("ref"), c.get("band_colors", {}).get("mine")))
    if panel == 0:
        print("   montage:", c.get("montage"))
