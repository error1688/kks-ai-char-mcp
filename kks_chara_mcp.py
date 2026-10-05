#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
KKS 人物生成 MCP 服务器（零依赖，stdio 传输）

配合游戏内插件 KKS_AICharBridge 使用：
  1. 启动 KoikatsuSunshine / CharaStudio（插件监听 http://127.0.0.1:24380）
  2. AI 通过本 MCP 服务器的工具生成人物卡 / 截图查看效果

支持两种帧格式：MCP 标准的按行分隔 JSON，以及 LSP 风格 Content-Length 帧。
端口可用环境变量 KKS_BRIDGE_PORT 覆盖（默认 24380）。
"""
import base64
import json
import os
import subprocess
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

PORT = int(os.environ.get("KKS_BRIDGE_PORT", "24380"))
BASE = "http://127.0.0.1:%d" % PORT
SERVER_NAME = "kks-chara-mcp"
SERVER_VERSION = "1.0.0"

# ---------------- 桥接 HTTP ----------------

def bridge(method, path, body=None, timeout=30):
    data = None
    headers = {}
    if body is not None:
        data = json.dumps(body, ensure_ascii=False).encode("utf-8")
        headers["Content-Type"] = "application/json; charset=utf-8"
    req = urllib.request.Request(BASE + path, data=data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as resp:
            return json.loads(resp.read().decode("utf-8"))
    except urllib.error.HTTPError as e:
        try:
            return json.loads(e.read().decode("utf-8"))
        except Exception:
            return {"ok": False, "error": "HTTP %d" % e.code}
    except Exception as e:
        return {"ok": False, "error": "无法连接游戏（请确认 KoikatsuSunshine/CharaStudio 已启动且 KKS_AICharBridge 插件加载）: %s" % e}

# 游戏程序路径：默认 CharaStudio；可用环境变量 KKS_EXE 覆盖
GAME_EXE = os.environ.get("KKS_EXE", r"E:\game\KKS\CharaStudio.exe")


def launch_game(wait_seconds=180):
    """一条调用把游戏拉起来并等到桥可用（对应 dsh-blender-plugin 的 one-call launch）。"""
    st = bridge("GET", "/status", timeout=6)
    if st.get("ok"):
        return {"ok": True, "already_running": True, "status": st}
    if not os.path.isfile(GAME_EXE):
        return {"ok": False, "error": "找不到游戏程序: %s（用环境变量 KKS_EXE 指定）" % GAME_EXE}
    try:
        subprocess.Popen([GAME_EXE], cwd=os.path.dirname(GAME_EXE),
                         creationflags=getattr(subprocess, "DETACHED_PROCESS", 0))
    except Exception as e:
        return {"ok": False, "error": "启动失败: %s" % e}
    t0 = time.time()
    while time.time() - t0 < wait_seconds:
        time.sleep(3)
        st = bridge("GET", "/status", timeout=5)
        if st.get("ok") and st.get("inStudio"):
            return {"ok": True, "ready": True, "elapsed_s": round(time.time() - t0, 1), "status": st}
    return {"ok": False, "error": "已启动但 %ds 内桥未就绪（首次启动可能要更久，或插件未加载）" % wait_seconds,
            "launched": True}


# ---------------- 工具定义 ----------------

GENERATE_SCHEMA = {
    "type": "object",
    "properties": {
        "sex": {"type": "string", "enum": ["female", "male"], "description": "默认 female"},
        "base": {"type": "string", "description": "基底卡（用户卡或官方预设卡名），省略用第一张预设"},
        "save_as": {"type": "string", "description": "卡片名"},
        "load": {"type": "boolean", "description": "true=生成后立即加入工作室场景（默认**替换同名角色**，不会越叠越多；要并排多个角色传 add:true）"},
        "add": {"type": "boolean", "description": "true=即使已有同名角色也再加载一个（默认 false=替换）"},
        "overwrite": {"type": "boolean"},
        "see": {"type": "boolean", "description": "true=同一次调用内继续拍三视图拼图一起返回"},
        "clear_accessories": {"type": "boolean", "description": "先清空基座卡原有饰品槽"},
        "parameter": {"type": "object", "description": "fullname/nickname/personality/birthMonth/birthDay/bloodType 等"},
        "face": {"type": "object", "description": "pupil{id,baseColor,subColor}、eyebrowId/eyebrowColor、eyelineColor、lipLineColor、headId、hlUpId/hlUpColor、shapeValueFace[] 等；shape:{滑条名:0~1} 按名字设面部滑条（ChinW/FaceLowW/ChinTipW/CheekW/ChinY/EyeW/MouthW…共 52 个，名字用 /params?what=shapes 查）"},
        "body": {"type": "object", "description": "skinMainColor/skinSubColor/skinId/nipColor/nailColor/bustSoftness/bustWeight/shapeValueBody[] 等；shape:{滑条名:0~1} 按名字设体型滑条（BustSize/ThighUpW/Calf/KneeLowW…共 44 个）；normalize 默认 true，会把易穿模滑条（含腿部）拉回安全带"},
        "hair": {"type": "object", "description": "back/front/side/option -> {id,baseColor,startColor,endColor,length}；startColor=发根端 endColor=发梢端"},
        "clothes": {"type": "object", "description": "top/bot/bra/shorts/gloves/panst/socks/shoes -> {id,color0~3,pattern0~3,patternColor0~3,state}；id 0=无；围裙色常是 color1；要纯色必须 pattern0:0 清掉花纹叠加层"},
        "accessories": {"type": "array", "description": "饰品 [{type,id,colors,slot?,parent?}]，type=hair/head/face/neck/body/waist/leg/arm/hand",
                        "items": {"type": "object", "properties": {
                            "type": {"type": ["string", "integer"]}, "id": {"type": "integer"},
                            "colors": {"type": "array", "items": {"type": "string"}},
                            "slot": {"type": "integer"}, "parent": {"type": "string"}}}}
    },
    "description": "字段全表、取值范围与验证闭环用 kks_help 查"
}

# ---------------- 长尾文档（工具描述保持精简，细节用 kks_help 查） ----------------

HELP = {
    "workflow": """按参考图生成人物的标准流程（每一步都要留证据）：

0. 开新会话先 kks_audit（场景里有没有叠着好几个角色/有没有别的遗留物）+ kks_launch（没开就拉起来）；
   改完参数想看效果之前，也要再 audit 一次。详见 kks_help verify。
1. 看图：提取发色（渐变要拆成 发根/发梢）、瞳色、肤色、服装主辅色、鞋袜、饰品。
2. 查资源：kks_list_options 按分类找候选（hair_*/top/bot/panst/shoes/eye/eyebrow/ao_*）。
   官方预设卡 kks_list_presets 按人格命名，选气质接近的当基底最省事。
3. 生成：kks_generate_character（想清空基座卡饰品槽就带 clear_accessories:true）。
   加 see:true 可以在同一次调用里直接拿到三视图拼图，省一轮。
4. 验：kks_capture_views 拿 front/right/back 三视图，逐项比对上面提取的特征。
   图里 warnings 有 frame_looks_empty / identical_to_previous 时，先修取景再谈像不像。
5. 调：不像就改参数重来（overwrite:true）。每次只改一处，便于判断是哪个参数起的作用。
6. 报：说清楚哪些对上了、哪些没对上、为什么（游戏里没有对应资源 / 饰品位置需要手工偏移等）。
   含糊的时候说"未确认"，不要用"应该没问题"糊过去。""",

    "params": """kks_generate_character 参数（颜色一律 #RRGGBB，也支持 #RGB/8 位）：

parameter: fullname（含空格自动拆姓+名）| nickname | personality(0~43，越界会钳制) | birthMonth | birthDay | bloodType(0=A,1=B,2=O,3=AB)
face:      pupil{id,baseColor,subColor} 作用于双眼 | eyebrowId/eyebrowColor | eyelineColor
           lipLineColor | moleId/moleColor | headId 脸型 | hlUpId/hlUpColor 高光 | shapeValueFace[] 滑条
           shape: {滑条名: 0~1} 按名字设面部滑条（52 个，0.5 附近中性）：
             ChinW/ChinTipW/FaceLowW/FaceBaseW 控制下巴与下颌宽窄（基座卡常是 0 = 尖下巴+硬下颌线，
             想圆一点就给 0.42~0.55）；CheekW/CheekBoneW 脸颊；ChinY/ChinLowY 下巴高低；
             EyeW/EyeH/EyeY/EyeTilt 眼型；MouthW/MouthY 嘴；EarSize 耳。
             名字以 /params?what=shapes 返回的为准（与游戏枚举逐项核对过）
             ※★ **先读原版卡，再决定改什么**：官方 44 张预设卡就是最好的参数样本，
               用 kks_inspect_character 的 face_shape / body_shape（或 kks_params?what=fields）
               把它们的值读出来照抄，比凭感觉推滑条靠谱得多。实测：
               `FaceLowW=0 / ChinW=0.286 / ChinTipW=0` 是**大多数官方卡**的取值（お嬢様/のじゃっ子/
               ギャル/セクシー…），不是"基座卡坏了"；而 `ChinTipW` 在**全部 44 张**里都是 0。
               把 ChinTipW 推到 0.5 会把低模头部推成多面体 —— 看上去正是"下巴有凹槽、脸坑洼"。
               想要圆下巴就用本来就圆的原版脸（如 天真爛漫：FaceLowW=0.5 / ChinW=0.325 / ChinTipW=0），
               直接拿那张卡当 base，不要覆盖 face.shape。
             ※ 只想改下巴时，**别把 FaceUp*/Cheek* 也设成同一个值**：
               FaceUpY/FaceUpSize 会把眼睛和额头整体挪位（看起来像眼睑变厚、眉毛变粗）；
               而 Cheek 与 Chin 取值不一致时，脸颊到下巴之间会出现**凹槽**。
               要圆下巴就沿"脸颊→下颌→下巴尖"给**单调递增**的值，例如
               FaceLowW 0.42 / ChinW 0.47 / ChinTipW 0.50（基座卡常是 0 / 0.29 / 0 = 最尖最窄）。
body:      skinMainColor | skinSubColor | skinId | nipColor | nailColor | sunburnColor
           bustSoftness | bustWeight | shapeValueBody[] 滑条
           shape: {滑条名: 0~1} 按名字设（BustSize/WaistLowW/Hip/ThighUpW/Calf/KneeLowW/AnkleW... 共 44 个）
           normalize: 默认 true，把易穿模滑条拉回安全带：乳晕/乳头压到 0~0.12，腿部滑条夹到 0.32~0.80
             （腿型和袜子/裤袜是两套网格，腿太极端会从袜子里顶出来）；false = 保留基座卡体型
           detailPower/detailId（身体细节凹凸贴图）、drawAddLine（肌肉线）、areolaSize
顶层：      smooth_skin: true = 关掉身体+面部的 detailPower/drawAddLine/涂装/晒痕并收敛光泽
hair:      back/front/side/option -> {id, baseColor, startColor, endColor, length}
           startColor=发根端，endColor=发梢端（做渐变靠这两个）
clothes:   top/bot/bra/shorts/gloves/panst/socks/shoes -> {id, color0~3, pattern0~3, patternColor0~3, state}
           id 0 = 无（不穿）；state 0=穿着(默认) 1=半脱 2/3=脱下，也可用 wear:false
           ※ 颜色有 4 个通道 color0~color3（女仆装的围裙色常是 color1）
           ※ **要纯色必须清花纹**：衣服上那层"浅色条纹/花色"是 pattern 叠加层盖出来的，
             形如 "pattern0":0 清掉第 0 通道的花纹；只用 colorN 改不掉它（常见坑：
             颜色明明写成深藏青，渲染出来却是浅蓝条纹）
accessories: [{type, id, colors[], slot?, parent?}]
           type: hair/head/face/neck/body/waist/leg/arm/hand（也可 1~10）
           基座卡常把 20 个饰品槽占满，加 clear_accessories:true 先清空

—— 以上只是最常用的；face/body **还有一大片官方成员可直接写**（泛型反射按成员名写入）——
face 共 39 个成员、body 共 25 个（完整清单：kks_params?what=fields）。常被忽略但很有用的：
  face: whiteId/whiteBaseColor/whiteSubColor 眼白 | hlDownId/hlDownColor/hlDownX/hlDownY 下半高光
        eyelineUpId/eyelineDownId/eyelineUpWeight 上下眼线分开调 | pupilX/pupilY/pupilWidth/pupilHeight 瞳孔位置与大小
        foregroundEyebrow/foregroundEyes 描画顺序 | doubleTooth 八重齿 | detailId/detailPower 面部凹凸细节
        cheekGlossPower/lipGlossPower 脸颊与唇光泽 | skinId 皮肤类型
  body: typeBone 骨骼类型 | underhairId/underhairColor 体毛 | areolaSize 乳晕 | nipId/nipGlossPower
        paintId[2]/paintColor[2]/paintLayoutId[2] 身体涂装两个槽 | skinGlossPower | nailGlossPower | sunburnId
  parameter: 除上面的标量外，**人格问卷也支持**（传子对象，如
        "attribute":{"majime":true,"friendly":true}、"awnser":{"sweet":true}、"denial":{"kiss":false}）；
        还有 aggressive/diligence/kindness 三个标量

异色瞳：face.pupil 传**数组** = 左右眼分别设（[左, 右]），或用 pupilLeft/pupilRight 显式指定：
        "face":{"pupil":[{"baseColor":"#FF0000"},{"baseColor":"#00FF00"}]}
        实测渲染确实左右不同色（isPupilSameSetting 是只读属性、不影响）

多套服装：卡片有 4 套坐标（制服/体操服…）。clothes 默认只写"当前那套"（和游戏一致）：
        "coordinate":1 → 只写第 1 套；"all_coordinates":true → 每套都写同一份
        注意 clothesState（穿着状态）只有 9 项、不分坐标，所以写别的套数时不会动状态

Vector 类型（饰品位移/花纹几何/发区微调）也支持，写法 [x,y,z] 或 {"x":..,"y":..}：
        accessories[].addMove、clothes 的 tiling/offset/rotate、hair 的 pos/rot/scl、face 的 moleLayout

滑条数组 shapeValueFace/Body 是逐元素写入：给一部分也能用，过长才会报错。""",

    "acceptance": """验收标准（照这个判"做完了没有"）：

1. kks_doctor 返回 kind=ok —— 通道本身可用，否则后面都不算数。
2. 生成调用返回 verified=true（卡片能被游戏自己读回来）。
3. 拿到三视图拼图，且每格 coverage 明显大于 0（不是空帧/黑帧）；
   出现 identical_to_previous 说明视角没变，等于没验。
4. 逐项对照参考图特征：发色/发型/瞳色/主色/鞋袜/饰品，逐条说对上或没对上。
5. 没对上的必须给出原因（无此资源 / 需要手工偏移 / 参数受限），不能沉默。

反面例子（我踩过的）：
- 只截了半身就说"完成"，头被裁掉了其实没看到脸。
- 只改卡片数据没调运行时接口，结果衣服是"已脱"状态，人出来是半裸的。
- 饰品 type 在卡片里存的是分类号(120=空)，按 0 判空会误判成"没有空槽"。""",

    "pitfalls": """常见坑：

- 服装不显示：官方预设卡的衣服状态是"已脱"（clothesState=3）。生成时会自动调用游戏的
  SetClothesStateAll(0) 让它穿上；若手工只改卡片数据就会半裸。
- 饰品没出现：不少饰品的默认位置/朝向不适合，需要手工偏移（addMove）——
  目前接口只能写 type/id/颜色并套用游戏默认定位，尾巴类常常因此看不见。
- 工作室里新角色都加在原点，多个角色会重叠，截图会互相遮挡。
  验证单个角色请用干净场景（重开工作室）。
- 截图缺头/缺脚：用 kks_capture_views 自动取景（按包围盒算距离），别用固定距离。
- 发型/服装 ID 不存在：返回里 skipped 会给 did_you_mean 的相近候选。
- 中文卡片名：query 会做 URL 解码，但 curl 在 Windows 下可能按本地编码发出，用 MCP 工具即可。
- **连裤袜(panst) 在本机不渲染**：实测同一套流程下 socks(膝袜/吊带袜) 能正常显示白色，
  panst 从 0/1/5/7 换来换去画面完全一样（游戏自己的视口也一样，不是离屏渲染的问题），
  资源文件 co_panst_*.unity3d 是在的 → 判断是身体材质/着色器层面的问题。要腿部覆盖请用 socks。
- **不要反复用 load:true 堆角色**：默认已改成"同名替换"，但若手动传 add:true，多次加载会在原点叠一摞同名模型；
  渲染出来是"好几个模型互相穿插"，极容易被误读成"穿模/皮肤异常"（我在腰部问题上就因此白查了一轮）。
  拿不准就先 kks_clear 清空，再只加载一个。
- **改完别马上拍**：/generate 的"恢复穿着"是异步的，紧跟其后的截图可能拍到还没穿上衣服的中间态。
  现在渲染前会自动补齐穿着（返回里 frozen 表示物理已冻结），但自定义流程时仍要留意。""",

    "polish": """成品自检（这三类问题只看整体截图是看不出来的，必须拍局部特写）：

1) kks_capture_views({framing:"face", panel_width:800}) 看脸；{framing:"eyes"} 看眼型/瞳色；
   kks_capture_views({framing:"bust"}) 看胸口/袖口；{framing:"torso"} 看腰腹。
2) 穿模（肢体顶出衣服）常见来源：
   - 乳晕/乳头形状（AreolaBulge/NipWeight/NipStand）：即使中等值也会顶穿薄衣服。body.normalize 默认会把它们压到 0~0.12；
     若手动在 body.shape 里给了值，就会覆盖 normalize —— 穿衣服的角色别给这几个。
   - 体型极端值：BustSize/BustX/Hip/WaistLowW/ThighUpW/ArmUpW 等拉到 0 或 1 时身体会顶出衣服。
   - 换完衣服/身体后没等网格加载完就截图：等 settled=true 再判读。
3) 皮肤不平整：body.detailPower（身体细节凹凸贴图）> 0.05 或 face.detailPower > 0.05 会显脏/不平；
   drawAddLine（肌肉线）、paintId（涂装）、sunburnId（晒痕）也会。smooth_skin:true 一键关掉并收敛光泽。
4) 残留物检查：基座卡常自带发饰/项链/蝴蝶结等饰品。只传 clear_accessories:true 即可清空（不要数组），
   但清完**一定要拍背面看**——头顶/后腰的残留饰品在正面图里往往看不见。
5) 衣服颜色"改了没变"：衣服上那层浅色条纹/花色是 pattern 叠加层盖出来的，只改 colorN 改不掉；
   要纯色就同时给 "pattern0":0（清第 0 通道花纹）。
6) 面部"没法看"的常见原因（都是"多改了不该改的"）：
   - eyelineColor 设得过深 → 眼线变成一大块黑带盖住眼珠。基座卡的眼线色是柔和的，能不动就别动。
   - eyebrowColor/eyebrowId 乱改 → 眉毛飘高/过细。
   - 覆盖 pupil.id / hlUpId / eyebrowId 这些"造型 id" → 高光变白块、眼型变怪。
   - 口红没有轮廓：lipLineId=0 时嘴几乎看不见，给个非 0 的 lipLineId。
   - 刘海挡眼：换个分发型（hair_front 4 大小姐分发 / 39 分发 / 12 中分）。
   结论：基座卡是官方做的成品，**只改参考图明确要求的部分（瞳色、发色、服装色）**，
   其余妆容 id 与颜色继承它；要动 id 就先拍 face 特写核对。
   用 kks_inspect_character 可以读回 face/body 全部参数（含 face_notes / body_shape_warnings）来定位问题。
7) 选不出 id 时别猜：kks_list_options({category:"eye"}) 会列出该分类下**游戏里真实存在**的全部 id 与名称
   （eye 有 96 种、eyebrow 18、lip 9、eye_hi_up 54、eyeline_up 83…）。给一个不存在的 id 会在返回的 skipped 里报错。
8) 面具/刘海挡眼：搜发型时用 kks_fit 的 save_shots，一眼就能从接触印相里看出哪种刘海把眼睛盖住了。""",

    "verify": """验收纪律（参考 dsh-blender-plugin 的"证据分级"；下面每条都是踩过坑才写下的）：

0. **先看场景，再看图**：动任何判断之前先 kks_audit，kind=ok 才算"场景可信"。
   同名角色叠一摞时，渲染出来几个身体互相穿插 —— 我把它当成了"穿模/皮肤异常"，
   在"腰部"上白查了一轮。audit 里 chars_separated 的距离、unique_names 的数量就是防这个的。
1. **致命判断来自数字，不来自看图**：角色数/重名/服装状态/分数表/覆盖率这类结论一律先用脚本量；
   看图只用来"确认脚本指出的异常长什么样"。图和数字冲突时，先怀疑图（是不是脏场景/没加载完）。
2. **看图要有预算**：每轮 ≤6 张、单张 ≤800×450（≈≤4.3k token）。要看清细节就拍局部特写
   （framing: face/eyes/bust/torso）或裁小图，不要拿整身图反复看。
3. **读图前先写断言**：先说这张图要验证哪 2~3 条**可测命题**（发色是不是 #86D9F2？
   腰侧有没有皮肤外露？），再去看；否则看完只能得到一个印象。
4. **读图后写三分法**：【确认看到的】【推断的】【我没有能力判断的】；
   无法用数字或明确像素证据支持的视觉判断，标注为"未经证实的视觉印象"，不要写成"通过"。
5. **改一次只验一次**：一次只动一组参数，改完立刻用同一取景复拍，否则说不清是哪一项起的作用。
6. **渲染不可复现时数字全废**：测量渲染会自动冻结 DynamicBone 并强制睁眼（返回里 frozen:true），
   否则头发每帧在飘、和"只差眼型"的真实信号相比噪声大 100 倍。

7. **别只看 ok/settled 就以为拿到了图**：实测返回 ok=true、settled=true，但图是**纯色空帧**
   （重启游戏后场景是空的，就该如此）。判据是 panels[].coverage 与 warnings（frame_looks_empty /
   no_subject），不是 ok。写图前先看这两个数。
8. **参数别自己推**：官方 44 张预设卡是最好的样本，先读（kks_inspect_character 的 face_shape /
   body_shape）再改。实测 ChinTipW 在全部 44 张里都是 0，我推到 0.5 就把低模下巴推成了多面体
   （凹槽/坑洼）。要圆脸就换本来就圆的原版卡当 base。

判定口径（三态）：PASS=数字与图都支持；FAIL=数字不支持或图上有可见缺陷；
UNKNOWN=无法量也无法看清 —— **UNKNOWN 不许写成 PASS**。""",

    "reference": """按参考图生成/校准的用法：

1. kks_reference({file:"C:/.../三视图.jpg"}) —— 自动切成 3 格（正面/侧面/背面），
   返回每格的剪影形状(ASCII)、包围盒、aspect、分带主色(head/torso/legs)。
   切分不理想时用 ranges:"0-400,420-820" 手工指定。
2. 生成角色并 load:true 进工作室。
3. kks_compare({ref:"名字", panel:0, view:"front"}) —— 得到总分与分项（iou_core / iou_full /
   color_hist / band_color / appearance）以及并排对照图。判读时数字和对照图一起看。
4. 哪个部位不像就搜：kks_fit({ref:"名字", ...}) —— 服务端一轮跑完所有候选，返回候选榜，
   并把最优应用回角色。整轮零模型轮次。两种模式：
   · mode=hair（默认，快）：只换发型 id/颜色，运行时热换，kks_fit({ref, kind:"hair_front", candidates:[4,12,39]})
   · mode=reload（通用，慢）：写临时卡+替换角色，可搜任意参数路径：
     kks_fit({mode:"reload", ref, name:"澪", base_params:{...与 generate 同构...},
              auto_candidates:{category:"hair_front", path:"hair.front.id", limit:8},
              framing:"head", ref_crop:"head", save_shots:true})
   搜面部细节一定要 framing:"face"/"eyes" + ref_crop:"head" 一起给（否则参考图是全身、渲染是特写，尺度对不上）。
5. **一定要带 save_shots:true**：会返回每个候选的 PNG + 一张接触印相 board_png（左→右 = 高分→低），
   board_order 给出每格的"候选值:分数"。然后**自己看图**再决定 —— 数字只是粗筛，
   眼型这种细节的分数差可能只有 1e-4 量级（噪声底约 4e-5），不足以单靠数字定胜负。
6. 换完再 kks_compare 复核（口径一致，分数可比）。

分数口径：total = 0.35*iou_core + 0.10*iou_full + 0.15*color_hist + 0.25*band_color + 0.15*appearance
（可用 weights 覆盖；搜面部细节时把 appearance 拉高、其余压低）。
iou_core 只取中央 50% 宽，用来避开工作室 T-pose 的手臂（参考图通常是手臂下垂）。
verdict：>=0.62 close / >=0.48 partial / 否则 far —— 这个阈值只对默认权重有意义。

可复现性（重要）：测量渲染前会把角色的 DynamicBone 物理停掉并强制睁眼。
不这么做的话，角色每次重新加载头发/裙摆的落点都不同，同一参数两次渲染的像素差能达到 meanL1 13，
而"只差眼型"的真实信号只有 0.10 —— 噪声比信号大 100 倍，任何像素指标都失效。
停掉物理后同参数重复渲染的差降到 0.04，搜索才有意义。所以看到 frozen:true 是正常的、也是期望的。""",

    "plugins": """借道第三方制作插件（游戏内"修改器"能改更多，一半靠的就是这些插件）。

**先问再借**：kks_plugins 看每个插件的 present（装没装）与 bridged_op（能不能通过本桥改）。
present=false 时借道会明确报错，不会假装成功；bridgeable=false 表示只能用它自己的界面改。

已接通的两个（都是零模型轮次、服务端直接改）：

1) kks_material —— MaterialEditor：改**任意 shader 属性**（vanilla 数据模型里没有的维度）
   op=list   列渲染器与材质名（实测本机 30 个渲染器）
   op=props  列某材质的**真实属性名与当前值** ← 必须先用它，别猜属性名
   op=set    改值：#RRGGBB=颜色 / 数字=浮点 / 路径=贴图 / true|false=关键字
   实测皮肤 shader 是 Koikano/main_skin，属性形如 _overcolor1/_SpecularColor/_SpecularPower/_nip。
   ⚠ 踩过的坑：MaterialAPI.SetColor(gameObject, materialName, ...) 对**角色材质一律返回 false**
   （属性明明存在、值读回原样、渲染也不变）——它只认自己管理的材质副本。本桥改走
   MaterialEditorCharaController.SetMaterialColorProperty，返回里的 saved_readback 才是真正存下的值。
   验证方式：改完 kks_capture_views 再拍一张，md5 变了才算真生效（实测有效）。

2) kks_bones —— KKSABMX(BonemodX)：**逐骨骼**缩放/位移/旋转，比 44 个形状滑条细得多
   op=list 列骨骼名（实测 979 根；命名是小写 cf_j_head / cf_j_hips / cf_j_hand_L，
           注意没有 cf_j_leg_L，腿是 cf_j_leg_L 之外的命名，先 list 再挑）
   op=set  bones:[{name, scale:[x,y,z] 倍率(1=不变), length, position, rotation}]
   实测：cf_j_head 放大 1.35 后渲染确实改变；不存在的骨骼名会被明确拒绝并提示先 list。

**代价（必须知道）**：这些数据存在插件自己的扩展存块里（ExtSave）。借道生成的卡，
在**没装对应插件的人**那里会退化成 vanilla 外观 —— 材质改动消失、骨骼改动消失。

探测到但**没接**的（bridgeable=false，不要指望用本桥改）：
  MoreAccessories（饰品槽位扩展，接口挂在 UI 实例上）、
  KKPE / MovUrAcc（数据只在 ExtSave 里、由 UI 驱动，且 MovUrAcc 本机未加载）、
  OverlayMods（叠图/纹身，控制器类型能探测到但未接）、MoreOutfits（套装槽位，同上）。
""",

    "all": "",
}
HELP["all"] = (chr(10) * 2).join([HELP["verify"], HELP["workflow"], HELP["params"], HELP["acceptance"], HELP["pitfalls"], HELP["reference"], HELP["polish"], HELP["plugins"]])

TOOLS = [
    {"name": "kks_status", "description": "查询恋活游戏状态（是否运行、当前场景、FPS、是否在工作室）",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "kks_list_characters", "description": "列出 UserData 里已保存的人物卡（男/女）",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "kks_list_presets", "description": "列出官方默认预设卡（以人格命名，适合当生成基底）",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "kks_list_options", "description": "列出当前游戏可用的发型/服装 ID 与名称（含 Mod），生成人物时从中选 id。返回内容较大，可用 category 只取一类：hair_back/hair_front/hair_side/hair_option/top/bot/bra/shorts/gloves/panst/socks/shoes",
     "inputSchema": {"type": "object", "properties": {"category": {"type": "string", "description": "只要某一类，可省略"}}}},
    {"name": "kks_generate_character", "description": "生成人物卡（基底卡 + 覆盖参数 -> PNG 卡；load=true 加入工作室场景）。加 see=true 可在同一次调用里拿到三视图拼图。参数与验证闭环见 kks_help。返回 applied/skipped 逐项说明。",
     "inputSchema": GENERATE_SCHEMA},
    {"name": "kks_screenshot", "description": "截单张画面并返回图像（验证造型优先用 kks_capture_views，它自动取景且能一次出多视角）",
     "inputSchema": {"type": "object", "properties": {
         "file": {"type": "string", "description": "截图文件名，可省略"},
         "focus": {"type": "boolean", "description": "true=先把相机对准人物再截（仅工作室）"},
         "name": {"type": "string", "description": "聚焦的人物名关键字，可省略"},
         "framing": {"type": "string", "enum": ["full", "bust", "head"], "description": "取景：full 全身 / bust 半身（默认）/ head 头部特写"},
         "view": {"type": "string", "enum": ["front", "back", "left", "right"], "description": "视角：front 正面（默认）/ back 背面（看后发、背部装饰、尾巴）/ left、right 侧面"}}}},
    {"name": "kks_focus_camera", "description": "把工作室相机移到人物正前方并保持几秒（hold 参数可调），随后自动交还相机控制权。注意：kks_screenshot 的 focus 参数效果更好，推荐直接用",
     "inputSchema": {"type": "object", "properties": {
         "name": {"type": "string", "description": "人物名关键字，可省略"},
         "hold": {"type": "number", "description": "保持秒数，默认 3"}}}},
    {"name": "kks_inspect_character", "description": "读出一张卡的真实内容：姓名/五官/**face_shape 面部 52 个滑条**/**body_shape 体型 44 个滑条**/发型/服装 id 与颜色/服装状态/饰品槽。改参数前先读原版预设卡 —— 官方 44 张就是最好的参数样本（如 ChinTipW 在 44 张里全是 0）；也用于排查「改了为什么没效果」",
     "inputSchema": {"type": "object", "properties": {
         "file": {"type": "string", "description": "卡片文件名"},
         "sex": {"type": "string", "enum": ["female", "male"], "description": "默认 female"}}}},
    {"name": "kks_reference", "description": "载入参考图并自动切成多格（三视图会切成 3 格），返回每格的剪影形状、包围盒、分带主色。之后用 kks_compare / kks_fit 跟它打分",
     "inputSchema": {"type": "object", "properties": {
         "file": {"type": "string", "description": "参考图绝对路径，或放在 UserData/AICharBridge 下的文件名"},
         "name": {"type": "string", "description": "给它起的名字（后续 compare 用），省略用文件名"},
         "ranges": {"type": "string", "description": "自动切分不理想时手工指定 x 区间，如 \"0-400,420-820\""}}}},
    {"name": "kks_compare", "description": "把我当前角色的某个视角与参考图某格做数值比对：剪影 IoU + 颜色直方图 + 分带主色 → 总分，并生成并排对照图。用来把“像不像”从主观变成数字",
     "inputSchema": {"type": "object", "properties": {
         "ref": {"type": "string", "description": "kks_reference 里起的名字"},
         "panel": {"type": "integer", "description": "第几格，默认 0（三视图一般是 0=正面 1=侧面 2=背面）"},
         "view": {"type": "string", "enum": ["front", "back", "left", "right"], "description": "我的哪个视角去比，默认 front"},
         "framing": {"type": "string", "enum": ["full", "bust", "head"], "description": "默认 full"},
         "name": {"type": "string", "description": "人物名关键字，可省略"}}}},
    {"name": "kks_fit", "description": "服务端搜索：逐个试候选参数，每个都渲染并与参考图打分，返回候选榜并把最优应用回角色。整轮零模型轮次。两种模式：默认 mode=hair（只换发型 id/颜色，运行时热换，快）；mode=reload（写临时卡+替换角色，可搜任意参数路径，如 face.pupil.id / clothes.top.pattern0，每轮约 2~4 秒）。强烈建议带 save_shots=true —— 会额外返回每个候选的 PNG 和一张接触印相 board_png（左→右 = 高分→低），让判断有图可依而不是只看数字",
     "inputSchema": {"type": "object", "properties": {
         "ref": {"type": "string", "description": "kks_reference 起的名字"},
         "panel": {"type": "integer", "description": "参考图第几格，默认 0"},
         "mode": {"type": "string", "enum": ["hair", "reload"], "description": "hair=只搜发型（默认，快）；reload=搜任意参数（慢但通用）"},
         "kind": {"type": "string", "enum": ["hair_back", "hair_front", "hair_side", "hair_option"], "description": "mode=hair 时搜哪个发型部位，默认 hair_back"},
         "candidates": {"type": "array", "description": "mode=hair：发型 id 列表（整数）；mode=reload：部分参数对象列表，如 [{\"face\":{\"pupil\":{\"id\":3}}}]"},
         "auto_candidates": {"type": "object", "description": "mode=reload：自动从游戏分类取候选 {category, path, limit?, start?}，如 {\"category\":\"hair_front\",\"path\":\"hair.front.id\",\"limit\":8}",
                             "properties": {"category": {"type": "string"}, "path": {"type": "string"}, "limit": {"type": "integer"}, "start": {"type": "integer"}}},
         "colors": {"type": "array", "items": {"type": "string"}, "description": "mode=hair：可选，同时试这些颜色（每个 id × 每个色）"},
         "base_params": {"type": "object", "description": "mode=reload：与 kks_generate_character 同构的完整参数（会被候选覆盖）"},
         "framing": {"type": "string", "enum": ["full", "head", "face", "eyes", "torso", "bust"], "description": "渲染取景，默认 full；搜面部细节用 face/eyes"},
         "ref_crop": {"type": "string", "enum": ["full", "head", "torso", "legs"], "description": "参考图裁到哪个部位（搜脸部必须同时裁参考，否则尺度对不上）。默认 full"},
         "weights": {"type": "object", "description": "分项权重 {iou_core, iou_full, color_hist, band_color, appearance}。搜面部细节时把 appearance 拉高、其余压低"},
         "save_shots": {"type": "boolean", "description": "true=保留每个候选的渲染 PNG（带 id 的文件名）并生成接触印相 board_png"},
         "view": {"type": "string", "enum": ["front", "back", "left", "right"], "description": "默认 front"},
         "budget_ms": {"type": "integer", "description": "时间预算，hair 默认 60000，reload 默认 120000"},
         "top_k": {"type": "integer", "description": "返回前几名，默认 5"},
         "name": {"type": "string", "description": "要替换/聚焦的角色名关键字（mode=reload 必需）"}}}},
    {"name": "kks_params", "description": "参数清单（回答“到底哪些参数能调”）。what=shapes：列出游戏自带的滑条名表（面部 52 / 体型 44），并逐项核对本插件的表是否一致；what=fields：把一张卡的 face/body/hair/clothes 所有公开成员连同当前值摊平列出；what=all：两者都给",
     "inputSchema": {"type": "object", "properties": {
         "what": {"type": "string", "enum": ["shapes", "fields", "all"], "description": "默认 shapes"},
         "file": {"type": "string", "description": "what=fields 时读哪张卡，默认第一张官方预设"},
         "sex": {"type": "string", "enum": ["female", "male"], "description": "默认 female"}}}},
    {"name": "kks_live", "description": "读**场景里正在跑的角色**的实时状态（不是卡片文件）：clothesState、每个服装部位的 id/颜色、以及运行时各服装槽是否 active。用来区分“数据没写进去”和“写进去了但运行时没生效”（例如服装状态被异步恢复穿着覆盖）",
     "inputSchema": {"type": "object", "properties": {
         "name": {"type": "string", "description": "人物名关键字，可省略"}}}},
    {"name": "kks_clear", "description": "清理工作室场景里的角色（防止反复 load 造成同名模型叠在原点互相穿插，那会被误读成“穿模”）",
     "inputSchema": {"type": "object", "properties": {
         "name": {"type": "string", "description": "只清名字含此关键字的角色；省略=清空全部"}}}},
    {"name": "kks_audit", "description": "场景数字体检（开新会话/看任何渲染图之前先跑）：抽查角色数、重名、是否叠在同一点、服装状态、主相机、物理骨骼。kind=fail 时先按 problems 里的 fix 处理 —— **脏场景里的图会骗人**（同名角色叠一摞会被误看成穿模）",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "kks_txn", "description": "快照 / 回滚：把当前场景角色的完整卡片存成快照，改坏了可以一键回滚",
     "inputSchema": {"type": "object", "properties": {
         "op": {"type": "string", "enum": ["snapshot", "restore", "list", "drop"], "description": "默认 list"},
         "label": {"type": "string", "description": "snapshot 时的备注（写进文件名）"},
         "id": {"type": "string", "description": "restore/drop 用哪个快照"},
         "name": {"type": "string", "description": "人物名关键字；snapshot 拍谁、restore 替换谁"}}}},
    {"name": "kks_plugins", "description": "探测第三方制作插件（MaterialEditor / KKSABMX / KKPE / MoreAccessories / OverlayMods…）：返回 present（装没装）、bridged_op（能不能通过本桥改动）、assembly。**借道别的插件前必须先问它** —— present=false 时借道接口会明确报错；bridgeable=false 表示只能用它自己的界面改。加 assemblies 参数（如 assemblies=Mate）可列出匹配的已加载程序集，用于区分“没装”和“类型名猜错”",
     "inputSchema": {"type": "object", "properties": {
         "assemblies": {"type": "string", "description": "可选：列出名字含此串的已加载程序集（all=全部）"}}}},
    {"name": "kks_material", "description": "借道 MaterialEditor 改**任意 shader 属性**（皮肤质感/金属度/自发光/肤色叠加层…），这是 vanilla 数据模型里根本没有的维度。op=list 列渲染器与材质名；op=props 列某材质的**真实属性名与当前值**（别猜 _Color —— 实测本机 Koikano/main_skin 用的是 _overcolor1/_SpecularPower 之类）；op=set 配 material+property+value 改值（#RRGGBB=颜色，数字=浮点，路径=贴图，true/false=关键字）。返回 saved_readback 是控制器里存下的值（不是 API 的返回值）。数据进 MaterialEditor 扩展块，别人没装就看不到效果",
     "inputSchema": {"type": "object", "properties": {
         "op": {"type": "string", "enum": ["list", "props", "set"], "description": "默认 list"},
         "material": {"type": "string", "description": "材质名（op=props/set 必填，从 op=list 拿）"},
         "property": {"type": "string", "description": "shader 属性名（从 op=props 拿真实名字）"},
         "value": {"description": "新值：#RRGGBB 颜色 / 数字 / 贴图路径 / true|false 关键字"},
         "object_type": {"type": "string", "enum": ["Character", "Clothing", "Accessory", "Hair"], "description": "默认 Character（身体/脸）；服装用 Clothing 并配 slot"},
         "slot": {"type": "integer", "description": "object_type=Clothing/Accessory 时的部件号，默认 0"},
         "name": {"type": "string", "description": "人物名关键字，可省略"}}}},
    {"name": "kks_bones", "description": "借道 KKSABMX(BonemodX) **逐骨骼**缩放/位移/旋转 —— 比 44 个形状滑条细得多（能单独改某个肩、手指、脚踝）。op=list 列该角色全部骨骼名（实测 979 根，命名形如 cf_j_head / cf_j_hips / cf_j_hand_L，**是小写**）；op=set 传 bones:[{name, scale:[x,y,z] 倍率（1=不变）, length, position, rotation}]。数据进 ABMX 扩展块，别人没装就看不到效果",
     "inputSchema": {"type": "object", "properties": {
         "op": {"type": "string", "enum": ["list", "set"], "description": "默认 set"},
         "bones": {"type": "array", "description": "op=set 时的骨骼列表",
             "items": {"type": "object", "properties": {
                 "name": {"type": "string", "description": "骨骼名，如 cf_j_head"},
                 "scale": {"description": "缩放倍率 [x,y,z]，1=不变"},
                 "length": {"type": "number", "description": "长度倍率，默认 1"},
                 "position": {"description": "位移 [x,y,z]"},
                 "rotation": {"description": "旋转（欧拉角）[x,y,z]"}}}},
         "location": {"type": "string", "description": "ABMX 骨骼分类，默认自动取 Body 类"},
         "name": {"type": "string", "description": "人物名关键字，可省略"}}}},
    {"name": "kks_launch", "description": "一条调用把游戏拉起来并等到桥就绪（已在跑就直接返回）。省掉手工启动+轮询；游戏路径可用环境变量 KKS_EXE 覆盖",
     "inputSchema": {"type": "object", "properties": {
         "wait_seconds": {"type": "integer", "description": "最多等多久，默认 180"}}}},
    {"name": "kks_selftest", "description": "自测/能力锁：端点与核心实现是否齐全（丢了就 fail）+ 端到端渲染测量一次。改完接口后跑它",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "kks_doctor", "description": "健康检查：kind=ok 才代表通道可用（唯一验收标准）。同时返回构建溯源（程序集路径/时间/sha256），用于确认跑的是不是当前构建",
     "inputSchema": {"type": "object", "properties": {}}},
    {"name": "kks_capture_views", "description": "把多个视角渲染并拼成一张图返回（自动按角色包围盒取景，不会再裁掉头/脚）。默认 front+right+back。可给 from/look_at（\"x,y,z\" 世界坐标）指定任意机位；近空帧会在 warnings 里给出修正建议",
     "inputSchema": {"type": "object", "properties": {
         "views": {"type": "array", "items": {"type": "string", "enum": ["front", "back", "left", "right"]}, "description": "要拍的视角，默认 [front,right,back]"},
         "framing": {"type": "string", "enum": ["full", "bust", "torso", "head", "face", "eyes"], "description": "取景，默认 full。face/eyes = 面部/眼部特写（查妆容、眼型、穿模）"},
         "name": {"type": "string", "description": "人物名关键字，可省略"},
         "from": {"type": "string", "description": "任意机位：相机世界坐标 x,y,z（给了它就用它，忽略 framing）"},
         "look_at": {"type": "string", "description": "任意机位：看向的世界坐标 x,y,z"},
         "panel_width": {"type": "integer", "description": "每格宽度（像素），默认 480"},
         "file": {"type": "string", "description": "文件名，可省略"}}}},
    {"name": "kks_help", "description": "查询使用说明（工作流 / 参数含义 / 验收标准 / 常见坑）。不确定怎么用或要按参考图生成时先问它",
     "inputSchema": {"type": "object", "properties": {
         "topic": {"type": "string", "description": "verify 验收纪律（先跑 kks_audit！） | workflow 工作流 | params 参数（含 face 39 / body 25 全成员面、异色瞳、多套服装） | acceptance 验收 | pitfalls 常见坑 | reference 按参考图校准 | polish 成品自检（穿模/皮肤/面部） | plugins 借道第三方插件（MaterialEditor/ABMX 怎么用、代价是什么） | all 全部", "default": "workflow"}}}},
    {"name": "kks_activity", "description": "查看 AI 桥最近 50 条操作记录（同时在游戏画面左上角 HUD 和 UserData/AICharBridge/activity.log 中可见，供用户监督）",
     "inputSchema": {"type": "object", "properties": {}}},
]

# ---------------- 工具执行 ----------------

def call_tool(name, args):
    if name == "kks_status":
        return bridge("GET", "/status")
    if name == "kks_activity":
        return bridge("GET", "/activity")
    if name == "kks_inspect_character":
        a = args or {}
        return bridge("GET", "/inspect?file=%s&sex=%s" % (
            urllib.parse.quote(a.get("file", "")), a.get("sex", "female")), timeout=60)
    if name == "kks_focus_camera":
        return bridge("POST", "/focus", body=args or {}, timeout=30)
    if name == "kks_list_characters":
        return bridge("GET", "/characters")
    if name == "kks_list_presets":
        return bridge("GET", "/presets")
    if name == "kks_list_options":
        cat = (args or {}).get("category")
        path = "/options?category=" + cat if cat else "/options"
        return bridge("GET", path, timeout=60)
    if name == "kks_generate_character":
        args = dict(args or {})
        args.setdefault("sex", "female")
        see = bool(args.pop("see", False))
        r = bridge("POST", "/generate", body=args, timeout=90)
        if not see or not r.get("ok"):
            return r
        # see=true：同一次工具调用里继续拍一张三视图拼图并一起返回（模型只看到一次调用）
        cap = bridge("POST", "/capture", body={
            "framing": "full",
            "views": ["front", "right", "back"],
            "focus": True,
        }, timeout=120)
        r["capture"] = {k: v for k, v in cap.items() if k != "file"}
        if cap.get("ok") and os.path.isfile(cap.get("file", "")):
            try:
                with open(cap["file"], "rb") as f:
                    data = f.read()
                if len(data) < 8 * 1024 * 1024:
                    return {"_image": base64.b64encode(data).decode("ascii"),
                            "_path": cap["file"], "_meta": r,
                            "_note": "生成结果 + 三视图拼图（同一次调用）"}
            except Exception:
                pass
        return r
    if name == "kks_reference":
        a = dict(args or {})
        if not a.get("file"):
            return {"ok": False, "error": "需要 file"}
        return bridge("POST", "/reference", body=a, timeout=120)
    if name == "kks_compare":
        a = dict(args or {})
        a.setdefault("view", "front")
        a.setdefault("framing", "full")
        r = bridge("POST", "/compare", body=a, timeout=120)
        if r.get("ok") and os.path.isfile(r.get("montage", "")):
            try:
                with open(r["montage"], "rb") as f:
                    data = f.read()
                if len(data) < 8 * 1024 * 1024:
                    meta = dict(r)
                    meta.pop("montage", None)
                    return {"_image": base64.b64encode(data).decode("ascii"),
                            "_path": r["montage"], "_meta": meta,
                            "_note": "左=参考图 右=我的渲染；分数见下"}
            except Exception:
                pass
        return r
    if name == "kks_fit":
        a = dict(args or {})
        return bridge("POST", "/fit", body=a, timeout=300)
    if name == "kks_audit":
        return bridge("GET", "/audit", timeout=120)
    if name == "kks_plugins":
        a = args or {}
        q = ""
        if a.get("assemblies"):
            q = "?assemblies=" + urllib.parse.quote(str(a["assemblies"]))
        return bridge("GET", "/plugins" + q, timeout=60)
    if name == "kks_material":
        return bridge("POST", "/material", body=dict(args or {}), timeout=120)
    if name == "kks_bones":
        return bridge("POST", "/bones", body=dict(args or {}), timeout=120)
    if name == "kks_txn":
        return bridge("POST", "/txn", body=dict(args or {}), timeout=120)
    if name == "kks_launch":
        a = args or {}
        return launch_game(int(a.get("wait_seconds", 180)))
    if name == "kks_clear":
        a = args or {}
        return bridge("POST", "/clear", body={"name": a.get("name", "")}, timeout=60)
    if name == "kks_params":
        a = args or {}
        return bridge("GET", "/params?what=%s&file=%s&sex=%s" % (
            urllib.parse.quote(a.get("what", "shapes")), urllib.parse.quote(a.get("file", "")),
            urllib.parse.quote(a.get("sex", "female"))), timeout=120)
    if name == "kks_live":
        a = args or {}
        return bridge("GET", "/live?name=%s" % urllib.parse.quote(a.get("name", "")), timeout=60)
    if name == "kks_selftest":
        return bridge("GET", "/selftest", timeout=120)
    if name == "kks_doctor":
        return bridge("GET", "/doctor", timeout=120)
    if name == "kks_capture_views":
        a = dict(args or {})
        a.setdefault("views", ["front", "right", "back"])
        a.setdefault("framing", "full")
        r = bridge("POST", "/capture", body=a, timeout=120)
        if r.get("ok") and os.path.isfile(r.get("file", "")):
            try:
                with open(r["file"], "rb") as f:
                    data = f.read()
                if len(data) < 8 * 1024 * 1024:
                    meta = dict(r)
                    meta.pop("file", None)
                    return {"_image": base64.b64encode(data).decode("ascii"),
                            "_path": r["file"], "_meta": meta}
            except Exception:
                pass
        return r
    if name == "kks_help":
        return {"ok": True, "topic": (args or {}).get("topic", "workflow"),
                "text": HELP.get((args or {}).get("topic", "workflow"), HELP["workflow"])}
    if name == "kks_screenshot":
        r = bridge("POST", "/screenshot", body=args or {}, timeout=30)
        if r.get("ok") and os.path.isfile(r.get("file", "")):
            try:
                with open(r["file"], "rb") as f:
                    data = f.read()
                if len(data) < 8 * 1024 * 1024:
                    return {"_image": base64.b64encode(data).decode("ascii"), "_path": r["file"]}
            except Exception:
                pass
        return r
    return {"ok": False, "error": "unknown tool " + name}

def tool_result(payload):
    """包装成 MCP tools/call 结果"""
    content = []
    if isinstance(payload, dict) and "_image" in payload:
        content.append({"type": "image", "data": payload["_image"], "mimeType": "image/png"})
        summary = payload.get("_note") or ("截图已保存: " + payload.get("_path", ""))
        meta = payload.get("_meta")
        if meta is not None:
            summary += chr(10) + json.dumps(meta, ensure_ascii=False, indent=1)
        content.append({"type": "text", "text": summary})
        return {"content": content, "isError": False}
    text = json.dumps(payload, ensure_ascii=False, indent=1)
    is_err = isinstance(payload, dict) and payload.get("ok") is False
    content.append({"type": "text", "text": text})
    return {"content": content, "isError": is_err}

# ---------------- MCP 协议 ----------------

def handle(msg):
    method = msg.get("method")
    mid = msg.get("id")
    is_notify = mid is None

    def reply(result=None, error=None):
        if is_notify:
            return None
        out = {"jsonrpc": "2.0", "id": mid}
        if error is not None:
            out["error"] = error
        else:
            out["result"] = result
        return out

    if method == "initialize":
        return reply({
            "protocolVersion": msg.get("params", {}).get("protocolVersion", "2024-11-05"),
            "capabilities": {"tools": {}},
            "serverInfo": {"name": SERVER_NAME, "version": SERVER_VERSION},
        })
    if method == "notifications/initialized":
        return None
    if method == "ping":
        return reply({})
    if method == "tools/list":
        return reply({"tools": TOOLS})
    if method == "tools/call":
        params = msg.get("params", {})
        try:
            payload = call_tool(params.get("name", ""), params.get("arguments") or {})
            return reply(tool_result(payload))
        except Exception as e:
            return reply({"content": [{"type": "text", "text": "工具执行异常: %r" % e}], "isError": True})
    if method and method.startswith("notifications/"):
        return None
    return reply(None, {"code": -32601, "message": "method not found: %s" % method})

def emit(obj):
    if obj is None:
        return
    data = json.dumps(obj, ensure_ascii=False).encode("utf-8")
    try:
        sys.stdout.buffer.write(data + b"\n")
        sys.stdout.buffer.flush()
    except OSError:
        sys.exit(0)  # 客户端关了管道

def main():
    # stdin 按行读（同时兼容 LSP Content-Length 帧）
    pending = None
    for raw in sys.stdin.buffer:
        line = raw.strip()
        if not line:
            continue
        if line.startswith(b"Content-Length:"):
            pending = int(line.split(b":", 1)[1].strip())
            continue
        if pending is not None:
            # LSP 模式：下一行（可能同批）是定长 JSON
            try:
                emit(handle(json.loads(line[:pending].decode("utf-8"))))
            except Exception as e:
                emit({"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "parse error: %r" % e}})
            pending = None
            continue
        try:
            emit(handle(json.loads(line.decode("utf-8"))))
        except Exception as e:
            emit({"jsonrpc": "2.0", "id": None, "error": {"code": -32700, "message": "parse error: %r" % e}})

if __name__ == "__main__":
    main()
