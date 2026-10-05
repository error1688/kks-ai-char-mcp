# KKS_AICharMCP

让 AI 在 **Koikatsu Sunshine / CharaStudio** 里**按描述或动漫图生成人物卡**，并且
**自己截图核对效果**——不是让模型凭空猜参数，而是把"像不像"变成可测量的数字。

```
AI（视觉 + 推理）
  │  MCP (stdio JSON-RPC)
  ▼
kks_chara_mcp.py            零依赖 Python 单文件
  │  HTTP (仅 127.0.0.1:24380)
  ▼
KKS_AICharBridge.dll        BepInEx 插件，所有游戏操作在主线程执行
  │
  ├─▶ 生成人物卡 / 加载工作室 / 离屏截图 / 服务端搜索
  └─▶ 游戏左上角 HUD + activity.log（用户实时监督）
```

## 为什么这样设计

普通"AI 生成人物"插件的问题是**模型看不见结果**：改完参数不知道到底生效没有、像不像，
于是反复瞎猜。本项目围绕这一点做了三件事：

- **可观察**：离屏渲染多视角拼图，拍前等画面稳定，返回覆盖度与警告，空帧不会冒充成功。
- **可测量**：参考图切格 → 剪影 IoU + 分带主色 + 前景色彩 → 一个总分，能横向比较候选。
- **可搜索**：`kks_fit` 在**服务端**逐个试候选（换发型/换任意参数）、渲染、打分、排序，
  整轮不消耗模型轮次，最后把最优应用回角色。
- **可回滚**：`kks_audit` 先体检场景，`kks_txn` 快照/回滚，改坏能退回去。

## 快速开始

### 1. 前置

- Koikatsu Sunshine（或 CharaStudio）+ **BepInEx 5.x**
- Python 3（MCP 服务器只用标准库）
- 任意 MCP 客户端（ZCode / Claude Desktop / …）

### 2. 编译插件

```bat
set KKS_HOME=E:\game\KKS
KKS_AICharMCP\build_bridge.bat
```

脚本先 `taskkill` 掉游戏再调用 .NET Framework 自带的 `csc.exe`，产物是
`%KKS_HOME%\BepInEx\plugins\KKS_AICharBridge.dll`。

> **务必确认 DLL 的时间戳/大小变了。** DLL 被占用时 csc 会以
> `CS0016 ... 无法在使用用户映射区域打开的文件上执行` 失败，很容易被漏看，
> 曾经因此让源码和 DLL 不一致——`/doctor` 的构建溯源（程序集 sha256 + 编译时间）就是为此加的。

### 3. 注册 MCP 服务器

在 MCP 客户端配置里加一条（路径改成你自己的）：

```json
{"mcp": {"servers": {"kks-chara": {
  "command": "python",
  "args": ["E:\\game\\KKS\\KKS_AICharMCP\\kks_chara_mcp.py"]
}}}}
```

### 4. 跑起来

先启动游戏（插件随 BepInEx 自动加载），然后：

```
kks_doctor          → kind=ok 才算通道可用（唯一验收标准）
kks_launch          → 或者让 AI 一条调用把游戏拉起来并等桥就绪
kks_status          → 看进程/场景/FPS
```

游戏没开时工具会返回明确的"无法连接游戏"，不会静默失败。
端口可在插件配置面板（F1）改，改后用环境变量 `KKS_BRIDGE_PORT` 同步给 MCP；
游戏路径可用 `KKS_EXE` 覆盖。

## MCP 工具

25 个，按用途分组。

**状态与诊断**

| 工具 | 作用 |
|---|---|
| `kks_doctor` | **唯一验收标准**：`kind=ok` 才代表通道可用。逐项检查 + 构建溯源（程序集路径/编译时间/sha256） |
| `kks_status` | 游戏状态（进程/场景/FPS/是否在工作室） |
| `kks_selftest` | 能力锁：PROBE 校验全部端点是否在册、核心实现是否齐全、端到端渲染测量一次 |
| `kks_audit` | **场景数字体检（看图前先跑）**：角色数/重名/两两距离/服装状态/主相机/物理；`kind=fail` 时按 `problems[].fix` 处理 |
| `kks_activity` | 最近 50 条操作记录 |
| `kks_launch` | 一条调用启动游戏并等桥就绪 |

**生成与读取**

| 工具 | 作用 |
|---|---|
| `kks_generate_character` | 基底卡 + 覆盖参数 → 保存 PNG 卡；`load:true` 加入工作室（默认替换同名角色，`add:true` 才并排） |
| `kks_inspect_character` | 读一张卡的真实内容：姓名/人格/五官、**面部 52 + 体型 44 个滑条**、发型、每套服装的 id/颜色/状态、饰品槽 |
| `kks_live` | 读**场景里正在跑的角色**（非卡片文件）：`clothesState`、各部位 id/颜色、运行时各槽是否 active——用来区分"没写进去"和"写进去了但没生效" |
| `kks_list_characters` | 列出 UserData 已存的人物卡 |
| `kks_list_presets` | 列出官方预设卡（44 张，按人格命名：お嬢様=大小姐、ギャル=辣妹…），**推荐作基底** |
| `kks_list_options` | 列出全部可用 id 与名称（含 Mod）：发型/服装/五官/饰品；可用 `category` 只取一类 |
| `kks_params` | `shapes` 列出游戏自带滑条名表（面 52 / 体 44）并核对本插件表是否一致；`fields` 把一张卡的所有公开成员连同当前值摊平列出 |

**观察（截图）**

| 工具 | 作用 |
|---|---|
| `kks_capture_views` | **推荐**：一次调用把多视角渲染拼成一张图。按包围盒自动取景（不裁头脚）、**离屏渲染**（不占用户视口、不带 UI）、拍前等画面稳定。可给 `from`/`look_at` 指定任意机位；空帧在 `warnings` 里给修正建议 |
| `kks_screenshot` | 单张截图，走用户的相机（兼容旧用法） |
| `kks_focus_camera` | 把相机移到人物正前方保持 `hold` 秒后交还控制权 |

**数值闭环**

| 工具 | 作用 |
|---|---|
| `kks_reference` | 载入参考图并自动切格（三视图→3 格），返回每格剪影形状、包围盒、分带主色 |
| `kks_compare` | 我的某视角 vs 参考图某格 → `iou_core`/`iou_full`/`color_hist`/`band_color`/`appearance` + 总分 + **并排对照图** |
| `kks_fit` | **服务端搜索**：逐个试候选、渲染、打分，返回候选榜并把最优应用回角色。`mode=hair`（运行时热换，快）/ `mode=reload`（写临时卡+替换角色，可搜**任意**参数路径）。`save_shots=true` 留下每个候选 PNG + 接触印相 `board_png` |

**维护**

| 工具 | 作用 |
|---|---|
| `kks_clear` | 清空工作室角色（可传 `name` 只清某一类） |
| `kks_txn` | 快照/回滚：`snapshot` / `list` / `restore` / `drop` |
| `kks_help` | 长尾文档：`workflow` / `params` / `acceptance` / `verify`（证据纪律）/ `pitfalls` / `reference` / `plugins` |

**借道第三方插件**

| 工具 | 作用 |
|---|---|
| `kks_plugins` | 探测制作类插件：`present`（装没装）+ `bridged_op`（能否经本桥改）。**借道前先问它** |
| `kks_material` | 借 MaterialEditor 改任意 shader 属性（`op=list/props/set`；`props` 必须先用，别猜属性名） |
| `kks_bones` | 借 KKSABMX 逐骨骼缩放/位移/旋转（`op=list` 拿 979 根骨骼名，`op=set` 改） |

## 生成参数

- **通用**：`sex`（female/male）、`base`（基底卡名）、`save_as`、`load`、`overwrite`
- **`parameter`**：`fullname`（含空格自动拆姓+名）、`nickname`、`personality`(0~43)、生日、血型
- **`face`**：`pupil`{id, baseColor, subColor}（双眼同色）、眉色/眼线/唇色/痣、`headId`；
  `shape:{滑条名: 0~1}` 按名字设面部滑条（52 个），或 `shapeValueFace` 数组
- **`body`**：`skinMainColor`、`bustSoftness`、`bustWeight`；`shape:{滑条名: 0~1}`（44 个）；
  `normalize`（默认开）把易穿模滑条拉回安全带
- **`hair`**：`back`/`front`/`side`/`option` → `{id, baseColor, startColor, endColor, length}`
  （`startColor` = 发根端，`endColor` = 发梢端，可做渐变）
- **`clothes`**：`top`/`bot`/`bra`/`shorts`/`gloves`/`panst`/`socks`/`shoes` →
  `{id, color0~3, pattern0~3, patternColor0~3, state}`
  - `id` 从 `kks_list_options` 查，**0 = 无（不穿）**
  - `state`：0=穿着（默认）1=半脱 2/3=脱下；也可用 `"wear": false`
  - **要纯色必须 `"pattern0": 0`** 清掉花纹叠加层，否则颜色被花纹盖住（见下）
- **`accessories`**：数组，`{type, id, colors, slot?, parent?}`；`type` 为
  `hair`/`head`/`face`/`neck`/`body`/`waist`/`leg`/`arm`/`hand`（也接受 1~10）；
  `clear_accessories: true` 先清空基座卡原有饰品槽（官方卡常把槽占满）
- 颜色一律 `#RRGGBB`（也支持 `#RGB` / 8 位）。返回里 `applied` / `skipped`
  **逐项列出有没有生效**，不合法输入会给出可读原因或 `did_you_mean`，不会静默产出残缺角色。

> KKS 的卡片结构：`face`/`body`/`hair` 在 `ChaFile.custom`，服装在
> `coordinate[status.coordinateType]`——与 KK 原版不同，插件已适配。

## 推荐工作流

1. **看图**：提取发色（渐变要分 base/start/end）、瞳色、肤色、服装主辅色、鞋袜、饰品
2. **查**：`kks_list_presets` 挑人格相近的官方卡当基底；`kks_list_options` 找最接近的 id
3. **生成**：`kks_generate_character`（`clear_accessories:true` 腾饰品槽）→ `load:true` 进场景
4. **体检**：`kks_audit` 确认场景里只有目标角色
5. **验**：`kks_capture_views` 拍三视图 + 局部特写；`kks_compare` 拿数字，看图定生死
6. **调**：不像就改参数重生成（`overwrite:true`）；细调交给 `kks_fit` 搜索

**示例**（鲸鱼女仆，按三视图）：渐变发 `startColor=#16265C`（根）→ `endColor=#86D9F2`（梢）；
女仆装 `top=218 古典女仆A` + `bot=208 古典女仆裙A`（**围裙色是 `color1`，要设白色**）；
白裤袜 `panst=1`；呆毛 `hair.option=8 長触覚`。

## 桥接 HTTP 端点

25 个，MCP 工具是它们的薄封装（`kks_launch` 除外，它只在本机拉起进程）。

| 端点 | 说明 |
|---|---|
| `GET /status` `/characters` `/presets` `/options` `/options_ids` `/activity` | 状态与资源清单 |
| `GET /inspect` `/params` `/live` | 卡片内容、参数清单、运行时状态 |
| `GET /audit` `/doctor` `/selftest` `/probe` | 体检、构建溯源、能力锁、单字段可落盘验证 |
| `POST /generate` `/clear` `/txn` | 生成、清场、快照回滚 |
| `GET /capture` `POST /capture` `/screenshot` `/focus` | 截图与相机 |
| `POST /reference` `/compare` `/fit` | 数值闭环 |
| `GET /plugins` `POST /material` `/bones` | 借道第三方插件（探测 / 材质 / 骨骼） |

## 设计要点（把踩过的坑说清楚）

这一节是本项目真正的内容——每一条都是实测踩出来的，而且**多数是"看渲染图看不出、必须逐像素或读数字"才发现的**。

### 证据纪律（先看数字，再看图）

从 dsh-blender-plugin 借鉴并固化（写在 `kks_help verify` 里）：

1. **看图之前先跑 `kks_audit`**——脏场景里的图会骗人（见"场景卫生"）。
2. **先数字后图像**：能用指标判断的先用指标，图片是补充而不是首选。
3. **图有预算**：每轮 ≤6 张、每张 ≤800×450，避免烧掉上下文。
4. **断言先于看图**：先写下"我预期看到 X"，再看图核对，防止被任意一张图牵着走。
5. **三态判定**：PASS / FAIL / **UNKNOWN**——看不清就说看不清，不要猜。
6. **`ok=true` 不等于图里有东西**：空场景离屏渲染出**纯色空帧**（8KB、单一灰阶），
   而返回里 `ok=true`、`settled=true`。**判据是 `panels[].coverage` 与 `warnings`。**
   我差点把这个当成代码回归去查。

### 测量必须可复现，否则搜索是噪声

一开始"搜眼型"完全搜不动：候选分数全都是 0.4742。逐像素对比才看清——**噪声比信号大 100 倍**：

| 对比 | meanL1 | 强差异像素占比 |
|---|---|---|
| 只差眼型的两个候选（冻结物理前） | 13.3 | 14.2% |
| 同一参数两次渲染（冻结物理前） | ~13 | ~14% |
| 同一参数两次渲染（**冻结物理后**） | **0.04** | 0.06% |
| 只差眼型的两个候选（冻结物理后） | 0.10 | 0.17% |

原因：角色每轮都是删掉重建的，**头发 DynamicBone 从头摆动、眼睛还在眨**，
于是"除了被搜的那个参数，画面还有 14% 的像素在变"。

→ 修法：测量渲染前停掉所有 `DynamicBone`（`enabled=false`）并强制睁眼
（反射 `ChangeEyesBlinkFlag(false)` / `ChangeEyesOpen(true)`），渲完还原。返回里的
`frozen:true` 就是这个状态。修完后同参数重复渲染噪声底降到 0.04，
发型搜索的区分度变成 0.536~0.562 的明显梯队。

**但眼型这种极细差异仍只有噪声底 2~4 倍——够排序、不够定胜负。**
所以这类判断必须靠 `save_shots` 的接触印相看图，不能只信"0.562 vs 0.536"。

### Unity 的像素数组是自下而上的

`Texture2D.GetPixels32()`（和 Blender 的 `image.pixels` 一样）**第 0 行是图像底部**。
按"Y0 = 图顶"写会得到上下颠倒的一切：`ref_crop:"head"` 裁出来是**鞋和腿**
（于是眼型搜索全程在拿"我的脸"和"参考图的鞋"打分），剪影 ASCII 打出来是倒的，
分带主色 band 0 标着"头"实际是"腿"。
→ 统一改成从 `im.Y1`（头顶）往下取。

### MSAA 在离屏 RT 上不生效 → 改用超采样

给离屏 `RenderTexture` 设 `antiAliasing=4`，和 `AA=1` 渲染出来**逐像素完全相同**
（meanAbsDiff=0.000）——因为游戏质量设置里 AA 是关的。
→ 改成**渲染 2 倍再盒式降采样**（`ss` 参数，默认 2），不依赖 MSAA 支持，实测确实改变输出（4.1% 像素）。
（MSAA 的 RT 也不能直接 `ReadPixels`，必须先 blit resolve。）

### 官方人物卡才是参数基准

一度自己"发明"参数，越改越丑。改为**先读官方卡的实测值**：`kks_inspect_character`
能读出 52 个面部滑条后，结论直接把之前的假设推翻了：

| 我原本的判断 | 44 张官方卡的实际数据 |
|---|---|
| 基座卡把 `FaceLowW`/`ChinTipW` 压到 0 是"极端值/缺陷" | `FaceLowW=0 / ChinW=0.286 / ChinTipW=0` 是**大多数**官方卡的取值，是游戏自家风格 |
| 把 `ChinTipW` 推到 0.45~0.5 让下巴变圆 | `ChinTipW` 在**全部 44 张**里都是 0——设计师从不碰它；推到 0.5 会把低模下巴推成**多面体**，正是用户看到的"坑坑洼洼" |
| 自己推一组"单调递增"的下颌值 | 直接用本来就圆的原版卡当基底：`天真爛漫 FaceLowW=0.5 / ChinW=0.325 / ChinTipW=0` |

**结论：基底卡是官方成品，只改参考图明确要求的（瞳色/发色/服装色），其余继承。**
这条也推广到皮肤和妆容（见"成品自检"）。

### 场景卫生：同名角色叠一摞 = 假穿模

`load:true` 早期只是 `AddFemale`，**不删旧的**。反复试验时同一张卡被加载十几次、
全部落在原点，用户一眼看出"模型重叠了"。后果比看起来严重：多个身体互相穿插，
腰侧冒出"皮肤三角"、下巴附近出现"凹槽"——我在腰腹白查了一轮（先怀疑围裙覆盖缝、
又收窄腰腹、又换女仆装），**收窄腰腹完全没用，因为衣服也跟着身体缩放**；
在干净场景 6 倍放大复验，同一套衣服**一个皮肤像素都没有**。

修法（两道保险）：`load:true` 默认**替换同名角色**；新增 `kks_clear` 一键清场；
生成返回带 `replaced_same_name` 与 `scene_chars`。
`kks_audit` 实测能抓出这种场景（`overlap(0.000m)` + `duplicate_names`）。

### 异步穿着竞态

`/generate` 里"恢复穿着"是 `StartCoroutine` 发射即忘，HTTP 响应立刻返回。
活动日志实锤：`17:14:42 截图` / `17:14:45 才 SetClothesStateAll`——
拍到的是"裤袜/饰品还没挂上"的中间态。
→ 把补齐穿着放进渲染路径（`EnsureDressed`），让稳定判定去等衣服真的加载完。

### 协程异常 = 调用方干等超时

每个异步接口都是"HTTP 线程把协程排进主线程队列，然后 `Task.Wait(30~300s)`"。
而 C# 迭代器协程**不能把 yield 放进 try/catch**，于是协程前半段（取景、加载参考图、
稳定等待循环）没有异常保护——那里一抛异常，`TaskCompletionSource` 永远不完成，
调用方干等满超时、还拿到误导性的"超时"错误，同时现场（临时灯/离屏相机/RT/
被禁用的相机控制权/测试中的发型/临时卡）全部残留。这就是反复出现的"一直报错/卡住了"。

→ 修法：新增 `ShotSettledSafe` 包装器（逐帧推进内层协程、把异常抓进 `ShotBox.Error`），
所有渲染调用点改走它；各协程的取景/补光/稳定循环分别加保护；搜索类协程用
try/finally 兜底（异常时还原发型、清理临时卡，并把异常落回 tcs）。
坏参考名从"干等 300 秒"变成 0.1 秒返回带指引的错误。

### 穿模与皮肤不平整的根因

| 现象 | 根因 | 对策 |
|---|---|---|
| 肢体穿过衣服 | 乳晕/乳头滑条（`AreolaBulge`/`NipWeight`/`NipStand`）顶穿薄衣服；体型极端值同理 | `body.normalize`（默认开）拉回安全带，乳晕/乳头压到 0~0.12 |
| 腿从袜子里顶出 | 基座卡腿型极端（`ThighLowW=0.078`/`Calf=0.180`），而袜子是独立网格不跟随 | 腿部滑条（`Thigh*`/`Knee*`/`Calf`/`Ankle*`）纳入 normalize 并单独夹到 0.32~0.80 |
| 皮肤不平整 | `detailPower`（凹凸贴图）> 0.05、`drawAddLine`（肌肉线）、`paintId`、`sunburnId` | `smooth_skin:true` 一键关闭并收敛光泽 |
| 面部难看 | 多数是"多改了不该改的"：`eyelineColor` 太深会变黑带盖住眼珠；覆盖 `pupil.id`/`hlUpId`/`eyebrowId` 会让高光和眼型变怪 | 只改参考图明确要求的项，用 `kks_inspect_character` 读回 `face_notes` 定位 |

**成品自检要拍局部**，整体截图看不出问题：

| 看什么 | 怎么拍 |
|---|---|
| 脸/妆容 | `capture_views {framing:"face"}` |
| 眼型/瞳色/高光 | `framing:"eyes"`（相机锚在头部骨骼，约 0.13m 视野） |
| 胸口/袖口穿模 | `framing:"bust"` |
| 腰腹/裙腰 | `framing:"torso"` |
| 任意部位 | `from`/`look_at` 给世界坐标 |

### 游戏内"修改器"能改更多：查清差集在哪

一个很直接的观察：游戏内制作器 + 装的一堆插件，能改的东西明显比本工具多。查清后发现差距分三层，
性质完全不同——**只有第三层是真的缺功能**。

**第一层：字段早就能写，只是从没暴露（纯文档问题）。** `Applier.Apply` 是按成员名**反射**写入的，
任何公开成员都能写。实测官方卡成员面：`face` **39 个**、`body` **25 个**，而文档只写了约 8 + 7 个。
漏掉的包括眼白 `whiteId/whiteBaseColor`、高光上下分开（`hlUp*` 之外还有 `hlDown*`）、
上下眼线分开 + 粗细 `eyelineUpId/eyelineDownId/eyelineUpWeight`、泪痣 `moleId`、
瞳孔位置大小 `pupilX/Y/Width/Height`、八重歯 `doubleTooth`、骨骼类型 `typeBone`、
体毛 `underhairId`、身体涂装两槽 `paintId[]/paintColor[]`、乳首 `nipId/nipGlossPower/areolaSize`、
描画顺序 `foregroundEyebrow/foregroundEyes`……现在全写进了 `kks_help params`。

**第二层：转换器缺类型。** `Convert()` 原来只认 float/int/byte/bool/string/Color/float[]/enum，
于是 `Vector2/3/4` 与 `bool[]` 一律报"不支持的目标类型"。补上后一次解锁：
花纹的 `tiling/offset/rotate`（Vector2）、饰品位移 `addMove` 与发区 `pos/rot/scl`（Vector3）、
痣与涂装定位（Vector4）、服装 `hideOpt` 与饰品 `showAccessory`（bool[]）。

**第三层：结构性缺口。**
- **多套服装**：卡片有 4 套坐标，之前只写当前那套 → 新增 `coordinate:N` 与 `all_coordinates:true`。
  顺带查清 `clothesState` 只有 9 项、**不分坐标**（全局），所以写别的套数时不动状态。
- **异色瞳**：双眼数据本来就独立（`face.pupil[2]`），之前却把同一份写给两只眼 →
  现在 `pupil` 传数组即左右分设。实测渲染确实不同色（右半 673 红/0 绿，左半以绿为主）。
  `isPupilSameSetting` 是只读属性，写它会明确报"属性只读"，但不影响异色瞳生效。
- **人格问卷**：`attribute/awnser/denial/interest` 是嵌套对象 → 给 Applier 加了嵌套递归
  （只对引用类型递归：值类型拿到的是副本，写进去不生效）。

**顺带抓到一个真 bug**：`ApplySmoothSkin` 的面部那段是**无条件**执行的（只有 body 段受
`smooth_skin` 控制），于是每生成一次就把官方脸的 `detailPower` 清零——而官方 お嬢様 实测是
**0.449**（脸部本来有凹凸细节）；它还跑在通用 apply 之后，用户显式传的值也会被覆盖。
改成整段受 `smooth_skin` 控制。验证（官方卡当基底）：默认 → 继承 0.449；`smooth_skin:true` → 0。

### 借道第三方插件

本机装了约 140 个插件，十几个扩展的是人物制作能力。逐个体检（Cecil 读 IL 元数据）后分两类：
有公开 API 的、和只有 UI 的。接通的走**运行时反射**——不硬引用它们的 DLL（没装的机器上会编译失败），
装了就用，没装就如实报错。

| 端点 | 插件 | 能力 |
|---|---|---|
| `/material` · `kks_material` | MaterialEditor | 改**任意 shader 属性**：皮肤质感、光泽、颜色叠加层（vanilla 数据模型里没有的维度） |
| `/bones` · `kks_bones` | KKSABMX (BonemodX) | **逐骨骼**缩放/位移/旋转，比 44 个形状滑条细得多（实测 979 根骨骼） |

两个都按"改完拍图看 md5 变没变"验证，而不是只信返回值：`_overcolor1` 改绿 → 渲染 md5 改变、
读回 `RGBA(0,1,0,1)`；`cf_j_head` 放大 1.35 → 渲染 md5 改变。

**坑（值得单独记）**：`MaterialAPI.SetColor(gameObject, materialName, ...)` 这个静态入口对
**角色材质一律返回 false**——属性明明存在（`HasProperty=true`）、值读回原样、渲染也不变，
因为它只认 MaterialEditor 自己管理的材质副本。正确入口是角色控制器的
`MaterialEditorCharaController.SetMaterialColorProperty(slot, ObjectType, material, prop, value, go, setProperty)`。
另外 `op=props`（列真实属性名）是必需的：本机皮肤 shader 是 `Koikano/main_skin`，
属性叫 `_overcolor1` / `_SpecularColor` / `_SpecularPower`——猜 `_Color` 只会得到 false。

**探测到但没接**（`/plugins` 里标 `bridgeable=false`，避免"检测到插件"被误读成"这能力能用"）：
MoreAccessories（槽位扩展，接口挂在 UI 实例上）、KKPE、MovUrAcc（数据只在 ExtSave 里、由 UI 驱动，
且本机未加载）、OverlayMods（叠图/纹身）、MoreOutfits（套装槽位）。

**代价（必须知道）**：这些数据都存在 `KKS_ExtensibleSaveFormat` 扩展块里。借道生成的卡，
在**没装对应插件的人**那里会退化成 vanilla 外观。

### "改了参数没效果"怎么排查

按这个顺序，能定位到具体哪一层断掉：

1. 看返回的 `applied` / `skipped` —— 参数有没有被接受
2. `kks_inspect_character` —— 有没有**写进卡片**
3. `kks_live` —— 有没有**在运行时生效**（区分"数据没写进去"和"写进去了但没生效"）
4. `GET /probe?file=卡.png` —— 单个字段改→存→回读，确认能不能落盘

已经踩到的具体例子：

- **`face.shape` 被无声丢弃**：`Applier` 只写了 `body` 的专门处理函数，没有 `face` 的，
  于是 `face.shape` 既不进 applied 也不进 skipped，**完全无声**——52 个面部滑条一个都调不了。
- **`clear_accessories:true` 单独传时无效**：清空逻辑写在"必须同时传 accessories"的分支里，
  于是只清空不加饰品时直接 return，基座卡的残留饰品留在卡上。
- **衣服颜色改了没变**：颜色其实写对了，但 `colorInfo[0]` 上挂着 **pattern 花纹叠加层**盖住了。
  → 新增 `pattern0~3` 通道，`"pattern0":0` 得到纯色。
- **`shoes` 只写了 `shoes_inner`**：鞋是两个部件（`parts[7]`/`parts[8]`），**外观在 `parts[8]`**。
  只写 `parts[7]` 时卡片数据变了但画面不变（换 4 个鞋 id 渲染出同一双鞋）。
- **`/inspect` 读不出滑条**：直接访问 `fc.shapeValueFace` 在该路径上取到 null（反射读得到），
  于是 `face_shape` 一直是空的——这也是我当年只能"猜"滑条值的原因。
  修法：`ReadFloatArray()` 属性/字段都试、反射优先。
- **饰品分类号整体错位一位**：`OptionCats` 饰品段漏了开头的 `120`，导致
  `category=ao_head` 实际读的是 `face` 分类——AI 拿到的 id 属于别的分类，生成时又被校验拒掉。
- **接触印相一直为空**：按 `iter` 取候选图时判了 `is double`，但 JSON 里是 int，
  于是 `cells` 恒空、`board_png` 恒 null。→ `Convert.ToInt32` 兜住两种数字类型。
- **`appearance` 指标对脸没用**：原来是 48×48 灰度点采样，眼睛只占几个格子。
  → 改成 64×64 **前景-only 彩色**块均值，并加了 `framing:"eyes"` 特写。

## 数值闭环的口径

```
参考图 ──/reference──> 切格 + 剪影/颜色指标
角色  ──/capture──> 三视图 ──/compare──> 分数 + 并排对照图 ──/fit──> 候选榜 ──> 回到最优
```

- **切格**：先按空列粗切；切不开时（三视图常被飘带/尾巴连住）在"墨量最低的列"下刀。
  可 `panels:3` 或 `ranges:"0-420,430-840"` 手工指定。
- **剪影**：离屏渲染用固定底色做精确比对；参考图用**从四边洪泛**——被深色线稿围住的
  白色区域（白围裙/白衣）不会被误判成背景挖掉，这是纯色阈值做不到的。
- **打分**：`total = 0.35*iou_core + 0.10*iou_full + 0.15*color_hist + 0.25*band_color + 0.15*appearance`
  （`weights` 可覆盖）。`iou_core` 只取中央 50% 宽以避开工作室 T-pose 的手臂；
  `band_color` 是剪影竖直三等分的主色对比；`appearance` 是包围盒对齐后的前景色彩块均值。
  verdict：≥0.62 `close` / ≥0.48 `partial` / 否则 `far`（仅对默认权重有意义）。
- **重要**：IoU 会被姿势差异压低（参考图手臂下垂 vs 工作室 T-pose），
  所以这个分数**只用于横向比较候选，不是绝对"像不像"**。判读一定结合对照图/接触印相。

## 透明性

- **游戏内 HUD**：画面左上角实时滚动每一步（F10 开关，配置里可关）
- **活动日志**：`UserData/AICharBridge/activity.log` + `kks_activity`（超过 512KB 轮转 `.old`）
- **逐项报告**：每次生成返回 `applied`/`skipped` 明细，改了什么一目了然
- **构建溯源**：`/doctor` 带程序集 path/size/编译时间/sha256，确认跑的是当前构建
- 只监听 `127.0.0.1`，外部机器无法访问

## 已知边界（非 bug）

- 服装 `id=0` 是「无」（不穿），所以指定 0 时人物只有内衣
- **连裤袜 `panst` 在部分机器上不渲染**：同一套写入流程下 `socks` 正常而 `panst` 换遍 id
  画面不变；用游戏自带视口截图对照结果相同（不是离屏渲染的锅），资源文件也确实存在
  → 指向身体材质/着色器层面（本机装有 `KKS_OverlayMods`/`KKSUS` 这类会改身体材质的插件）。
  **要腿部覆盖请用 `socks`。**
- **饰品位置**：能写 type/id/颜色并调用游戏默认定位，但部分饰品（如尾巴类）的默认位置/朝向
  需要手工偏移（`addMove`）才能在画面上看到——那通常是在制作模式里拖出来的，目前不支持。
  同部位放多个饰品会定位错乱。
- 饰品资源取决于已安装的 Mod。参考图独有的部件（鲸鱼尾、鱼鳍耳等）本机没有对应资源。
- 工作室里新角色都加在原点，多角色会重叠；验证单个角色请用干净场景（`kks_audit` 会告诉你）。
- 高版本 MCP 客户端若只显示部分工具，是连接建立早于文件更新——重连即可。

## 设计借鉴

参考 <https://github.com/sixtysevenlf/dsh-blender-plugin>（Blender 直连 MCP 通道）：

| 借鉴的点 | 本项目落地 |
|---|---|
| 任意机位 + 按包围盒自动取景 | `capture_views` 支持 `from`/`look_at`，默认双轴 fit（之前固定 3 档，反复裁掉头） |
| 离屏渲染，不碰用户视口 | 临时相机 + RenderTexture（之前强占相机、还会拍到 UI） |
| 多视角一次出图（contact sheet） | 一次调用拼一张 PNG，每格带 `coverage`/指纹（之前一张一调用，浪费模型轮次） |
| 帧哈希去重 / 稳定判断 | 拍前连续两次粗指纹一致才拍，避免拍到"衣服没加载完"的中间态 |
| 空帧自诊断 | `coverage` + `frame_looks_empty` + 修正建议（黑帧不再白烧一轮） |
| 唯一验收标准 + 构建溯源 | `kks_doctor` → `kind=ok`；provenance 带 sha256/编译时间 |
| `did_you_mean` | 非法 ID 给出相近条目或该分类 id 范围 |
| 描述预算 + 渐进披露 | 工具描述精简，参数全表/流程/验收放进 `kks_help` |
| 证据分级与读图纪律 | `kks_help verify`（见"证据纪律"一节） |

**还没做的**：

1. **更鲁棒的比对**：自适应掩码、质心/尺度对齐、多视角一致性；发色色相比对、衣服区域分割
2. **任务层（job）**：长任务返回 jobId，"客户端超时 ≠ 任务失败"
3. **写租约（lease）**：多会话并发写保护
4. **自动化回归**：把工具名/数量钉死的测试（现在 `kks_selftest` 是能力锁 + 手工验证脚本）
5. **通道与 skill 分离**：把"按参考图生成人物"的流程与验收写成独立 skill
6. **继续借道插件**：MoreAccessories（饰品槽位，接口在 UI 实例上，需要再挖一层）、
   OverlayMods（叠图/纹身，控制器已能探测到）、MoreOutfits（套装槽位）；
   以及给借道数据加"没装插件时的降级提示"（现在只在返回里说明，不阻止生成）

## 目录结构

```
KKS_AICharMCP/
├─ BridgePlugin/AICharBridge.cs    游戏内插件源码（25 端点）
├─ kks_chara_mcp.py                MCP 服务器（25 工具，仅标准库）
├─ build_bridge.bat                编译脚本（KKS_HOME 可配）
├─ tools/                          验证脚本与探针
└─ README.md
```
