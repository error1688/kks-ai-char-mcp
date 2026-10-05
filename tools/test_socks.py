# -*- coding: utf-8 -*-
"""裤袜到底渲染不渲染：分别试 panst=7 厚裤袜A / socks=3 膝袜 / 两者都有，各拍全身高清图。
   膝袜(socks)是肯定有网格的，用它当对照：socks 能显示而 panst 不能 => panst 资源/渲染有问题。"""
import json, urllib.request

BASE = "http://127.0.0.1:24380"

def post(p, b, t=300):
    r = urllib.request.Request(BASE + p, data=json.dumps(b, ensure_ascii=False).encode("utf-8"),
                               headers={"Content-Type": "application/json"}, method="POST")
    return json.loads(urllib.request.urlopen(r, timeout=t).read().decode())

common = {
    "base": "お嬢様.png", "sex": "female", "save_as": "AI-袜测", "overwrite": True, "load": True,
    "clear_accessories": True, "smooth_skin": True,
    "parameter": {"fullname": "汐见 澪", "nickname": "澪"},
    "body": {"shape": {"BustSize": 0.38}, "normalize": True},
    "clothes": {"top": {"id": 218, "color0": "#1E2A55", "color1": "#FFFFFF", "pattern0": 0},
                "bot": {"id": 208, "color0": "#1E2A55", "color1": "#2E4A8C", "pattern0": 0},
                "shoes": {"id": 8, "color0": "#1B2249", "pattern0": 0}},
}
cases = [
    ("panst7", {"panst": {"id": 7, "color0": "#FFFFFF", "pattern0": 0}}),
    ("socks3", {"socks": {"id": 3, "color0": "#FFFFFF", "pattern0": 0}}),
    ("both",   {"panst": {"id": 7, "color0": "#FFFFFF", "pattern0": 0},
                "socks": {"id": 3, "color0": "#FFFFFF", "pattern0": 0}}),
]
for tag, extra in cases:
    cl = dict(common["clothes"]); cl.update(extra)
    g = post("/generate", dict(common, clothes=cl), 300)
    r = post("/capture", {"views": ["front"], "framing": "full", "panel_width": 1080,
                          "name": "澪", "file": "SOCK_%s.png" % tag}, 300)
    print("%-8s gen=%s cap=%s" % (tag, g.get("ok"), r.get("ok")))
