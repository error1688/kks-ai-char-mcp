# KKS_AICharMCP - 恋活阳光 AI 人物生成（MCP）

让 AI（如 ZCode/Claude 等 MCP 客户端）通过工具调用，在 Koikatsu Sunshine /
CharaStudio 里**按描述或动漫图片生成人物卡**，并截图自查效果，全程公开透明。

```
AI(视觉+推理) ──MCP(stdio)──> kks_chara_mcp.py ──HTTP──> 游戏内 KKS_AICharBridge 插件 ──> 生成人物卡 / 加载工作室 / 截图
                                                    │
                                                    └──> 游戏画面左上角 HUD + activity.log（用户实时监督）
```

## 组成

| 组件 | 位置 | 说明 |
|---|---|---|
| 游戏内插件 | `BepInEx\plugins\KKS_AICharBridge.dll` | 本地 HTTP 桥（仅 127.0.0.1:24380），卡片生成/加载/截图，全部游戏操作在主线程执行 |
| MCP 服务器 | `KKS_AICharMCP\kks_chara_mcp.py` | 零依赖 Python 单文件，stdio JSON-RPC，已注册到 ZCode 工作区配置 |
| 源码 | `KKS_AICharMCP\BridgePlugin\AICharBridge.cs` | 编译命令见下 |

## MCP 工具（AI 可调用）

| 工具 | 作用 |
|---|---|
| `kks_status` | 游戏状态（进程/场景/FPS/是否工作室） |
| `kks_list_characters` | 列出 UserData 已保存人物卡 |
| `kks_list_presets` | 列出官方预设卡（44 张，按人格命名：お嬢様=大小姐、ギャル=辣妹、ボクっ娘=假小子…），推荐作基底 |
| `kks_list_options` | 列出当前游戏全部可用 ID 与名称（含 Mod）：发型、服装、五官（眼/眉/睫毛/眼线/唇/鼻/痣）、饰品（发/头/脸/颈/身/腰/腿/臂/手）。可用 `category` 只取一类 |
| `kks_generate_character` | 生成人物：基底卡 + 覆盖参数 → 保存 PNG 卡（`load:true` 直接加入工作室场景） |
| `kks_capture_views` | **推荐**：一次调用把多视角渲染拼成一张图返回。按角色包围盒自动取景（不会再裁掉头/脚），**离屏渲染**（不占用户视口、不带游戏 UI），拍前等画面稳定（服装是异步加载的）。可给 `from`/`look_at` 指定任意机位；近空帧在 `warnings` 里给修正建议 |
| `kks_doctor` | **唯一验收标准**：`kind=ok` 才代表通道可用。返回逐项检查 + 构建溯源（程序集路径/编译时间/sha256）+ 生效配置 |
| `kks_help` | 查长尾文档：workflow 工作流 / params 参数 / acceptance 验收 / pitfalls 常见坑 / reference 按参考图校准 |
| `kks_reference` | 载入参考图并自动切成多格（三视图→3 格），返回每格剪影形状(ASCII)、包围盒、aspect、分带主色；附带 `ink_profile` 与 `bg_color` 便于诊断切分 |
| `kks_compare` | 数值对标：我的某视角 vs 参考图某格 → `iou_core`/`iou_full`/`color_hist`/`band_color` + 总分 + **并排对照图**；权重可传参覆盖 |
| `kks_fit` | **服务端搜索**：逐个试候选参数，每个都渲染打分，返回候选榜并把最优应用回角色。整轮零模型轮次。`mode=hair`（默认，运行时热换发型，快）/ `mode=reload`（写临时卡+替换角色，可搜任意参数路径）。`save_shots=true` 会留下每个候选的 PNG + 一张接触印相 `board_png`（左→右 = 高分→低） |
| `kks_params` | **参数清单**：`shapes` 列出游戏自带的滑条名表（面部 52 / 体型 44）并核对本插件表是否一致；`fields` 把一张卡的 face/body/hair/clothes 所有公开成员连同当前值摊平列出 |
| `kks_clear` | 清空工作室场景里的角色（防"同名角色叠一摞"，可传 `name` 只清某一类） |
| `kks_audit` | **场景数字体检**（看图前先跑）：角色数/重名/两两距离/服装状态/主相机/物理；`kind=fail` 时按 `problems[].fix` 处理 |
| `kks_txn` | 快照/回滚：把当前角色完整存成卡，改坏了一键回滚（`snapshot`/`list`/`restore`/`drop`） |
| `kks_launch` | 一条调用把游戏拉起来并等到桥就绪（已在跑就直接返回） |
| `kks_live` | 读**场景里正在跑的角色**的实时状态：`clothesState`、各服装部位 id/颜色、运行时各槽是否 active（区分"没写进去"与"写进去但没生效"） |
| `kks_selftest` | 能力锁 + 自测：PROBE 校验 22 个端点是否在册、核心实现是否齐全、端到端渲染测量一次 |
| `kks_screenshot` | 单张截图（走用户的相机，兼容旧用法；验证造型请用 `kks_capture_views`） |
| `kks_focus_camera` | 手动把相机移到人物正前方，保持 `hold` 秒（默认 3）后交还控制权 |
| `kks_inspect_character` | 读出一张卡的真实内容（姓名/人格/五官/发型/每套服装 id 与颜色/服装状态），用于核对生成结果、排查"改了没效果" |
| `kks_activity` | 最近 50 条操作记录 |

## 生成参数（kks_generate_character）

- `sex`：female/male；`base`：基底卡名；`save_as`：卡片名；`load`：true 时加入工作室；`overwrite`：覆盖同名卡
- `parameter`：`fullname`（含空格自动拆成姓+名）、`nickname`、`personality`(0~43)、生日、血型等
- `face`：`pupil`{id, baseColor, subColor}（双眼同色）、眉色/眼线/唇色/痣、`headId`、`shapeValueFace` 滑条数组
- `body`：`skinMainColor`、`bustSoftness`、`bustWeight`、`shapeValueBody` 滑条数组（身高/胖瘦/胸围）
- `hair`：`back`/`front`/`side`/`option` → {id, baseColor, startColor, endColor, length}
- `clothes`：`top`/`bot`/`bra`/`shorts`/`gloves`/`panst`/`socks`/`shoes` → {id, color0~2, state}
  - `id`：从 `kks_list_options` 查；**0 = 无（不穿）**
  - `state`：可选，0=穿着（默认）1=半脱 2/3=脱下；也可用 `"wear": false`
- `accessories`：数组，元素 {type, id, colors, slot?, parent?}
  - `type`：`hair`/`head`/`face`/`neck`/`body`/`waist`/`leg`/`arm`/`hand`（也接受 1~10 数字）
  - `slot`：省略则自动找空槽；`parent`：父骨骼，省略时用游戏默认值
  - `clear_accessories: true`：先清空基座卡原有的饰品槽（官方卡常把 20 个槽占满）
- 颜色一律 `#RRGGBB`（也支持 `#RGB`/8 位）；ID 用 `kks_list_options` 查询；返回里 `applied/skipped` 明确列出每一项是否生效

KKS 卡片结构说明：face/body/hair 在 `ChaFile.custom`，服装在 `coordinate[status.coordinateType]`，
与 KK 原版不同，插件已适配。

## AI 图片/描述生成人物的标准流程（防盲干）

1. **看**：AI 用视觉能力读用户给的动漫图，提取发色/瞳色/肤色/发型特征
2. **查**：`kks_list_presets` 选人格相近的官方卡当基底；`kks_list_options` 找接近的发型 ID
3. **生成**：`kks_generate_character`（参数对齐图片）
4. **验**：`kks_screenshot(focus=true)` 看真实画面，与图对比
5. **调**：不像 → 改参数重新生成（`overwrite:true`），循环 2~4 步直到接近
6. 工具描述里已写死该闭环——AI 不得跳过截图直接宣称完成

## 公开透明

- **游戏内 HUD**：画面左上角实时滚动 AI 的每一步（F10 开关，配置里可关）
- **活动日志**：`UserData/AICharBridge/activity.log` + `kks_activity` 工具
- **逐项报告**：每次生成返回 applied/skipped 明细，改了什么一目了然
- 只监听 127.0.0.1，AI 无法从外部机器访问

## 注册状态

已写入 `E:\game\.zcode\config.json`（工作区作用域，ZCode 打开 E:\game 时自动连接）：

```json
{"mcp":{"servers":{"kks-chara":{"command":"…python.exe","args":["E:\\game\\KKS\\KKS_AICharMCP\\kks_chara_mcp.py"]}}}}
```

**使用前提**：先启动 KoikatsuSunshine 或 CharaStudio（插件随 BepInEx 自动加载），
游戏没开时工具会返回明确的"无法连接游戏"提示。端口在插件配置（F1 → AI人物生成桥）可改，
改后设环境变量 `KKS_BRIDGE_PORT` 同步给 MCP 服务器。

## 编译

```
KKS_AICharMCP\build_bridge.bat
```

脚本会先 `taskkill` 掉 CharaStudio/KoikatsuSunshine（DLL 被占用时 csc 会以
`CS0016 ... 无法在使用用户映射区域打开的文件上执行` 静默失败），再调用
`C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe`：

```
csc -target:library -optimize+ -codepage:65001 -nowarn:1701,1702,1762
    -out:BepInEx\plugins\KKS_AICharBridge.dll
    -r:BepInEx\core\BepInEx.dll
    -r:CharaStudio_Data\Managed\{UnityEngine, UnityEngine.CoreModule, UnityEngine.ScreenCaptureModule,
       UnityEngine.InputLegacyModule, UnityEngine.IMGUIModule, UnityEngine.TextRenderingModule,
       UnityEngine.ImageConversionModule, UnityEngine.ParticleSystemModule, Assembly-CSharp}.dll
    KKS_AICharMCP\BridgePlugin\AICharBridge.cs
```

编译后务必确认 DLL 的**时间戳/大小变了**——被占用时 csc 的失败很容易被漏看，
曾经因此让源码和 DLL 不一致（`/doctor` 的 provenance 就是为此加的）。

注意：从 Git Bash 调 csc 时不要用 `KKS_AICharMCP/BridgePlugin/AICharBridge.cs` 这种正斜杠相对路径
（路径会被吞掉，报"找不到源文件"），用 `build_bridge.bat` 或 Windows 反斜杠绝对路径。

## 已验证（含本轮修复）

- 主游戏：状态/列表/选项/生成/回读验证/截图 全通过
- 工作室：生成 `load:true` → 自动等加载 → 相机对准正脸 → 截图（人物清晰可见）全通过
- 生成的卡经游戏自身 `LoadCharaFile` 回读验证（`verified:true`）
- 服装：红色水手服 / 深蓝外套 在画面上确认已穿上；发型换款与染色确认生效
- 非法输入（不存在的发型 ID、负数服装 ID、越界人格/生日、非法颜色、超长滑条数组）全部被拦下并在
  `skipped` 里给出可读原因，不再静默产出残缺角色

## 已修复的坑（都是实测踩出来的）

| 问题 | 原因 | 修法 |
|---|---|---|
| 生成的人物不穿衣服/只穿内衣 | KKS 官方预设卡把服装状态存成「已脱」（`clothesState=3`），服装数据对但不穿着；且改卡片里的状态会被游戏存档流程重置 | 角色加载后调用游戏自身的运行时接口 `ChaControl.SetClothesStateAll(0)` 真正穿上 |
| `face` 里同时给 pupil 和其它五官参数时，其它参数被静默丢弃 | 代码写成 if/else 二选一 | pupil 作用于双眼，其余键照常应用到 face 本体 |
| 截图只拍到后脑勺 | 相机放在 `head - forward` 侧 | 改到 `head + forward` 侧（正面），补光方向同步反转 |
| 换个不存在的发型 ID → 生成出残缺角色 | 没有校验 | 用 `ChaListControl.ContainsInfo` 校验（发型 101~104、服装 105~112），不合法直接跳过并说明 |
| 部分滑条数组会破坏原有数据 | `float[]` 字段整体替换 | 改为逐元素写入现有数组，过长才报错 |
| 人格/生日越界 | 未钳制 | 钳到合法范围并在 `skipped` 里注明 |
| 空 body 调 `/screenshot` 抛空引用 | 参数读取未判空 | 全部判空 |
| 非 ASCII 卡片名查询失败 | query 未做 URL 解码 | `Uri.UnescapeDataString` |
| 主线程卡住时请求永久挂起 | 无超时 | 主线程等待 45 秒超时（生成 120 秒、截图 30 秒） |
| HUD 每帧分配内存 | 每帧 new GUIStyle + GetRange | 缓存样式、只在活动变化时重建文本 |
| activity.log 无限增长 | 无上限 | 超过 512KB 轮转 `.old` |
| 极端优化插件的「帧率上限 = -1（不修改）」实为设为 0（无上限） | 三元表达式写错 | 启动时记录原始值，-1 时还原原始设置 |
| 纹理串流被无条件打开 | 与说明不符 | 改为配置项「启用纹理串流」，默认关 |

## 调试接口

- `GET /probe?file=卡片.png&top=1&state=0`：改一个值→保存→回读，返回改前/内存中/回读后的值，
  用来确认某个字段到底能不能落盘（会生成 `probe_*.png`）

## 从三视图生成人物（实战流程）

1. 看图提取：发色（渐变要分 base/start/end）、瞳色、服装主辅色、鞋袜、饰品
2. `kks_list_options` 按分类查候选（发型/服装/五官/饰品），挑最接近的 id
3. `kks_generate_character`（`clear_accessories:true` 腾出饰品槽）→ `load:true` 进场景
4. `kks_screenshot` 依次拍 `framing=full` 的 front/back/side 三视图（+ `head` 特写）比对
5. 不像就改参数重来（`overwrite:true`），直到接近

**示例**（鲸鱼女仆，参考三视图）：渐变发用 `startColor=#16265C`（根）→ `endColor=#86D9F2`（梢），
女仆装用 `top=218 古典女仆A` + `bot=208 古典女仆裙A`（`color1` 是围裙色，要设白色），
白裤袜 `panst=1`，呆毛 `hair.option=8 長触覚`。

## 数值闭环（把"像不像"变成数字）

```
参考图 ──/reference──> 切格 + 剪影/颜色指标
                         │
角色 ──/capture──> 三视图拼图 ──/compare──> 分数 + 并排对照图 ──> /fit 搜索候选 ──> 回到最优
```

- **切格**：先按空列粗切；切不开时（三视图常被飘带/尾巴连住）改在"墨量最低的列"下刀，每刀在自己 1/N 邻域找谷底。
  宽图默认按三视图试 3 格；可用 `panels:3` 或 `ranges:"0-420,430-840"` 手工指定。
- **剪影**：离屏渲染用固定底色做精确比对；参考图用**从四边洪泛**——被深色线稿围住的白色区域（白围裙/白衣）
  不会被误判成背景挖掉，这是纯色阈值做不到的。
- **打分**：`total = 0.35*iou_core + 0.10*iou_full + 0.15*color_hist + 0.25*band_color + 0.15*appearance`
  （权重可通过 `weights` 覆盖）。
  `iou_core` 只取中央 50% 宽，用来避开工作室 T-pose 的手臂；`band_color` 是剪影竖直三等分（头/躯干/腿）的主色对比；
  `appearance` 是包围盒对齐后 64×64 的**前景-only 彩色**块均值 L1（只比较双方至少一边是前景的格子，
  否则大片背景差异会把候选之间的分差压平）。
  verdict：≥0.62 close / ≥0.48 partial / 否则 far（只对默认权重有意义）。
- **搜索**：`mode=hair` 用运行时接口 `ChangeHair` + `ChangeSettingHairColor` 热换（不必重载卡片），
  每个候选等画面稳定后渲染打分；`mode=reload` 走"写临时卡 → 替换场景角色 → 渲染 → 打分"，
  什么参数都能搜（眼型/唇线/花纹/体型…），代价是每轮 2~4 秒。
  结束时**显式把最优参数应用回角色**（否则会停在最后一轮，而不是最好那轮）。
- **重要**：IoU 会被姿势差异压低（参考图手臂下垂 vs 工作室 T-pose），所以这个分数**只用于横向比较候选**，
  不要当成绝对"像不像"。判读时一定要结合返回的对照图/接触印相。

### 可复现性是搜索的前提（本轮最大的发现）

一开始"搜眼型"完全搜不动：同一批候选的分数几乎一样（0.4742 全部相同）。加了外观指标后依然只有 1e-4 的差。
把两次渲染的图逐像素对比才看清原因 —— **噪声比信号大 100 倍**：

| 对比 | meanL1 | 强差异像素占比 |
|---|---|---|
| 只差眼型的两个候选（冻结物理前） | 13.3 | 14.2% |
| 同一参数两次渲染（冻结物理前） | ~13 | ~14% |
| 同一参数两次渲染（**冻结物理后**） | **0.04** | 0.06% |
| 只差眼型的两个候选（冻结物理后） | 0.10 | 0.17% |

原因：角色每轮都是删掉重建的，**头发的 DynamicBone 从头开始摆动、眼睛还在眨**，
所以"除了被搜的那个参数，画面还有 14% 的像素在变"。差异图上一眼就能看到
（发丝、围裙褶子全是红的，而眼型贡献只有 0.17%）。

→ 修法：测量渲染前停掉角色的所有 `DynamicBone`（`enabled=false`）并强制睁眼
（反射调用 `ChangeEyesBlinkFlag(false)` / `ChangeEyesOpen(true)`），渲染完再还原。
返回里的 `frozen:true` 就是这个状态。修完之后：

- 同参数重复渲染的噪声底降到 meanL1 0.04；
- 发型搜索的分数差从"看不清"变成 **0.536~0.562 的明显梯队**（约噪声底的 100 倍）；
- 眼型这种极细的差异仍然只有 1.8e-4 的信号（约噪声底 2~4 倍），**够排序但不够定胜负**，
  所以眼型/妆容这类判断必须靠 `save_shots` 的接触印相看图决定，不能只看数字。

这也是为什么 `kks_fit` 现在默认建议带 `save_shots:true`：让 AI 有图可依，而不是盲信一个 0.562 对 0.536 的数字。

## 成品自检（局部特写，别只看整体）

整体截图看不出穿模、皮肤质感、妆容问题，必须拍局部：

| 要看什么 | 怎么拍 |
|---|---|
| 脸/妆容 | `kks_capture_views {framing:"face", panel_width:800}` |
| 眼型/瞳色/高光 | `framing:"eyes"`（相机锚在头部骨骼，约 0.13m 视野，眼睛占大半画面） |
| 胸口/袖口穿模 | `framing:"bust"` |
| 腰腹/裙腰 | `framing:"torso"` |
| 任意部位 | `from`/`look_at` 给世界坐标，配合 `panel_width` 放大 |

**三类问题的根因（实测）**

- **肢体穿过衣服**：乳晕/乳头形状滑条（`AreolaBulge`/`NipWeight`/`NipStand`）会顶穿薄衣服，
  即使中等值也会；体型极端值（BustSize/Hip/WaistLowW/ThighUpW）同理。
  → `body.normalize`（默认开）把它们拉回安全带，乳晕/乳头压到 0~0.12。
- **皮肤不平整**：`detailPower`（身体/面部细节凹凸贴图）> 0.05、`drawAddLine`（肌肉线）、
  `paintId`（涂装）、`sunburnId`（晒痕）。→ `smooth_skin:true` 一键关闭并收敛光泽。
- **面部难看**：多数是"多改了不该改的"——`eyelineColor` 设太深会让眼线变成黑带盖住眼珠；
  覆盖 `pupil.id`/`hlUpId`/`eyebrowId` 会让高光和眼型变怪；`lipLineId=0` 时嘴没有轮廓。
  → 基座卡是官方成品，**只改参考图明确要求的（瞳色/发色/服装色）**，其余继承。
  用 `kks_inspect_character` 读回 `face_notes` / `body_shape_warnings` 定位。

## 第六轮：系统性修"协程异常 = 调用方干等超时"这一类 bug

**现象**：之前反复出现的"一直报错/卡住了"，有一类根因在结构上：桥接插件的每个异步接口
（/screenshot /capture /compare /fit /focus）都是"HTTP 线程把协程排进主线程队列，然后
`Task.Wait(30~300秒)`"。C# 迭代器协程里 **yield 不能放进 try/catch**，于是协程前半段
（取景、加载参考图、稳定等待循环）一直是**裸奔**的 —— 那里一抛异常，`TaskCompletionSource`
永远不完成，调用方只能干等满超时，还拿到误导性的"超时"错误；同时现场（临时灯/离屏相机/
RT/被禁用的相机控制权/测试中的发型/临时卡）全部残留。

**修法（六处）**：

- 新增 `ShotSettledSafe` 包装器：逐帧推进内层协程、把异常抓进 `ShotBox.Error`，
  所有 `ShotSettled` 调用点（compare/fit/reload）都改走它 —— 这是"yield 不能进 try/catch"
  的标准解法。
- `CaptureCoroutine`：取景/补光段整体 try/catch；稳定等待循环把每轮不含 yield 的探测体
  单独包起来，失败时释放 RT/相机/补光再落异常。
- `ShotCoroutine`：焦点设置段 try/catch，失败时 `RestoreCamera`（否则用户的相机控制权被
  留在禁用状态）；`FocusCoroutine` 同样处理，hold 用 try/finally 保证交还控制权。
- `FitCoroutine`（发型搜索）：参考图加载立刻报错（原来坏参考名 = 干等 300 秒）；整个搜索
  循环用 try/finally 兜底 —— **异常散出时把角色还原回原发型**，并把异常落回 tcs。
- `FitReloadCoroutine`（mode=reload）：同样处理，异常时清理 `__fit_tmp_*` 临时卡。
- `CompareCoroutine`：打分/写拼图段（含文件 IO）包进 try/catch，写盘失败也是干净的 500。

**验证**：22 路由 selftest ok；正常路径全量回归（capture coverage 0.238 / compare total
0.650 verdict=close / fit 两候选 restored_best=true）；坏参考名 0.1 秒内返回带指引的错误
（原来是 60~300 秒超时）。

**顺带查明**：MCP 服务器文件定义 22 个工具、stdio 握手实测 tools/list 返回 22 个；
若会话里只见 9 个旧工具，是连接建立早于文件更新 —— **重开会话/重连 MCP 即可**，不是代码 bug。

## 第五轮：按三视图重做时抓到的两个真 bug（都属"数据对了但外观不对"）

- **饰品分类号整体错位一位**：`OptionCats` 的饰品段写成 `121..129`，漏了开头的 `120`
  （`ChaAccessoryDefine.AccessoryCategoryTypeNone = 120`）。于是 `/options?category=ao_head`
  实际读的是 `face` 分类、`ao_body` 读的是 `waist`…… **AI 拿到的 id 属于别的分类，生成时又被校验拒掉**。
  我因此还误判过"本机没有女仆头饰/鱼鳍耳"（其实 `ao_head` 169 项里就有 `38/39/209/210 女仆头带/帽`、
  `313 サイバーフィン`、呆毛、狐耳猫耳兔耳等）。→ 改成 `120..129`，与 `AccessoryTypeNames` 的
  `120+type` 校验同源；脚本核对 34 个名字 ↔ 34 个分类号一一对应。
- **`shoes` 只写了 `shoes_inner`**：鞋子是两个部件（`parts[7]=shoes_inner`、`parts[8]=shoes_outer`），
  **外观在 shoes_outer**。只写 parts[7] 时卡片数据变了但画面不变 —— 实测换 `1/5/8/21` 四个鞋 id，
  渲染出来是**同一双基座卡的灰白花边鞋**。→ 写 `shoes` 时同时写 parts[8]，四个 id 立刻渲出四双不同的鞋。

**附带核实**：一度以为"加腰部饰品把裙子染成浅蓝"，按纪律拿原图核对后发现**颜色根本没变**
（是缩略图缩放 + 尾巴错位造成的误读）。同一轮也确认了：**同部位放多个饰品会定位错乱**
（加第二个 waist 饰品后脚踝冒出蝴蝶结、多出一条未染色巨尾挡在身前），因为本接口不支持饰品位置微调。

## 第四轮：按原版人物卡取参数（并修掉"读不到参数"这个根因）

用户一句话点醒：「你参考原版人物卡不行吗，为啥要自己测」。照做之后结论很直接 —— **我一直在自己发明参数**。

### 根因：`/inspect` 读不出滑条值

`FaceShapes()`/体型那段是直接访问 `fc.shapeValueFace` / `bd.shapeValueBody`，**实测在这条路径上取到的是 null**
（而走反射的 `/params?what=fields` 读得到）→ `face_shape` / `body_shape` 一直是空的。
后果：我根本无法读原版卡的滑条，只能凭感觉推 —— 于是推错。

修法：新增 `ReadFloatArray(obj, name)`（属性/字段都试，反射优先），两个读法统一走它。
修好之后 `kks_inspect_character` 能正常返回 52 个面部滑条 + 44 个体型滑条。

### 读到 44 张官方卡之后，我的假设全反了

| 我之前的判断 | 44 张官方卡的实际数据 |
|---|---|
| 「基座卡把 `FaceLowW` 压到 0、`ChinTipW` 压到 0 是极端值/缺陷」 | `FaceLowW=0 / ChinW=0.286 / ChinTipW=0` 是**大多数**官方卡的取值（お嬢様/のじゃっ子/ギャル/セクシー/ボーイッシュ…），是游戏自家的风格 |
| 「把 `ChinTipW` 推到 0.45~0.5 让下巴变圆」 | `ChinTipW` 在**全部 44 张**里都是 0 —— 设计师从不碰这个滑条；推到 0.5 会把低模下巴推成**多面体**，正是用户看到的"两个凹槽/坑坑洼洼" |
| 「自己推一组"单调递增"的下颌值」 | 直接拿本来就圆的原版卡当基底即可：`天真爛漫 FaceLowW=0.5 / ChinW=0.325 / ChinTipW=0` |

终版：**base = 天真爛漫，完全不覆盖 `face.shape`**，只改参考图明确要求的项（发色渐变/女仆装配色/袜/鞋）。
同取景对比图里，官方脸的下颌是平滑圆曲线，我推的那版有清晰的多面体折线与下巴中缝的凹槽。

### 顺带两个坑

- **`RenderTexture.antiAliasing` 在本机无效**：给离屏 RT 设 AA=4，和 AA=1 渲出来的图**逐像素完全相同**
  （meanAbsDiff=0.000）—— 因为游戏质量设置里 AA 是关的。改成**渲染 2 倍再盒式降采样**（`ss` 参数，默认 2），
  不依赖 MSAA 支持，实测确实改变输出（4.1% 像素）。
- **`ok=true` 不代表图里有东西**：重启游戏后场景是空的，离屏渲染出来是**纯色空帧**（8653 字节、只有一个灰阶），
  而返回里 `ok=true`、`settled=true`。判据必须是 `panels[].coverage` 与 `warnings`。
  我差点把这个当"回归"去查代码。（纪律已写进 `kks_help verify` 第 7 条。）

### 本轮新增的能力（对照 dsh-blender-plugin）

| 能力 | 落地 | 对应参考里的 |
|---|---|---|
| **场景数字体检**（看图之前的 L0 门槛） | `GET /audit` + `kks_audit`：角色数/重名/两两距离/服装状态/主相机/物理，`kind=fail` 时给出 fix。**实测叠 3 个同名角色会报 overlap(0.000m)+duplicate_names** —— 正是把我骗过去的那种场景 | `blender_rt_plan(op="audit_*")` 的网格体检 + 证据分级 |
| **事务/回滚** | `POST /txn` + `kks_txn`：`snapshot`（把当前角色完整存成卡）/`list`/`restore`/`drop` | `blender_rt_txn` |
| **一条调用启动游戏** | `kks_launch`：没开就拉起来并等桥就绪（`KKS_EXE` 可覆盖路径） | `blender_viewport(op="doctor"/launch)` 的 one-call launch |
| **清场** | `POST /clear` + `kks_clear`；`load:true` 默认替换同名角色（`add:true` 才并排） | — |

端点 22 个，MCP 工具 22 个。

## 第三轮：一个把前面几轮判断全部污染掉的 bug —— 场景里叠了一摞同名角色

`/generate` 的 `load:true` 只是 `AddFemale`，**不删旧的**。我的每次试验都带 `load:true`，
同一张卡（同 fullname「汐见 澪」）被反复加载十几次，全部落在原点 —— 用户直接看出来"模型重叠了"。

后果比看起来严重：多个身体互相穿插，腰侧就会冒出"皮肤三角"、下巴附近会出现"凹槽"。
我在腰部上白查了一轮（先怀疑围裙覆盖缝、又收窄腰腹、又换女仆装，收窄腰腹完全没用是因为
**衣服也是跟着身体缩放的**，那个"皮肤三角"根本不是穿模）——在干净场景下用 6 倍放大复验，
同一套衣服**一个皮肤像素都没有**。

修法（两道保险）：
- `load:true` 默认**替换同名角色**（`DeleteStudioChars(fullname)` 再 `AddFemale`），要并排多个角色传 `add:true`；
- 新增 `POST /clear`（可带 `name` 过滤）一键清场；生成返回里带 `replaced_same_name` 与 `scene_chars`，
  一眼就能看出"场景里有几个角色"。

教训写进纪律：**任何"看渲染图判断"之前，先确认场景里只有目标那一个角色。**

### 下巴：凹槽是我自己改出来的，正确改法是"单调递增"

基座卡把下颌拉到了极端：`FaceLowW=0.000`、`ChinTipW=0.000`、`ChinW=0.286` —— 所以下巴尖。
我第一版只挑了几个滑条往中间推（`CheekW 0.46` 配 `ChinW 0.50`、`ChinTipW 0.45`），
脸颊与下巴之间取值不连续 → 轮廓上出现**两个凹槽**。

第二版改成"把下颌/脸颊整组统一设 0.5"，凹槽没了，但**眼睛被带上**了：`FaceUpY 0→0.5`
把眼睛和额头整体挪了位，看起来像眼睑变厚、眉毛变粗。用同一"眼部特写"取景做受控对比才确认
（之前看着"变厚"其实是裁剪比例不同造成的错觉，受控对比里两边眼睛完全一致）。

第三版（采用）：只动"下颌→下巴尖"这一串，并保证**沿脸颊→下颌→下巴尖单调递增**：
`FaceLowW 0.42 / ChinW 0.47 / ChinTipW 0.50`，加 `ChinLowY/ChinLowZ/ChinY/ChinZ/ChinTipY/ChinTipZ` 的中庸值，
不碰 `FaceUp*` 与 `Cheek*` → 下巴圆了、眼型不动。

（脸颊上那两条淡斜线在"改/不改"两张图里都在，是基座卡面部自带的眼睑—脸颊过渡，不是这次改出来的。）

### 顺带确认的事

- `top=37 女仆装` 虽然腰部干净，但它是**无袖**的，跟参考图的长袖不符 → 仍用 `古典女仆A(218/208)`。
- 所谓"腰部问题"在干净场景下不存在；参考图与成品的差异主要在**参考图独有件**（鱼鳍耳、鲸尾、
  带蕾丝的女仆头饰、围裙上的鲸鱼印花），这些本机没有对应资源。

## 本轮（第二轮）修的 bug —— 三条都是"参数到底有没有生效"级别的

- **`face.shape` 被无声丢弃**：`Applier.Apply` 会把 `shape` 键交给专门函数处理，但我只写了 `body` 的
  专门函数（`ApplyBodyShape`），没有 `face` 的 —— 于是 `face.shape` 既不进 applied 也不进 skipped，
  **完全无声**。后果：面部 52 个滑条一个都调不了，"下巴尖"这种问题在 API 层面无解
  （基座卡把 `FaceLowW`/`ChinTipW` 存成 0，就是最尖最窄）。→ 补 `ApplyFaceShape`（含大小写纠错提示），
  实测 `FaceLowW 0→0.45 / ChinW 0.286→0.5 / ChinTipW 0→0.45 / FaceBaseW 0.228→0.4` 后下颌明显变圆。
- **`ref_crop` 上下颠倒**（Unity 的 `GetPixels32()` 和 Blender 的 `image.pixels` 一样是**自下而上**的）：
  `CropRegion` 按"Y0 = 图顶"写，于是 `ref_crop:"head"` 裁出来是**鞋子和腿**。也就是说之前用
  `ref_crop:"head"` 做眼型搜索时，全程在拿"我的脸部特写"和"参考图的鞋子"比分数。
  同时受影响的还有剪影 ASCII（打出来是倒的）和分带主色（band 0 标的是"头"，实际是"腿"）。
  → 三处一起改成按 `im.Y1`（头顶）往下取；已用 `/compare` 的并排图逐张确认。
- **"恢复穿着"是异步的，紧跟其后的截图会拍到半裸**：`/generate` 里 `StartCoroutine(DressAfterLoad(...))`
  是发射即忘，HTTP 响应立刻返回。活动日志实锤：`17:14:42 截图` / `17:14:45 才 SetClothesStateAll`。
  后果就是"裤袜/饰品还没挂上"的中间态被当成结果。→ 把"补齐穿着"放进渲染路径（`EnsureDressed`），
  让稳定判定去等衣服真的加载完；`/capture` 可用 `dress:false` 关掉。
- **腿部穿模**：基座卡的腿型滑条很极端（`ThighLowW=0.078`、`Calf=0.180`、`KneeLowZ=0.087`），
  而袜子/裤袜是独立网格不跟随 → 大腿/膝盖正面从袜子里顶出斑点状皮肤。
  → 把腿部滑条（Thigh*/Knee*/Calf/Ankle*）纳入 `normalize`，并单独夹到 0.32~0.80（比身体其它部位更紧）。

### 附带查清：连裤袜(panst) 在本机不渲染（环境问题，非代码）

同一套写入流程下 `socks`（膝袜/吊带袜）能正常显示白色，`panst` 从 0/1/5/7 换遍了画面一模一样；
用**游戏自己的视口**截图（`/screenshot`）对照，结果相同 → 不是离屏渲染的锅；
`abdata/chara/co_panst_*.unity3d` 资源文件也确实在 → 指向身体材质/着色器层面（本机装有
`KKS_OverlayMods`/`KKSUS` 这类会改身体材质的插件）。**要腿部覆盖请用 `socks`。**

## 新增：参数清单与实时状态回读

| 端点 | 作用 |
|---|---|
| `GET /params?what=shapes` | 列出游戏自带的滑条名表（从 `ChaFileDefine.FaceShapeIdx/BodyShapeIdx` 枚举反射读出），并**逐项对本插件内置的表**，返回 `consistent` 与第一处不一致 |
| `GET /params?what=fields` | 把一张卡的 face/body/hair/clothes **所有公开成员连同当前值**摊平列出（回答"到底哪些参数能调"） |
| `GET /live` | 读**场景里正在跑的角色**（不是卡片文件）：`clothesState`、每个服装部位的 id/颜色、以及运行时 `objClothes` 各槽是否 active —— 用来区分"数据没写进去"和"写进去了但运行时没生效" |

核对结果：面 52/52、身体 44/44，**与游戏枚举逐项一致**，索引 0 基对齐（所以"按名字设滑条"是可靠的）。

## 本轮修的 bug（都是"看渲染图 / 逐像素对比"才发现的）

- **测量渲染不可复现**（最大的一条）：角色每轮重建后头发物理从头摆动、眼睛在眨，
  同一参数两次渲染差 meanL1 13、14% 像素；而"只差眼型"的信号只有 0.10。
  → 测量前停掉 `DynamicBone` 并强制睁眼，噪声底降到 0.04，搜索才有意义（详见上面"可复现性"一节）。
- **接触印相没生成**：`/fit` 里按 `iter` 取候选图时判了 `is double`，但 JSON 里 `iter` 是 int，
  于是 `cells` 恒为空、`board_png` 一直是 null、`board_order` 恒为 `[]`。
  → 改用 `Convert.ToInt32` 兜住两种数字类型。
- **`appearance` 指标对脸没用**：原来是包围盒对齐后 **48×48 灰度点采样**，眼睛在整幅里只占几个格子，
  两个视觉上明显不同的眼型只差 0.0009。→ 改成 64×64 **前景-only 彩色块均值**，并加 `framing:"eyes"` 眼部特写。
- **衣服颜色改了没变**：颜色其实写对了（`colorInfo[0]=#1E2A55` 深藏青），但 `colorInfo[0]` 上挂着
  **pattern=30 花纹叠加层**，那层浅蓝条纹是它盖出来的。→ 新增 `color0~3` / `pattern0~3` / `patternColor0~3`
  四个通道，`"pattern0":0` 清掉花纹即可得到纯色。
- **`clear_accessories:true` 单独传时无效**：清空逻辑写在"必须同时传 accessories 数组"的分支里，
  于是只清空不加饰品时直接 return，基座卡的残留饰品（红色发饰、项链）留在卡上。
  → 判断拆开后，实测"剩余饰品槽：已全部清空"。

## 设计借鉴（对照 dsh-blender-plugin）

参考 <https://github.com/sixtysevenlf/dsh-blender-plugin>（Blender 的直连 MCP 通道），本套桥接做了这些对齐：

| 借鉴的点 | 在本项目里的落地 | 解决的问题 |
|---|---|---|
| 任意机位 + 按包围盒自动取景 | `kks_capture_views` 支持 `from`/`look_at`；默认按角色渲染包围盒算距离（双轴 fit + aspect），并过滤异常大的包围盒 | 之前固定 3 档取景，反复出现"头被裁掉" |
| 离屏渲染，不碰用户视口 | 临时相机 + RenderTexture，同步 `Render()`；不需要禁用用户的相机控制 | 之前强占相机、还会拍到工作室 UI |
| 多视角一次出图（contact sheet） | 一次调用拼成一张 PNG，每格带 `coverage`/指纹；`identical_to_previous` 提示视角没变 | 之前一张一调用，模型要花好几轮 |
| 帧哈希去重/稳定判断 | 拍前连续两次粗指纹一致才拍（`settled`），避免拍到"衣服还没加载完"的中间态 | 之前拍到只穿内衣的中间态 |
| 空帧自诊断 | `coverage` + `frame_looks_empty` 警告 + 修正建议 | 黑帧/空帧会白烧一轮 |
| 唯一验收标准 + 构建溯源 | `kks_doctor` → `kind=ok`；provenance 带程序集 sha256/编译时间 | 之前无法确认"跑的是不是我刚编译的那份" |
| `did_you_mean` | 非法 ID 时给出相近条目，或该分类的 id 范围 | 之前只说"ID 不存在" |
| 描述预算 + 渐进披露 | 12 个工具共约 5 千字符，参数全表/流程/验收放在 `kks_help` | 之前单个工具描述就 3000+ 字符 |

**已落地**：数值对标（`/reference` + `/compare` + 并排对照图）、服务端搜索（`/fit`）、能力锁（`/selftest`）、
构建溯源（`/doctor`）、离屏渲染与画面稳定检测、`did_you_mean`。

**还没做的（下一步优先级）**：

1. **尺度/掩码更鲁棒的比对**：现在用剪影 IoU + 分带主色；他们还有自适应掩码、质心/尺度对齐、
   多视角一致性（`qc_render_views` 的 `ref_path → IoU per view`）。可以继续加"发色专门的色相比对"、
   "衣服区域分割"等更贴合换装游戏的指标。
2. **事务/回滚**（`txn`）：改之前快照参数与卡片，不满意一键回退。现在只能靠 `overwrite` 重生成。
3. **任务层**（`job`）：长任务返回 jobId，"客户端超时 != 任务失败"。现在超时就是报错，没有收集入口。
4. **写租约**（`lease`）：多会话并发写保护。
5. **能力锁与自测**：`npm test` 那种把工具名/数量钉死的测试，防止接口悄悄退化；本项目目前只有手工验证脚本。
6. **通道与 skill 分离**：他们明确"插件管通道、skill 管流程与验收标准"（配套 `dsh-skill-blender-modeling`，含三视图标定、双证据验收、6 类坑）。
   本项目可以把"按参考图生成人物"的流程与验收写成 skill，把 `kks_help` 的内容固化下来。

## 已知边界（非 bug）

- 服装 id `0` = 「无」（不穿），所以指定 id 0 时人物自然只显示内衣
- 工作室里每个新角色都加在原点，多角色会重叠；验证单个角色请用干净场景
- 工作室曾出现一次"未响应"，隔离测试确认是第三方插件（StudioAddonLite）初始化阶段的偶发挂起，
  与本套插件无关，重开即恢复
- **饰品位置**：接口能写入 type/id/颜色并调用游戏的默认定位，但部分饰品（如尾巴类）的默认位置/朝向
  需要手工偏移（`addMove`）才能在画面上看到——那部分参数正常是在制作模式里拖出来的，目前不支持
- 饰品资源取决于已安装的 Mod：本机只有狐耳/猫耳/龙尾等，没有鲸鱼尾、鱼鳍耳这类部件
