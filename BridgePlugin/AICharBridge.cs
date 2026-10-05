// KKS_AICharBridge - 恋活阳光 AI 人物生成桥
// 在游戏内开放本地 HTTP 接口，供外部 MCP 服务器/AI 调用：
//   GET  /status                游戏状态
//   GET  /characters            列出已保存的人物卡
//   GET  /presets               列出官方默认人格预设卡
//   GET  /options               列出可用的发型/服装 ID（运行时读游戏列表）
//   POST /generate              以某张卡为基底，按 JSON 参数生成新人物卡（可在工作室加载）
//   POST /screenshot            截取当前游戏画面（供 AI 查看效果）
// 所有游戏操作在主线程执行；仅监听 127.0.0.1。
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AICharBridge
{
    [BepInPlugin("aicharbridge.kks.local", "AI人物生成桥 AICharBridge", "1.0.0")]
    [BepInProcess("KoikatsuSunshine.exe")]
    [BepInProcess("CharaStudio.exe")]
    [BepInProcess("KoikatsuSunshine_VR.exe")]
    public class AICharBridge : BaseUnityPlugin
    {
        internal static AICharBridge Instance;
        private ConfigEntry<int> _port;
        private ConfigEntry<bool> _enabled;
        private ConfigEntry<bool> _hud;
        private ConfigEntry<KeyCode> _hudKey;
        private BridgeServer _server;
        private readonly ConcurrentQueue<Action> _mainThread = new ConcurrentQueue<Action>();
        private float _fpsSmooth = 60f;
        private readonly List<string> _activity = new List<string>();

        // 生成时请求的饰品，等角色进场景后再用游戏自身的接口套上去
        private class PendingAcs { public int Slot; public int Type; public int Id; public string Parent; }
        private readonly List<PendingAcs> _pendingAcs = new List<PendingAcs>();
        private readonly object _activityLock = new object();

        private void Awake()
        {
            Instance = this;
            _port = Config.Bind("网络", "端口", 24380, new ConfigDescription(
                "本地 HTTP 桥监听端口（仅 127.0.0.1，AI 通过 MCP 服务器访问）", new AcceptableValueRange<int>(1024, 65535)));
            _enabled = Config.Bind("网络", "启用", true, "关闭后不再开启本地服务");
            _hud = Config.Bind("显示", "活动面板", true, "在游戏画面左上角实时显示 AI 的每一步操作（公开透明）");
            _hudKey = Config.Bind("显示", "活动面板开关热键", KeyCode.F10, "切换活动面板显示");

            if (_enabled.Value)
            {
                _server = new BridgeServer(_port.Value, HandleRequest);
                if (!_server.Start())
                    Logger.LogError("HTTP 桥启动失败（端口可能被占用）: " + _port.Value);
                else
                {
                    Logger.LogInfo("AI 人物桥已启动: http://127.0.0.1:" + _port.Value + "/status");
                    AddActivity("AI 人物桥已启动，端口 " + _port.Value);
                }
            }
        }

        private void OnDestroy()
        {
            if (_server != null) _server.Stop();
        }

        private void Update()
        {
            if (_hudKey.Value != KeyCode.None && Input.GetKeyDown(_hudKey.Value)) _hud.Value = !_hud.Value;
            _fpsSmooth = Mathf.Lerp(_fpsSmooth, 1f / Mathf.Max(Time.unscaledDeltaTime, 0.0001f), 0.05f);
            Action a;
            int budget = 64;
            while (budget-- > 0 && _mainThread.TryDequeue(out a))
            {
                try { a(); }
                catch (Exception e) { Logger.LogError("主线程任务异常: " + e); }
            }
        }

        // ============ 活动记录（游戏画面 + 日志文件，公开透明） ============
        private string ActivityLogFile { get { return Path.Combine(GameRoot, "UserData/AICharBridge/activity.log"); } }
        private const long ActivityLogMaxBytes = 512 * 1024;

        internal void AddActivity(string text)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + "  " + text;
            lock (_activityLock)
            {
                _activity.Add(line);
                if (_activity.Count > 200) _activity.RemoveRange(0, _activity.Count - 200);
                _activityVersion++;
            }
            Logger.LogInfo("[AI桥] " + text);
            try
            {
                string dir = Path.GetDirectoryName(ActivityLogFile);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                // 日志超过上限时轮转一份 .old，避免无限增长
                FileInfo fi = new FileInfo(ActivityLogFile);
                if (fi.Exists && fi.Length > ActivityLogMaxBytes)
                {
                    string old = ActivityLogFile + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(ActivityLogFile, old);
                }
                File.AppendAllText(ActivityLogFile, line + Environment.NewLine, Encoding.UTF8);
            }
            catch (Exception) { }
        }

        private GUIStyle _hudTitleStyle;
        private GUIStyle _hudBodyStyle;
        private int _activityVersion;
        private int _hudCachedVersion = -1;
        private string[] _hudLines = new string[0];

        private void OnGUI()
        {
            if (!_hud.Value) return;
            // 仅在活动有变化时重建文本，避免每帧 GC（本插件与性能优化插件共存，自身不能拖后腿）
            if (_activityVersion != _hudCachedVersion)
            {
                lock (_activityLock)
                {
                    int start = Math.Max(0, _activity.Count - 10);
                    _hudLines = _activity.GetRange(start, _activity.Count - start).ToArray();
                }
                _hudCachedVersion = _activityVersion;
            }
            if (_hudTitleStyle == null)
            {
                _hudTitleStyle = new GUIStyle(GUI.skin.label);
                _hudTitleStyle.fontStyle = FontStyle.Bold;
                _hudBodyStyle = new GUIStyle(GUI.skin.label);
                _hudBodyStyle.fontSize = 12;
                _hudBodyStyle.wordWrap = false;
                _hudBodyStyle.clipping = TextClipping.Clip;
            }
            int n = _hudLines.Length;
            GUI.Box(new Rect(8, 8, 560, 34 + n * 18 + 12), "");
            GUI.Label(new Rect(18, 12, 520, 22), "AI 人物桥 - 实时活动（" + _hudKey.Value + " 开关）", _hudTitleStyle);
            for (int i = 0; i < n; i++)
                GUI.Label(new Rect(18, 32 + i * 18, 540, 18), _hudLines[i], _hudBodyStyle);
        }

        // 在主线程执行 func 并等待结果（HTTP 线程调用）。超时返回错误而不是永久挂住请求。
        internal object RunOnMain(Func<object> func)
        {
            return RunOnMain(func, 45000);
        }

        internal object RunOnMain(Func<object> func, int timeoutMs)
        {
            var tcs = new TaskCompletionSource<object>();
            _mainThread.Enqueue(delegate
            {
                try { tcs.SetResult(func()); }
                catch (Exception e) { tcs.SetException(e); }
            });
            if (!tcs.Task.Wait(timeoutMs))
                throw new Exception("游戏主线程 " + (timeoutMs / 1000) + " 秒内未响应（可能正在加载、最小化或卡顿），请稍后重试");
            return tcs.Task.Result;
        }

        private static string GameRoot { get { return Directory.GetParent(Application.dataPath).FullName; } }

        // ============ 请求路由 ============
        // 能力清单：selftest 用它做能力锁（与 MCP 侧工具数对应）
        private static readonly string[] RouteManifest = new string[] {
            "/status", "/characters", "/presets", "/options", "/options_ids", "/activity", "/inspect", "/params", "/live", "/clear", "/audit", "/txn", "/probe",
            "/doctor", "/capture", "/reference", "/compare", "/fit", "/selftest",
            "/focus", "/generate", "/screenshot" };

        private static bool RouteExists(string path)
        {
            for (int i = 0; i < RouteManifest.Length; i++) if (RouteManifest[i] == path) return true;
            return false;
        }

        private BridgeServer.Response HandleRequest(string method, string path, string query, string body)
        {
            // 只回报端点是否存在，不执行任何工作（自测/能力锁用，零副作用）
            if (method == "PROBE")
            {
                bool known = RouteExists(path);
                return Resp(known ? 200 : 404, Json.Obj("ok", known, "route", path));
            }
            try
            {
                // 状态查询与活动查询本身不记入活动日志，避免自污染
                if (path != "/selftest" && path != "/status" && path != "/activity" && path != "/doctor" && path != "/capture" && path != "/reference" && path != "/compare" && path != "/fit")
                    AddActivity("收到请求 " + method + " " + path);
                if (path == "/status" && method == "GET") return Resp(200, GetStatus());
                if (path == "/characters" && method == "GET") return Resp(200, ListCharacters());
                if (path == "/presets" && method == "GET") return Resp(200, ListPresets());
                if (path == "/options" && method == "GET") return Resp(200, GetOptions(query));
                if (path == "/activity" && method == "GET") return Resp(200, GetActivity());
                if (path == "/inspect" && method == "GET") return Resp(200, Inspect(query));
                if (path == "/params" && method == "GET") return Resp(200, ParamInventory(query));
                if (path == "/live" && method == "GET") return Resp(200, Live(query));
                if (path == "/clear" && (method == "GET" || method == "POST")) return Resp(200, ClearScene(query));
                if (path == "/audit" && method == "GET") return Resp(200, Audit());
                if (path == "/txn" && method == "POST") return Resp(200, Txn(Json.Parse(body) as Dictionary<string, object> ?? new Dictionary<string, object>()));
                if (path == "/probe" && method == "GET") return Resp(200, Probe(query));
                if (path == "/doctor" && method == "GET") return Resp(200, Doctor());
                if (path == "/capture" && method == "POST") return Resp(200, Capture(body));
                if (path == "/reference" && method == "POST") return Resp(200, Reference(body));
                if (path == "/compare" && method == "POST") return Resp(200, Compare(body));
                if (path == "/fit" && method == "POST") return Resp(200, Fit(body));
                if (path == "/options_ids" && method == "GET") return Resp(200, OptionIds(query));
                if (path == "/selftest" && method == "GET") return Resp(200, SelfTest());
                if (path == "/focus" && method == "POST") return Resp(200, FocusCamera(body));
                if (path == "/generate" && method == "POST") return Resp(200, Generate(body));
                if (path == "/screenshot" && method == "POST") return Resp(200, Screenshot(body));
                return Resp(404, Json.Obj("ok", false, "error", "unknown endpoint " + method + " " + path));
            }
            catch (Exception e)
            {
                Exception real = e;
                while (real is AggregateException && real.InnerException != null) real = real.InnerException;
                AddActivity("!! 接口异常 " + path + ": " + real.Message);
                return Resp(500, Json.Obj("ok", false, "error", real.Message, "type", real.GetType().Name));
            }
        }

        private object GetActivity()
        {
            lock (_activityLock)
            {
                int start = Math.Max(0, _activity.Count - 50);
                return Json.Obj("ok", true, "entries", _activity.GetRange(start, _activity.Count - start).ToArray());
            }
        }

        // ============ /inspect：读出一张人物卡的真实内容（用于排查"改了为什么没生效"） ============
        // ============ /params：参数清单（回答"到底有哪些参数可以调"） ============
        // 滑条名表不靠猜：游戏自己用枚举 ChaFileDefine.FaceShapeIdx / BodyShapeIdx 定义
        // "滑条名 = 数组下标"，直接读它的枚举就绝对对得上。
        // （第一版我去找"含 ChinLowY 的静态 string[]"，结果一无所获、还误报 consistent=true
        //   —— 因为那些名字是**枚举成员名**，不是字符串字面量。）
        private static List<object> _shapeTables;
        private static readonly object _shapeTablesLock = new object();

        private static List<object> DiscoverShapeTables()
        {
            lock (_shapeTablesLock)
            {
                if (_shapeTables != null) return _shapeTables;
                var found = new List<object>();
                Assembly asm = typeof(ChaFileFace).Assembly;
                Type cfd = asm.GetType("ChaFileDefine");
                if (cfd != null)
                {
                    Type[] nested;
                    try { nested = cfd.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic); }
                    catch (Exception) { nested = new Type[0]; }
                    for (int i = 0; i < nested.Length; i++)
                    {
                        Type n = nested[i];
                        if (n == null || !n.IsEnum) continue;
                        if (n.Name != "FaceShapeIdx" && n.Name != "BodyShapeIdx") continue;
                        string[] raw;
                        try { raw = Enum.GetNames(n); } catch (Exception) { continue; }
                        var pairs = new List<KeyValuePair<int, string>>();
                        for (int k = 0; k < raw.Length; k++)
                        {
                            if (raw[k] == "value__") continue;
                            int val;
                            try { val = System.Convert.ToInt32(Enum.Parse(n, raw[k])); } catch (Exception) { continue; }
                            pairs.Add(new KeyValuePair<int, string>(val, raw[k]));
                        }
                        pairs.Sort(delegate (KeyValuePair<int, string> a, KeyValuePair<int, string> b) { return a.Key.CompareTo(b.Key); });
                        var names = new List<object>();
                        var index = new Dictionary<string, object>();
                        for (int k = 0; k < pairs.Count; k++) { names.Add(pairs[k].Value); index[pairs[k].Value] = (double)pairs[k].Key; }
                        found.Add(Json.Obj("kind", n.Name == "FaceShapeIdx" ? "face" : "body",
                            "type", n.FullName, "count", names.Count, "names", names, "index", index));
                    }
                }
                _shapeTables = found;
                return found;
            }
        }

        // 把某个对象的所有公开成员摊平（值也读出来），用于清点"可调参数"。
        // 注意：游戏卡片结构体（ChaFileFace/Body/Hair）对外暴露的是**属性**，不是公开字段
        // —— 只扫 fields 会得到空清单（我第一版就踩了）。
        private static object FormatValue(object v, Type ft, int depth)
        {
            if (v == null) return null;
            if (ft == typeof(float)) return (double)(float)v;
            if (ft == typeof(int)) return (double)(int)v;
            if (ft == typeof(byte)) return (double)(byte)v;
            if (ft == typeof(bool)) return (bool)v;
            if (ft == typeof(string)) return (string)v;
            if (ft == typeof(Color)) return Hex((Color)v);
            if (ft.IsArray)
            {
                Array a = v as Array;
                if (a == null) return null;
                if (a.Length > 90) return "<array " + a.Length + ">";
                var items = new List<object>();
                for (int k = 0; k < a.Length; k++)
                {
                    object e = a.GetValue(k);
                    if (e is float) items.Add((double)(float)e);
                    else if (e is Color) items.Add(Hex((Color)e));
                    else if (e is int) items.Add((double)(int)e);
                    else items.Add(e == null ? null : e.ToString());
                }
                return items;
            }
            if (ft.IsValueType && !ft.IsEnum)
            {
                var sub = new Dictionary<string, object>();
                FlattenInto(sub, v, "", depth + 1);
                return sub.Count > 0 ? (object)sub : null;
            }
            return null;   // 不认识的类型不列，免得塞一堆 ToString 进去
        }

        private static void FlattenInto(Dictionary<string, object> into, object o, string prefix, int depth)
        {
            if (o == null || depth > 3 || into.Count > 900) return;
            Type t = o.GetType();
            PropertyInfo[] ps;
            try { ps = t.GetProperties(BindingFlags.Public | BindingFlags.Instance); } catch (Exception) { ps = new PropertyInfo[0]; }
            for (int i = 0; i < ps.Length; i++)
            {
                PropertyInfo p = ps[i];
                if (!p.CanRead || p.GetIndexParameters().Length > 0) continue;
                string key = prefix + p.Name;
                if (into.ContainsKey(key)) continue;
                object v;
                try { v = p.GetValue(o, null); } catch (Exception) { continue; }
                object fv = FormatValue(v, p.PropertyType, depth);
                if (fv != null) into[key] = fv;
            }
            FieldInfo[] fs;
            try { fs = t.GetFields(BindingFlags.Public | BindingFlags.Instance); } catch (Exception) { fs = new FieldInfo[0]; }
            for (int i = 0; i < fs.Length; i++)
            {
                FieldInfo f = fs[i];
                string key = prefix + f.Name;
                if (into.ContainsKey(key)) continue;
                object v;
                try { v = f.GetValue(o); } catch (Exception) { continue; }
                object fv = FormatValue(v, f.FieldType, depth);
                if (fv != null) into[key] = fv;
            }
        }

        private object ParamInventory(string query)
        {
            string what = QueryValue(query, "what");
            if (string.IsNullOrEmpty(what)) what = "shapes";
            var tables = DiscoverShapeTables();
            var res = new Dictionary<string, object>();
            res["ok"] = true;

            if (what == "shapes" || what == "all")
            {
                // 和源码里hardcode的表对拍，不一致就明确报出来
                var checks = new List<object>();
                foreach (object o in tables)
                {
                    var d = o as Dictionary<string, object>;
                    if (d == null) continue;
                    string kind = (string)d["kind"];
                    var names = d["names"] as List<object>;
                    string[] mine = kind == "face" ? FaceShapeNames : BodyShapeNames;
                    int diff = -1; string firstDiff = null;
                    if (names != null)
                    {
                        diff = 0;
                        int n = Mathf.Max(names.Count, mine.Length);
                        for (int i = 0; i < n; i++)
                        {
                            string a = i < names.Count ? (string)names[i] : "<缺>";
                            string b = i < mine.Length ? mine[i] : "<缺>";
                            if (a != b)
                            {
                                diff++;
                                if (firstDiff == null) firstDiff = "索引 " + i + "：游戏=" + a + " 本插件=" + b;
                            }
                        }
                    }
                    checks.Add(Json.Obj("kind", kind, "game_type", d["type"],
                        "game_count", d["count"], "plugin_count", mine.Length, "mismatches", diff,
                        "first_mismatch", firstDiff));
                }
                res["shape_tables"] = tables;
                res["consistency"] = checks;
                int bad = 0;
                foreach (object c in checks) if (System.Convert.ToInt32(((Dictionary<string, object>)c)["mismatches"]) != 0) bad++;
                // 一个表都没找到时必须报"无法核对"，不能因为没查到就报 consistent=true（假通过）
                res["consistent"] = checks.Count == 0 ? (object)null : (object)(bad == 0);
                res["note"] = checks.Count == 0
                    ? "★ 没找到游戏的滑条名表（ChaFileDefine.FaceShapeIdx/BodyShapeIdx 反射失败），本次无法核对"
                    : (bad == 0
                        ? "本插件内置的滑条名表与游戏枚举逐项一致，按名字设滑条不会错位"
                        : "★ 不一致！按名字设滑条会改错部位，需以 shape_tables 为准修正");
            }

            if (what == "fields" || what == "all")
            {
                string file = QueryValue(query, "file");
                if (string.IsNullOrEmpty(file)) file = "お嬢様.png";
                string sex = QueryValue(query, "sex");
                bool male = string.Equals(sex, "male", StringComparison.OrdinalIgnoreCase);
                byte sexB = male ? (byte)0 : (byte)1;
                string name = EnsurePng(file);
                string userDir = Path.Combine(GameRoot, male ? "UserData/chara/male" : "UserData/chara/female");
                string presetDir = Path.Combine(GameRoot, male ? "DefaultData/chara/male" : "DefaultData/chara/female");
                string path = Path.Combine(userDir, name);
                if (!File.Exists(path)) path = Path.Combine(presetDir, file);
                if (!File.Exists(path)) throw new Exception("找不到卡片: " + file);

                res["cards"] = RunOnMain(delegate
                {
                    ChaFileControl cf = new ChaFileControl();
                    if (!cf.LoadCharaFile(path, sexB, true, true)) throw new Exception("读取卡片失败: " + path);
                    var face = new Dictionary<string, object>();
                    var body = new Dictionary<string, object>();
                    var hair = new Dictionary<string, object>();
                    var cloth = new Dictionary<string, object>();
                    if (cf.custom != null)
                    {
                        FlattenInto(face, cf.custom.face, "", 0);
                        FlattenInto(body, cf.custom.body, "", 0);
                        FlattenInto(hair, cf.custom.hair, "", 0);
                    }
                    try
                    {
                        ChaFileCoordinate co = cf.coordinate[cf.status.coordinateType];
                        if (co != null) FlattenInto(cloth, co.clothes, "", 0);
                    }
                    catch (Exception) { }
                    return Json.Obj("file", Path.GetFileName(path),
                        "face_fields", face.Count, "body_fields", body.Count, "hair_fields", hair.Count,
                        "clothes_fields", cloth.Count,
                        "face", face, "body", body, "hair", hair);
                });
            }

            if (what == "shapes" || what == "all")
                res["hint"] = "滑条一律 0~1，0.5 附近为中性。face/body 的 shape 参数按这里给出的名字设（如 {\"shape\":{\"ChinW\":0.6}}）";
            return res;
        }

        // /live：读**场景里正在跑的那个角色**的实时状态（不是卡片文件）。
        // 用来区分"数据没写进去"和"写进去了但运行时没生效"——这两种情况的修法完全不同。
        private object Live(string query)
        {
            string name = QueryValue(query, "name");
            return RunOnMain(delegate
            {
                string found;
                ChaControl cc = FindStudioChar(name, out found);
                if (cc == null) return Json.Obj("ok", false, "error", "场景里没有角色（先在工作室里 load:true）");
                ChaFileControl cf = cc.chaFile;
                var states = new List<object>();
                if (cf != null && cf.status != null && cf.status.clothesState != null)
                    foreach (byte s in cf.status.clothesState) states.Add((int)s);
                var parts = new List<object>();
                try
                {
                    int ci = cf.status != null ? cf.status.coordinateType : 0;
                    ChaFileCoordinate co = cf.coordinate[ci];
                    if (co != null && co.clothes != null && co.clothes.parts != null)
                        for (int i = 0; i < co.clothes.parts.Length; i++)
                        {
                            ChaFileClothes.PartsInfo pi = co.clothes.parts[i];
                            parts.Add(Json.Obj("idx", i, "id", pi != null ? pi.id : -1,
                                "color0", pi != null ? Hex(pi.colorInfo[0].baseColor) : null));
                        }
                }
                catch (Exception) { }
                // 运行时各组件的可见性：确认"游戏认为它穿着没有"
                var vis = new List<object>();
                try
                {
                    if (cc.objClothes != null)
                        for (int i = 0; i < cc.objClothes.Length; i++)
                        {
                            GameObject g = cc.objClothes[i];
                            vis.Add(Json.Obj("slot", i, "exists", g != null,
                                "active", g != null && g.activeSelf));
                        }
                }
                catch (Exception) { }
                return Json.Obj("ok", true, "focus", found,
                    "fullname", cf != null && cf.parameter != null ? cf.parameter.fullname : null,
                    "coordinateType", cf != null && cf.status != null ? cf.status.coordinateType : -1,
                    "clothesState", states, "clothes_parts", parts, "objClothes", vis,
                    "note", "clothesState: 0=穿着 1=半脱 2/3=脱下；objClothes 是运行时服装物体是否 active");
            });
        }

        // 统计场景里的角色数（用来发现"重复加载导致模型重叠"）
        private static int StudioCharCount(List<ChaControl> outList)
        {
            int n = 0;
            try
            {
                Studio.Studio studio = Singleton<Studio.Studio>.Instance;
                if (studio == null || studio.dicObjectCtrl == null) return 0;
                foreach (Studio.ObjectCtrlInfo info in new List<Studio.ObjectCtrlInfo>(studio.dicObjectCtrl.Values))
                {
                    var oci = info as Studio.OCIChar;
                    if (oci == null) continue;
                    n++;
                    if (outList != null)
                    {
                        ChaControl c = GetChaControl(oci);
                        if (c != null) outList.Add(c);
                    }
                }
            }
            catch (Exception) { }
            return n;
        }

        // 立刻删掉场景里的角色（同步版，供 /clear 与"加载前先替换同名角色"用）。
        // 不这么做的话，每次 load:true 都会在原点再叠一个同名角色 —— 实测叠了十几个，
        // 渲染出来是"好几个模型穿插在一起"，腰上那些诡异的皮肤三角根本不是穿模，是别的副本。
        private static int DeleteStudioChars(string nameContains)
        {
            var victims = new List<Studio.OCIChar>();
            try
            {
                Studio.Studio studio = Singleton<Studio.Studio>.Instance;
                if (studio == null || studio.dicObjectCtrl == null) return 0;
                foreach (Studio.ObjectCtrlInfo info in new List<Studio.ObjectCtrlInfo>(studio.dicObjectCtrl.Values))
                {
                    var oci = info as Studio.OCIChar;
                    if (oci == null) continue;
                    if (!string.IsNullOrEmpty(nameContains))
                    {
                        ChaControl cc = GetChaControl(oci);
                        string cn = cc != null && cc.chaFile != null && cc.chaFile.parameter != null
                            ? (cc.chaFile.parameter.fullname ?? "") : "";
                        if (cn.IndexOf(nameContains, StringComparison.Ordinal) < 0) continue;
                    }
                    victims.Add(oci);
                }
                for (int i = 0; i < victims.Count; i++)
                {
                    try { Studio.Studio.DeleteInfo(victims[i].objectInfo, true); } catch (Exception) { }
                    try { if (victims[i].treeNodeObject != null) Studio.Studio.DeleteNode(victims[i].treeNodeObject); } catch (Exception) { }
                }
            }
            catch (Exception) { }
            return victims.Count;
        }

        private object ClearScene(string query)
        {
            string name = QueryValue(query, "name");
            return RunOnMain(delegate
            {
                var before = new List<ChaControl>();
                int n = StudioCharCount(before);
                int del = DeleteStudioChars(name);
                AddActivity("√ 清理场景：原有角色 " + n + " 个，删除 " + del + " 个");
                return Json.Obj("ok", true, "before", n, "deleted", del, "filter", name,
                    "note", "不传 name 就清空全部角色");
            });
        }

        // ============ /audit：场景数字体检（开会话/看渲染图之前的 L0 门槛） ============
        // 参考 dsh-blender-plugin 的"证据分级"：致命判断应来自脚本量出来的数字，而不是看图。
        // 这条最该先有——我自己就栽过：工作室里叠了十几个同名角色，渲染出来互相穿插，
        // 我把"另一个副本的身体"当成了穿模，白查了一轮。这个 /audit 一眼就能发现。
        private object Audit()
        {
            // 跑在主线程：下面要调 EnsureDressed（Unity API），并且要在"补齐穿着之后"再读状态 ——
            // 先补再读，结果才等于"渲染时真正会看到的样子"（否则刚加载的卡会被误报成"半裸"）。
            return RunOnMain(delegate { return AuditOnMain(); });
        }

        private object AuditOnMain()
        {
            var checks = new List<object>();
            var problems = new List<object>();
            var chars = new List<object>();

            Studio.Studio studio = null;
            try { studio = Singleton<Studio.Studio>.Instance; } catch (Exception) { }
            bool inStudio = studio != null;
            checks.Add(Json.Obj("name", "in_studio", "ok", inStudio, "detail", inStudio ? "当前在工作室" : "不在工作室"));
            Camera cam = Camera.main;
            checks.Add(Json.Obj("name", "main_camera", "ok", cam != null, "detail", cam != null ? "主相机可用" : "没有主相机，渲染会失败"));

            if (inStudio)
            {
                try
                {
                    var list = new List<ChaControl>();
                    int n = StudioCharCount(list);
                    checks.Add(Json.Obj("name", "scene_chars", "ok", n > 0, "detail", n + " 个角色"));

                    var names = new Dictionary<string, int>();
                    var positions = new List<Vector3>();
                    for (int i = 0; i < list.Count; i++)
                    {
                        ChaControl cc = list[i];
                        bool dressed = EnsureDressed(cc);   // 先补齐穿着，再读取（与渲染路径一致）
                        string fn = (cc.chaFile != null && cc.chaFile.parameter != null) ? (cc.chaFile.parameter.fullname ?? "") : "";
                        if (!names.ContainsKey(fn)) names[fn] = 0;
                        names[fn]++;
                        positions.Add(cc.transform.position);

                        int offWithId = 0;
                        var st = new List<object>();
                        try
                        {
                            ChaFileControl cf = cc.chaFile;
                            if (cf != null && cf.status != null && cf.status.clothesState != null)
                            {
                                ChaFileCoordinate co = cf.coordinate[cf.status.coordinateType];
                                for (int k = 0; k < cf.status.clothesState.Length; k++)
                                {
                                    st.Add((int)cf.status.clothesState[k]);
                                    if (cf.status.clothesState[k] != 0 && co != null && co.clothes != null
                                        && co.clothes.parts != null && k < co.clothes.parts.Length
                                        && co.clothes.parts[k] != null && co.clothes.parts[k].id != 0) offWithId++;
                                }
                            }
                        }
                        catch (Exception) { }
                        chars.Add(Json.Obj("index", i, "fullname", fn, "pos", positions[i].ToString(),
                            "clothes_state", st, "parts_off_but_has_id", offWithId, "dressed_by_audit", dressed));
                        if (offWithId > 0)
                            problems.Add(Json.Obj("name", "clothes_off",
                                "detail", "「" + fn + "」有 " + offWithId + " 个部位写了 id 但状态是『脱』（补齐穿着后仍是如此）",
                                "fix", "用 /live 看 objClothes 是否 active；数据对但不显示时多半是资源/着色器层面（本机的 panst 就是这种）"));
                    }

                    var dup = new List<object>();
                    foreach (KeyValuePair<string, int> kv in names)
                        if (kv.Value > 1) dup.Add(Json.Obj("fullname", kv.Key, "count", kv.Value));
                    checks.Add(Json.Obj("name", "unique_names", "ok", dup.Count == 0,
                        "detail", dup.Count == 0 ? "角色名唯一" : (dup.Count + " 个名字有重复")));
                    if (dup.Count > 0)
                        problems.Add(Json.Obj("name", "duplicate_names",
                            "detail", "有同名角色多份（多半是反复 load:true 叠出来的）",
                            "fix", "先 /clear 清空再只加载一个；或给不同角色不同 fullname"));

                    if (positions.Count >= 2)
                    {
                        float minDist = float.MaxValue;
                        for (int i = 0; i < positions.Count; i++)
                            for (int j = i + 1; j < positions.Count; j++)
                                minDist = Mathf.Min(minDist, Vector3.Distance(positions[i], positions[j]));
                        bool apart = minDist >= 0.25f;
                        checks.Add(Json.Obj("name", "chars_separated", "ok", apart,
                            "detail", "最近两个角色相距 " + minDist.ToString("0.000") + " m"));
                        if (!apart)
                            problems.Add(Json.Obj("name", "overlap",
                                "detail", "有角色几乎站在同一位置（" + minDist.ToString("0.00") + " m），渲染会互相穿插",
                                "fix", "清场后只留一个，或把角色挪开再渲染；脏场景里的图会骗人"));
                    }

                    int dyn = 0;
                    if (list.Count > 0 && list[0] != null)
                    {
                        try
                        {
                            DynamicBone[] bs = list[0].GetComponentsInChildren<DynamicBone>(true);
                            for (int i = 0; i < bs.Length; i++) if (bs[i] != null && bs[i].enabled) dyn++;
                        }
                        catch (Exception) { }
                    }
                    checks.Add(Json.Obj("name", "physics_frozen", "ok", true,
                        "detail", dyn == 0 ? "物理骨骼未启用" : (dyn + " 个 DynamicBone 在跑（测量渲染会自动冻结，仅提示）")));
                }
                catch (Exception e) { checks.Add(Json.Obj("name", "scene_scan", "ok", false, "detail", "扫描失败: " + e.Message)); }
            }

            bool ok = problems.Count == 0;
            return Json.Obj("kind", ok ? "ok" : "fail", "ok", ok,
                "checks", checks, "problems", problems, "chars", chars,
                "note", ok ? "场景干净，可以放心渲染/看图" : "先按 problems 的 fix 处理再看图 —— 脏场景里的图会骗人");
        }

        // ============ /txn：快照与回滚 ============
        // 参考 blender_rt_txn（snapshot/restore/list/drop）。快照的是"当前场景角色的完整卡片"，
        // 所以回滚 = 把那张快照卡重新加载回场景，连运行时状态一起还原。
        private static string TxnDir { get { return Path.Combine(GameRoot, "UserData/AICharBridge/txn"); } }

        private object Txn(Dictionary<string, object> req)
        {
            string op = Str(req, "op", "list");
            if (!Directory.Exists(TxnDir)) Directory.CreateDirectory(TxnDir);

            if (op == "list")
            {
                var items = new List<object>();
                string[] files;
                try { files = Directory.GetFiles(TxnDir, "*.png"); } catch (Exception) { files = new string[0]; }
                Array.Sort(files);
                for (int i = 0; i < files.Length; i++)
                {
                    var fi = new FileInfo(files[i]);
                    items.Add(Json.Obj("id", Path.GetFileNameWithoutExtension(files[i]),
                        "label", TxLabel(fi.Name), "bytes", fi.Length,
                        "time", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss"), "card", fi.FullName));
                }
                return Json.Obj("ok", true, "op", "list", "count", items.Count, "snapshots", items,
                    "note", "restore 会把快照卡重新加载进场景（替换同名角色）");
            }

            if (op == "snapshot")
            {
                string label = Str(req, "label", null);
                string name = Str(req, "name", null);
                return RunOnMain(delegate
                {
                    string found;
                    ChaControl cc = FindStudioChar(name, out found);
                    if (cc == null) return Json.Obj("ok", false, "error", "场景里没有角色可快照");
                    if (cc.chaFile == null) return Json.Obj("ok", false, "error", "拿不到角色卡片数据");
                    string id = DateTime.Now.ToString("yyyyMMdd_HHmmss") +
                        (string.IsNullOrEmpty(label) ? "" : "_" + SanitizeFileName(label));
                    string path = Path.Combine(TxnDir, id + ".png");
                    if (!cc.chaFile.SaveCharaFile(path, 1, true))
                        return Json.Obj("ok", false, "error", "保存快照失败: " + path);
                    AddActivity("√ 快照 " + id);
                    return Json.Obj("ok", true, "op", "snapshot", "id", id, "card", path,
                        "fullname", cc.chaFile.parameter != null ? cc.chaFile.parameter.fullname : null,
                        "bytes", new FileInfo(path).Length);
                });
            }

            if (op == "restore")
            {
                string id = Str(req, "id", null);
                if (string.IsNullOrEmpty(id)) throw new Exception("restore 需要 id（先 op=list）");
                string path = Path.Combine(TxnDir, SanitizeFileName(id) + ".png");
                if (!File.Exists(path)) throw new Exception("没有这个快照: " + id);
                string nm = Str(req, "name", null);
                return RunOnMain(delegate
                {
                    Studio.Studio studio = null;
                    try { studio = Singleton<Studio.Studio>.Instance; } catch (Exception) { }
                    if (studio == null) return Json.Obj("ok", false, "error", "只能在工作室里回滚");
                    int del = DeleteStudioChars(nm);
                    studio.AddFemale(path);
                    StartCoroutine(DressAfterLoad(nm));
                    AddActivity("√ 回滚到快照 " + id + (del > 0 ? ("（替换 " + del + " 个同名角色）") : ""));
                    return Json.Obj("ok", true, "op", "restore", "id", id, "card", path, "replaced", del);
                });
            }

            if (op == "drop")
            {
                string id = Str(req, "id", null);
                if (string.IsNullOrEmpty(id)) throw new Exception("drop 需要 id");
                string path = Path.Combine(TxnDir, SanitizeFileName(id) + ".png");
                if (!File.Exists(path)) throw new Exception("没有这个快照: " + id);
                File.Delete(path);
                AddActivity("√ 删除快照 " + id);
                return Json.Obj("ok", true, "op", "drop", "id", id);
            }

            throw new Exception("op 只支持 snapshot/restore/list/drop");
        }

        private static string TxLabel(string fileName)
        {
            string s = Path.GetFileNameWithoutExtension(fileName);
            int i = s.IndexOf('_', 9);
            return i > 0 && i + 1 < s.Length ? s.Substring(i + 1) : "";
        }

        private object Inspect(string query)
        {            string file = QueryValue(query, "file");
            string sex = QueryValue(query, "sex");
            if (string.IsNullOrEmpty(file)) throw new Exception("用法: /inspect?file=卡片名.png&sex=female");
            bool male = string.Equals(sex, "male", StringComparison.OrdinalIgnoreCase);
            byte sexB = male ? (byte)0 : (byte)1;
            string name = EnsurePng(file);
            string userDir = Path.Combine(GameRoot, male ? "UserData/chara/male" : "UserData/chara/female");
            string presetDir = Path.Combine(GameRoot, male ? "DefaultData/chara/male" : "DefaultData/chara/female");
            string path = Path.Combine(userDir, name);
            if (!File.Exists(path)) path = Path.Combine(presetDir, file);
            if (!File.Exists(path)) throw new Exception("找不到卡片: " + file);

            return RunOnMain(delegate { return InspectOnMain(path, sexB); });
        }

        private object InspectOnMain(string path, byte sexB)
        {
            ChaFileControl cf = new ChaFileControl();
            if (!cf.LoadCharaFile(path, sexB, true, true))
                throw new Exception("读取卡片失败: " + path);

            var result = new Dictionary<string, object>();
            result["ok"] = true;
            result["file"] = path;

            ChaFileParameter prm = cf.parameter;
            if (prm != null)
                result["parameter"] = Json.Obj("fullname", prm.fullname, "lastname", prm.lastname,
                    "firstname", prm.firstname, "nickname", prm.nickname,
                    "personality", prm.personality, "birthMonth", (int)prm.birthMonth,
                    "birthDay", (int)prm.birthDay, "bloodType", (int)prm.bloodType, "sex", (int)prm.sex);

            if (cf.custom != null && cf.custom.face != null)
            {
                ChaFileFace fc = cf.custom.face;
                var pupils = new List<object>();
                if (fc.pupil != null)
                    foreach (ChaFileFace.PupilInfo pi in fc.pupil)
                        pupils.Add(Json.Obj("id", pi.id, "baseColor", Hex(pi.baseColor), "subColor", Hex(pi.subColor)));
                result["face"] = Json.Obj("headId", fc.headId, "skinId", fc.skinId,
                    "eyebrowId", fc.eyebrowId, "eyebrowColor", Hex(fc.eyebrowColor),
                    "eyelineColor", Hex(fc.eyelineColor), "lipLineColor", Hex(fc.lipLineColor),
                    "whiteId", fc.whiteId, "hlUpId", fc.hlUpId, "hlDownId", fc.hlDownId,
                    "noseId", fc.noseId, "moleId", fc.moleId,
                    "eyelineUpId", fc.eyelineUpId, "eyelineUpWeight", (double)fc.eyelineUpWeight,
                    "eyelineDownId", fc.eyelineDownId, "lipLineId", fc.lipLineId,
                    "detailId", fc.detailId, "detailPower", (double)fc.detailPower,
                    "cheekGlossPower", (double)fc.cheekGlossPower, "lipGlossPower", (double)fc.lipGlossPower,
                    "pupilWidth", (double)fc.pupilWidth, "pupilHeight", (double)fc.pupilHeight,
                    "doubleTooth", fc.doubleTooth, "pupil", pupils);
                var faceNotes = new List<object>();
                if (fc.detailPower > 0.05f) faceNotes.Add(Json.Obj("item", "face.detailPower", "value", (double)fc.detailPower,
                    "note", "面部细节贴图强度，高了会显脏/不平"));
                if (fc.eyelineUpId == 0 && fc.eyelineDownId == 0)
                    faceNotes.Add(Json.Obj("item", "eyeline", "value", 0, "note", "上下眼线 id 都是 0（无眼线/睫毛），脸会显得很素"));
                if (fc.lipLineId == 0) faceNotes.Add(Json.Obj("item", "lipLineId", "value", 0, "note", "唇线 id 为 0，嘴唇没有轮廓"));
                result["face_notes"] = faceNotes;
                result["face_shape"] = FaceShapes(fc);
            }
            if (cf.custom != null && cf.custom.body != null)
            {
                ChaFileBody bd = cf.custom.body;
                result["body"] = Json.Obj("skinId", bd.skinId, "skinMainColor", Hex(bd.skinMainColor),
                    "skinSubColor", Hex(bd.skinSubColor), "nipColor", Hex(bd.nipColor),
                    "bustSoftness", (double)bd.bustSoftness, "bustWeight", (double)bd.bustWeight,
                    "detailId", bd.detailId, "detailPower", (double)bd.detailPower,
                    "skinGlossPower", (double)bd.skinGlossPower, "drawAddLine", bd.drawAddLine,
                    "nipId", bd.nipId, "nipGlossPower", (double)bd.nipGlossPower, "areolaSize", (double)bd.areolaSize,
                    "underhairId", bd.underhairId, "nailColor", Hex(bd.nailColor),
                    "sunburnId", bd.sunburnId, "sunburnColor", Hex(bd.sunburnColor),
                    "paintId", bd.paintId == null ? null : new List<object>(new object[] { bd.paintId.Length }),
                    "shapeValueBodyCount", (ReadFloatArray(bd, "shapeValueBody") ?? new float[0]).Length);

                // 体型滑条按名字输出，并标出"容易穿模"和"极端值"
                var shapes = new Dictionary<string, object>();
                var shapeWarn = new List<object>();
                float[] svb = ReadFloatArray(bd, "shapeValueBody");
                if (svb != null)
                {
                    for (int i = 0; i < svb.Length && i < BodyShapeNames.Length; i++)
                    {
                        float v = svb[i];
                        shapes[BodyShapeNames[i]] = (double)v;
                        if (v < 0.03f || v > 0.97f)
                            shapeWarn.Add(Json.Obj("shape", BodyShapeNames[i], "value", (double)v,
                                "clip_prone", IsClipProne(BodyShapeNames[i])));
                    }
                }
                result["body_shape"] = shapes;
                result["body_shape_warnings"] = shapeWarn;

                // 皮肤质感相关提示（"皮肤不平整"通常出在这几项）
                var skinNotes = new List<object>();
                if (bd.detailPower > 0.05f) skinNotes.Add(Json.Obj("item", "detailPower", "value", (double)bd.detailPower,
                    "note", "身体细节贴图强度，>0 会有凹凸/粗糙质感"));
                if (bd.drawAddLine) skinNotes.Add(Json.Obj("item", "drawAddLine", "value", true,
                    "note", "开着肌肉线条，皮肤会显得不平整"));
                if (bd.paintId != null)
                {
                    int on = 0;
                    foreach (int pid in bd.paintId) if (pid != 0) on++;
                    if (on > 0) skinNotes.Add(Json.Obj("item", "paintId", "value", on, "note", "身体涂装开着 " + on + " 层"));
                }
                if (bd.sunburnId != 0) skinNotes.Add(Json.Obj("item", "sunburnId", "value", bd.sunburnId, "note", "晒痕开着"));
                result["skin_notes"] = skinNotes;
            }
            if (cf.custom != null && cf.custom.hair != null && cf.custom.hair.parts != null)
            {
                var parts = new List<object>();
                for (int i = 0; i < cf.custom.hair.parts.Length; i++)
                {
                    ChaFileHair.PartsInfo hp = cf.custom.hair.parts[i];
                    parts.Add(Json.Obj("index", i, "id", hp == null ? -1 : hp.id,
                        "baseColor", hp == null ? null : Hex(hp.baseColor),
                        "length", hp == null ? 0.0 : (double)hp.length));
                }
                result["hair"] = Json.Obj("kind", cf.custom.hair.kind, "parts", parts);
            }

            // 服装：每套 coordinate 的每个部位 id / 颜色 / 隐藏标记，用来判断"生效的到底是哪一套"
            if (cf.coordinate != null)
            {
                var coords = new List<object>();
                for (int c = 0; c < cf.coordinate.Length; c++)
                {
                    ChaFileCoordinate coord = cf.coordinate[c];
                    var parts = new List<object>();
                    if (coord != null && coord.clothes != null && coord.clothes.parts != null)
                    {
                        for (int i = 0; i < coord.clothes.parts.Length; i++)
                        {
                            ChaFileClothes.PartsInfo cp = coord.clothes.parts[i];
                            if (cp == null) { parts.Add(Json.Obj("part", i, "id", -1)); continue; }
                            var colors = new List<object>();
                            if (cp.colorInfo != null)
                                foreach (ChaFileClothes.PartsInfo.ColorInfo ci in cp.colorInfo)
                                    colors.Add(Json.Obj("base", Hex(ci.baseColor), "pattern", ci.pattern));
                            var hide = new List<object>();
                            if (cp.hideOpt != null)
                                foreach (bool h in cp.hideOpt) hide.Add(h);
                            parts.Add(Json.Obj("part", i, "id", cp.id, "colorInfo", colors,
                                "hideOpt", hide, "sleevesType", cp.sleevesType));
                        }
                    }
                    coords.Add(Json.Obj("index", c, "clothes", parts,
                        "subPartsId", SubParts(coord),
                        "accessories", AccessoryParts(coord)));
                }
                result["coordinateType"] = cf.status == null ? -1 : cf.status.coordinateType;
                var states = new List<object>();
                if (cf.status != null && cf.status.clothesState != null)
                    foreach (byte s in cf.status.clothesState) states.Add((int)s);
                result["clothesState"] = states;   // 0=穿着 1=半脱 2=脱下：即使服装数据正确，状态为 2 也不显示
                result["coordinates"] = coords;
            }
            return result;
        }

        private static string Hex(Color c)
        {
            return "#" + ((int)Math.Round(c.r * 255f)).ToString("X2")
                       + ((int)Math.Round(c.g * 255f)).ToString("X2")
                       + ((int)Math.Round(c.b * 255f)).ToString("X2");
        }

        // 调试用：改一个值 → 保存 → 回读，确认哪个字段能真正落盘
        private object Probe(string query)
        {
            string file = QueryValue(query, "file");
            if (string.IsNullOrEmpty(file)) throw new Exception("用法: /probe?file=卡片.png[&top=1][&state=0]");
            string topStr = QueryValue(query, "top");
            string stateStr = QueryValue(query, "state");
            int topId = topStr == null ? -1 : int.Parse(topStr);
            int state = stateStr == null ? -1 : int.Parse(stateStr);
            string path = Path.Combine(GameRoot, "UserData/chara/female", EnsurePng(file));
            if (!File.Exists(path)) throw new Exception("找不到卡片: " + path);
            string outPath = Path.Combine(GameRoot, "UserData/chara/female", "probe_" + EnsurePng(file));

            return RunOnMain(delegate
            {
                var r = new Dictionary<string, object>();
                ChaFileControl cf = new ChaFileControl();
                if (!cf.LoadCharaFile(path, 1, true, true)) throw new Exception("读取失败");
                r["before_state"] = StateList(cf);
                r["before_topId"] = cf.coordinate[0].clothes.parts[0].id;

                if (topId >= 0) cf.coordinate[0].clothes.parts[0].id = topId;
                if (state >= 0)
                {
                    if (cf.status.clothesState.Length > 0) cf.status.clothesState[0] = (byte)state;
                    if (cf.status.clothesState.Length > 8) cf.status.clothesState[8] = (byte)state;
                }
                r["inMemory_state"] = StateList(cf);
                r["inMemory_topId"] = cf.coordinate[0].clothes.parts[0].id;

                if (!cf.SaveCharaFile(outPath, 1, true)) throw new Exception("保存失败: " + outPath);
                r["savedTo"] = outPath;

                ChaFileControl back = new ChaFileControl();
                if (!back.LoadCharaFile(outPath, 1, true, true)) throw new Exception("回读失败");
                r["after_state"] = StateList(back);
                r["after_topId"] = back.coordinate[0].clothes.parts[0].id;
                r["ok"] = true;
                return r;
            });
        }

        private static object StateList(ChaFileControl cf)
        {
            var l = new List<object>();
            if (cf.status != null && cf.status.clothesState != null)
                foreach (byte b in cf.status.clothesState) l.Add((int)b);
            return l;
        }

        private static object SubParts(ChaFileCoordinate coord)
        {
            var list = new List<object>();
            if (coord != null && coord.clothes != null && coord.clothes.subPartsId != null)
                foreach (int v in coord.clothes.subPartsId) list.Add(v);
            return list;
        }

        private static object AccessoryParts(ChaFileCoordinate coord)
        {
            var list = new List<object>();
            if (coord == null || coord.accessory == null || coord.accessory.parts == null) return list;
            for (int i = 0; i < coord.accessory.parts.Length; i++)
            {
                ChaFileAccessory.PartsInfo p = coord.accessory.parts[i];
                if (p == null || p.type == AccessoryEmptyType) continue;
                int semantic = p.type - 120;
                var cols = new List<object>();
                if (p.color != null) foreach (Color c in p.color) cols.Add(Hex(c));
                list.Add(Json.Obj("slot", i, "type", p.type,
                    "typeName", semantic >= 0 && semantic < AccessoryTypeNames.Length ? AccessoryTypeNames[semantic] : "?",
                    "id", p.id, "parent", p.parentKey, "colors", cols));
            }
            return list;
        }

        // ============ /focus：把工作室相机对准人物（保持数秒后交还控制权） ============
        private object FocusCamera(string body)
        {
            Dictionary<string, object> req = Json.Parse(body) as Dictionary<string, object>;
            string name = Str(req, "name", null);
            float hold = 3f;
            object hv;
            if (req != null && req.TryGetValue("hold", out hv) && hv is double) hold = (float)Math.Max(0.5, Math.Min(30.0, (double)hv));

            var tcs = new TaskCompletionSource<object>();
            _mainThread.Enqueue(delegate
            {
                try { StartCoroutine(FocusCoroutine(name, hold, tcs)); }
                catch (Exception e) { tcs.SetException(e); }
            });
            if (!tcs.Task.Wait(20000))
                throw new Exception("聚焦超时（游戏可能在加载中）");
            if (tcs.Task.IsFaulted) throw tcs.Task.Exception.InnerException ?? tcs.Task.Exception;
            return tcs.Task.Result;
        }

        private IEnumerator FocusCoroutine(string name, float hold, TaskCompletionSource<object> tcs)
        {
            Camera cam = Camera.main;
            Studio.Studio studio = null;
            try { studio = Singleton<Studio.Studio>.Instance; } catch (Exception) { }
            if (cam == null || studio == null) { tcs.SetException(new Exception("当前不在工作室场景")); yield break; }

            string targetName;
            ChaControl target = FindStudioChar(name, out targetName);
            if (target == null) { tcs.SetException(new Exception("场景中没有可聚焦的角色")); yield break; }

            Studio.CameraControl ctrl = cam.GetComponent<Studio.CameraControl>();
            if (ctrl != null) ctrl.enabled = false;   // 暂停相机脚本，否则会被每帧覆盖回原视角
            try { FrameCharacter(cam, target); }
            catch (Exception e)
            {
                if (ctrl != null) ctrl.enabled = true;   // 失败也要交还控制权，用户视角不能被卡死
                tcs.SetException(e);
                yield break;
            }
            AddActivity("√ 相机已对准 " + targetName + "（保持 " + hold.ToString("0.#") + " 秒）");

            try
            {
                yield return new WaitForSecondsRealtime(hold);
            }
            finally
            {
                if (ctrl != null) ctrl.enabled = true;    // 交还控制，用户可继续自由操作
            }

            Vector3 p = target.transform.position;
            tcs.TrySetResult(Json.Obj("ok", true, "focus", targetName, "hold", (double)hold,
                "position", Json.Obj("x", (double)p.x, "y", (double)p.y, "z", (double)p.z)));
        }

        // 站到角色正前方看她（用 -forward 会拍到后脑勺），并把视点放在指定高度
        private const float ShotFov = 35f;   // 截图固定视角，使取景与游戏内的相机 FOV 设置无关

        // fitHeight = 画面需要容纳的高度（米），focalHeight = 视点离脚底的高度
        private static void FrameCharacter(Camera cam, ChaControl target, float fitHeight, float focalHeight, Vector3 dir)
        {
            cam.fieldOfView = ShotFov;
            float dist = (fitHeight * 0.5f) / Mathf.Tan(ShotFov * 0.5f * Mathf.Deg2Rad) + 0.25f;
            Vector3 focus = target.transform.position + new Vector3(0f, focalHeight, 0f);
            cam.transform.position = focus + dir * dist + Vector3.up * 0.10f;
            cam.transform.LookAt(focus);
        }

        private static void FrameCharacter(Camera cam, ChaControl target)
        {
            FrameCharacter(cam, target, 1.05f, 1.25f, FlattenedForward(target));
        }

        // 观察方向：front 正前方 / back 背面 / left 左侧 / right 右侧（看背面蝴蝶结、尾巴、发长用 back）
        private static Vector3 ViewDir(ChaControl target, string view)
        {
            Vector3 fwd = FlattenedForward(target);
            Vector3 right = Vector3.Cross(Vector3.up, fwd).normalized;
            switch ((view ?? "front").ToLowerInvariant())
            {
                case "back": return -fwd;
                case "left": return -right;
                case "right": return right;
                default: return fwd;
            }
        }

        // 按取景名给出（画面容纳高度, 视点高度）：全身/半身/头部
        private static void FramingOf(string framing, out float fitHeight, out float focalHeight)
        {
            switch ((framing ?? "").ToLowerInvariant())
            {
                case "full": fitHeight = 1.90f; focalHeight = 0.88f; break;   // 全身（含头顶余量）
                case "head": fitHeight = 0.52f; focalHeight = 1.40f; break;   // 头部特写
                case "torso": fitHeight = 0.75f; focalHeight = 1.05f; break;  // 躯干（查穿模）
                case "face": fitHeight = 0.30f; focalHeight = 1.44f; break;   // 面部特写（看妆容）
                case "eyes": fitHeight = 0.14f; focalHeight = 1.47f; break;   // 眼部特写（查眼型/瞳色）
                default: fitHeight = 1.05f; focalHeight = 1.22f; break;       // bust 半身
            }
        }

        private ChaControl FindStudioChar(string name, out string foundName)
        {
            foundName = "";
            Studio.Studio studio = null;
            try { studio = Singleton<Studio.Studio>.Instance; } catch (Exception) { }
            if (studio == null) return null;
            ChaControl last = null;
            string lastN = "";
            foreach (Studio.ObjectCtrlInfo info in studio.dicObjectCtrl.Values)
            {
                Studio.OCIChar oci = info as Studio.OCIChar;
                if (oci == null) continue;
                ChaControl cc = GetChaControl(oci);
                if (cc == null) continue;
                string cn = cc.chaFile != null && cc.chaFile.parameter != null ? (cc.chaFile.parameter.fullname ?? "") : "";
                if (!string.IsNullOrEmpty(name) && cn.IndexOf(name, StringComparison.Ordinal) >= 0)
                {
                    foundName = cn;
                    return cc;
                }
                last = cc; lastN = cn;
            }
            // 指定了名字但没匹配上：不静默换人，明确返回未找到
            if (!string.IsNullOrEmpty(name)) return null;
            foundName = lastN;
            return last;
        }

        private static ChaControl GetChaControl(Studio.OCIChar oci)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;
            try
            {
                PropertyInfo p = oci.GetType().GetProperty("chaCtrl", F);
                if (p != null) return p.GetValue(oci, null) as ChaControl;
                FieldInfo f = oci.GetType().GetField("chaCtrl", F);
                if (f != null) return f.GetValue(oci) as ChaControl;
                p = oci.GetType().GetProperty("charInfo", F);
                if (p != null) return p.GetValue(oci, null) as ChaControl;
                f = oci.GetType().GetField("charInfo", F);
                if (f != null) return f.GetValue(oci) as ChaControl;
            }
            catch (Exception) { }
            return null;
        }

        private static object GetMemberValue(object target, string name)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;
            PropertyInfo p = target.GetType().GetProperty(name, F);
            if (p != null) return p.GetValue(target, null);
            FieldInfo f = target.GetType().GetField(name, F);
            return f == null ? null : f.GetValue(target);
        }

        private static bool SetMemberValue(object target, string name, object value)
        {
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.IgnoreCase;
            PropertyInfo p = target.GetType().GetProperty(name, F);
            if (p != null && p.CanWrite) { p.SetValue(target, value, null); return true; }
            FieldInfo f = target.GetType().GetField(name, F);
            if (f != null) { f.SetValue(target, value); return true; }
            return false;
        }

        private static BridgeServer.Response Resp(int code, object payload)
        {
            return new BridgeServer.Response { Code = code, Body = Json.Write(payload) };
        }

        // ============ /status ============
        private object GetStatus()
        {
            return RunOnMain(delegate
            {
                object studio = null;
                try { studio = Singleton<Studio.Studio>.Instance; } catch (Exception) { }
                return Json.Obj(
                    "ok", true,
                    "plugin", "AICharBridge",
                    "version", "1.0.0",
                    "process", Application.productName,
                    "scene", SceneManager.GetActiveScene().name,
                    "fps", (int)Mathf.Max(_fpsSmooth, 0f),
                    "inStudio", studio != null,
                    "port", _port.Value);
            });
        }

        // ============ /characters ============
        private object ListCharacters()
        {
            return RunOnMain(delegate
            {
                return Json.Obj("ok", true, "female", ListCards(Path.Combine(GameRoot, "UserData/chara/female")),
                    "male", ListCards(Path.Combine(GameRoot, "UserData/chara/male")));
            });
        }

        private static object ListCards(string dir)
        {
            var arr = new List<object>();
            try
            {
                if (Directory.Exists(dir))
                {
                    foreach (FileInfo f in new DirectoryInfo(dir).GetFiles("*.png"))
                    {
                        arr.Add(Json.Obj("file", f.Name, "size", f.Length,
                            "modified", f.LastWriteTime.ToString("yyyy-MM-dd HH:mm")));
                    }
                }
            }
            catch (Exception) { }
            return arr;
        }

        // ============ /presets ============
        private object ListPresets()
        {
            string root = Path.Combine(GameRoot, "DefaultData/chara");
            return Json.Obj("ok", true,
                "female", ListCards(Path.Combine(root, "female")),
                "male", ListCards(Path.Combine(root, "male")),
                "note", "DefaultData 卡片以官方人格命名，可作为生成基底");
        }

        // ============ /options ============
        private static readonly int[] OptionCats = new int[] {
            // 发型
            101, 102, 103, 104,
            // 服装
            105, 106, 107, 108, 109, 110, 111, 112,
            // 五官（脸型/眼/眉/睫毛/眼线/唇/鼻/痣）
            400, 401, 402, 403, 404, 406, 407, 408, 410, 412, 414, 415,
            // 饰品（无/发/头/脸/颈/身/腰/腿/臂/手）
            // ⚠ 必须从 120 开始：ChaAccessoryDefine.AccessoryCategoryTypeNone = 120，
            // 即 120=none、121=hair、122=head、…、129=hand。
            // 曾写成 121..129（漏了 120）→ 每个分类都错位一位：
            // /options?category=ao_head 实际读的是 face 分类，AI 拿到的 id 属于别的分类，
            // 生成时又被校验拒掉；我还因此误判"本机没有女仆头饰/鳍耳"。
            // 展示与校验必须用同一套分类号。
            120, 121, 122, 123, 124, 125, 126, 127, 128, 129
        };
        private static readonly string[] OptionNames = new string[] {
            "hair_back", "hair_front", "hair_side", "hair_option",
            "top", "bot", "bra", "shorts", "gloves", "panst", "socks", "shoes",
            "face_detail", "eyeshadow", "cheek", "lip", "lipline", "eyebrow",
            "eye_white", "eye", "eye_hi_up", "eyeline_up", "nose", "mole",
            "ao_none", "ao_hair", "ao_head", "ao_face", "ao_neck", "ao_body",
            "ao_waist", "ao_leg", "ao_arm", "ao_hand"
        };

        private object GetOptions(string query)
        {
            string only = QueryValue(query, "category");
            return RunOnMain(delegate
            {
                ChaListControl ctl = null;
                try { ctl = Manager.Character.chaListCtrl; } catch (Exception) { }
                if (ctl == null) throw new Exception("人物列表尚未加载完成（游戏还在启动中）");

                var result = new Dictionary<string, object>();
                for (int i = 0; i < OptionCats.Length; i++)
                {
                    if (!string.IsNullOrEmpty(only) && OptionNames[i] != only) continue;
                    result[OptionNames[i]] = ReadCategory(ctl, OptionCats[i]);
                }
                if (result.Count == 0)
                    throw new Exception("没有这个分类: " + only + "（可用: " + string.Join(", ", OptionNames) + "）");
                result["ok"] = true;
                result["note"] = "发型/服装 ID 来自当前游戏（含 Mod），生成时直接引用 id 即可；可用 ?category=hair_back 只取一类";
                return result;
            });
        }

        private static object ReadCategory(ChaListControl ctl, int categoryNo)
        {
            var arr = new List<object>();
            Dictionary<int, ListInfoBase> info;
            try { info = ctl.GetCategoryInfo((ChaListDefine.CategoryNo)categoryNo); }
            catch (Exception) { info = null; }
            if (info == null) return arr;
            List<int> ids = new List<int>(info.Keys);
            ids.Sort();
            foreach (int id in ids)
            {
                ListInfoBase lb = info[id];
                if (lb == null) continue;
                string n = lb.Name == null ? "" : lb.Name;
                if (n.Length > 60) n = n.Substring(0, 60);
                arr.Add(Json.Obj("id", id, "name", n));
            }
            return arr;
        }

        private static string QueryValue(string query, string key)
        {
            if (string.IsNullOrEmpty(query)) return null;
            foreach (string pair in query.Split('&'))
            {
                int eq = pair.IndexOf('=');
                if (eq > 0 && pair.Substring(0, eq) == key)
                {
                    string v = pair.Substring(eq + 1).Replace('+', ' ');
                    try { return Uri.UnescapeDataString(v); }   // 卡片名可能含中文，必须解码
                    catch (Exception) { return v; }
                }
            }
            return null;
        }

        // 发型/服装 ID 是否真实存在（防止 AI 填一个不存在的 ID 生成出残缺角色）
        private static int CountInCategory(int categoryNo)
        {
            try
            {
                ChaListControl ctl = Manager.Character.chaListCtrl;
                if (ctl == null) return -1;
                return ctl.CountInfo((ChaListDefine.CategoryNo)categoryNo);
            }
            catch (Exception) { return -1; }
        }

        // 未知 ID 时给出最接近的候选（借鉴 dsh-blender-plugin 的 did_you_mean）
        private static string DidYouMean(ChaListControl ctl, int categoryNo, int want)
        {
            try
            {
                Dictionary<int, ListInfoBase> info = ctl.GetCategoryInfo((ChaListDefine.CategoryNo)categoryNo);
                if (info == null || info.Count == 0) return "可用 kks_list_options 查询有效 ID";
                var near = new List<int>();
                for (int d = 1; d <= 15 && near.Count < 3; d++)
                {
                    if (info.ContainsKey(want - d)) near.Add(want - d);
                    if (info.ContainsKey(want + d)) near.Add(want + d);
                }
                if (near.Count == 0)
                {
                    int maxId = 0;
                    foreach (int k in info.Keys) if (k > maxId) maxId = k;
                    return "该分类共 " + info.Count + " 项，id 范围 0~" + maxId + "（用 kks_list_options 查具体条目）";
                }
                var parts = new List<string>();
                foreach (int n in near)
                {
                    ListInfoBase lb = info[n];
                    string nm = lb != null && lb.Name != null ? lb.Name.TrimEnd('\u180e') : "?";
                    parts.Add(n + " = " + nm);
                }
                return "相近的有 → " + string.Join(" / ", parts.ToArray());
            }
            catch (Exception) { return "可用 kks_list_options 查询有效 ID"; }
        }

        private static bool ValidatePartId(Applier applier, string label, int id, int categoryNo)
        {
            if (id < 0) { applier.Skip(label, "ID 不能为负数"); return false; }
            if (CountInCategory(categoryNo) <= 0) return true; // 列表还没加载好，不拦
            try
            {
                ChaListControl ctl = Manager.Character.chaListCtrl;
                if (ctl != null && !ctl.ContainsInfo((ChaListDefine.CategoryNo)categoryNo, id))
                {
                    applier.Skip(label, "ID " + id + " 不存在。" + DidYouMean(ctl, categoryNo, id));
                    return false;
                }
            }
            catch (Exception) { }
            return true;
        }

        // ============ /generate ============
        private object Generate(string body)
        {
            Dictionary<string, object> req = Json.Parse(body) as Dictionary<string, object>;
            if (req == null) throw new Exception("请求体不是合法 JSON 对象");
            return RunOnMain(delegate { return GenerateOnMain(req); }, 120000);
        }

        private object GenerateOnMain(Dictionary<string, object> req)
        {
            string sex = Str(req, "sex", "female").ToLowerInvariant();
            bool male = sex == "male" || sex == "0";
            byte sexB = male ? (byte)0 : (byte)1;
            string sexDir = male ? "male" : "female";
            AddActivity("开始生成人物（" + sex + "）…");

            string root = GameRoot;
            string userDir = Path.Combine(root, "UserData/chara/" + sexDir);
            string presetDir = Path.Combine(root, "DefaultData/chara/" + sexDir);

            // 1. 选基底卡：显式指定 > 用户卡 > 官方预设
            string basePath = null;
            string baseName = Str(req, "base", null);
            if (!string.IsNullOrEmpty(baseName))
            {
                string p1 = Path.Combine(userDir, EnsurePng(baseName));
                string p2 = Path.Combine(presetDir, baseName);
                if (File.Exists(p1)) basePath = p1;
                else if (File.Exists(p2)) basePath = p2;
                else throw new Exception("找不到基底卡: " + baseName);
            }
            else
            {
                string[] files = Directory.Exists(presetDir) ? Directory.GetFiles(presetDir, "*.png") : new string[0];
                if (files.Length == 0) throw new Exception("没有可用的官方预设卡: " + presetDir);
                Array.Sort(files, StringComparer.Ordinal);
                basePath = files[0];
            }

            // 2. 目标路径
            string saveAs = Str(req, "save_as", null);
            if (string.IsNullOrEmpty(saveAs)) saveAs = "AI_" + DateTime.Now.ToString("yyyyMMdd_HHmmss");
            saveAs = EnsurePng(SanitizeFileName(saveAs));
            string dest = Path.Combine(userDir, saveAs);
            if (File.Exists(dest) && !Bool(req, "overwrite", false))
                throw new Exception("目标已存在: " + saveAs + "（overwrite=true 可覆盖）");

            // 3. 读取基底卡
            ChaFileControl cf = new ChaFileControl();
            if (!cf.LoadCharaFile(basePath, sexB, true, true))
                throw new Exception("读取基底卡失败: " + basePath);

            // 4. 应用参数（KKS 结构：face/body/hair 在 cf.custom，服装在 coordinate 里）
            var applier = new Applier();
            int replaced = 0;
            ApplySection(applier, req, "parameter", cf.parameter);
            ClampParameter(req, applier, cf.parameter);
            if (cf.custom != null)
            {
                if (req.ContainsKey("face") && cf.custom.face != null)
                {
                    Dictionary<string, object> face = req["face"] as Dictionary<string, object>;
                    if (face != null)
                    {
                        // pupil 作用于双眼，其余键照常应用到 face 本体（此前写成 if/else，导致带 pupil 时其他五官参数被丢弃）
                        if (face.ContainsKey("pupil"))
                        {
                            var pv = face["pupil"] as Dictionary<string, object>;
                            if (pv != null && cf.custom.face.pupil != null)
                            {
                                for (int e = 0; e < cf.custom.face.pupil.Length; e++)
                                    applier.Apply(cf.custom.face.pupil[e], pv, "face.pupil[" + e + "]");
                            }
                            var rest = new Dictionary<string, object>(face);
                            rest.Remove("pupil");
                            if (rest.Count > 0) applier.Apply(cf.custom.face, rest, "face");
                        }
                        else applier.Apply(cf.custom.face, face, "face");
                    }
                }
                ApplySection(applier, req, "body", cf.custom.body);
                ApplyFaceShape(applier, req, cf.custom.face);
                ApplyBodyShape(applier, req, cf.custom.body);
                NormalizeBodyShape(applier, req, cf.custom.body);
                ApplySmoothSkin(applier, req, cf.custom.body, cf.custom.face);
                if (req.ContainsKey("hair") && cf.custom.hair != null && cf.custom.hair.parts != null)
                {
                    Dictionary<string, object> hair = req["hair"] as Dictionary<string, object>;
                    foreach (KeyValuePair<string, object> kv in hair)
                    {
                        int idx = HairIndex(kv.Key);
                        if (idx < 0 || idx >= cf.custom.hair.parts.Length) { applier.Skip("hair." + kv.Key, "无此发区"); continue; }
                        var part = kv.Value as Dictionary<string, object>;
                        if (part == null) continue;
                        double? wantId = Num(part, "id");
                        // 发型部位 0~3 对应分类 101~104
                        if (wantId.HasValue && !ValidatePartId(applier, "hair." + kv.Key + ".id", (int)wantId.Value, 101 + idx)) continue;
                        applier.Apply(cf.custom.hair.parts[idx], part, "hair." + kv.Key);
                    }
                }
            }
            if (req.ContainsKey("clothes") && cf.coordinate != null && cf.coordinate.Length > 0)
            {
                int ci = cf.status != null ? cf.status.coordinateType : 0;
                if (ci < 0 || ci >= cf.coordinate.Length) ci = 0;
                ChaFileCoordinate coord = cf.coordinate[ci];
                if (coord != null && coord.clothes != null && coord.clothes.parts != null)
                {
                    Dictionary<string, object> cl = req["clothes"] as Dictionary<string, object>;
                    foreach (KeyValuePair<string, object> kv in cl)
                    {
                        int idx = ClothesIndex(kv.Key);
                        if (idx < 0 || idx >= coord.clothes.parts.Length) { applier.Skip("clothes." + kv.Key, "无此部位"); continue; }
                        var part = kv.Value as Dictionary<string, object>;
                        if (part == null) continue;
                        double? wantId = Num(part, "id");
                        // 服装部位 0~7 对应分类 105~112
                        if (wantId.HasValue && !ValidatePartId(applier, "clothes." + kv.Key + ".id", (int)wantId.Value, 105 + idx)) continue;
                        applier.Apply(coord.clothes.parts[idx], part, "clothes." + kv.Key);
                        // ⚠ shoes 是**两个部件**：parts[7]=shoes_inner、parts[8]=shoes_outer，
                        // 外观（看得见的那双鞋）在 shoes_outer。只写 parts[7] 时数据变了但外观不变 ——
                        // 实测换 1/5/8/21 四个鞋 id 渲染出的是同一双基座卡的鞋。
                        if (idx == 7 && coord.clothes.parts.Length > 8)
                            applier.Apply(coord.clothes.parts[8], part, "clothes." + kv.Key + "(outer)");
                        // 关键：服装数据对但不等于会显示——官方预设卡里很多部位存的是"已脱"状态。
                        // 这里把被设置的部位同步改成"穿着"，否则生成出来的人会是半裸的。
                        SetClothesState(applier, cf, idx, kv.Key, part);
                    }
                }
            }
            ApplyAccessories(applier, req, cf);

            // 5. 保存并回读验证
            if (!Directory.Exists(userDir)) Directory.CreateDirectory(userDir);
            if (!cf.SaveCharaFile(dest, sexB, true))
                throw new Exception("保存卡片失败: " + dest);
            bool verified = false;
            try
            {
                ChaFileControl check = new ChaFileControl();
                verified = check.LoadCharaFile(dest, sexB, true, true) &&
                           check.parameter != null && check.parameter.nickname != null;
            }
            catch (Exception) { verified = false; }

            // 6. 可选：在工作室内加载
            bool loaded = false;
            string note = "";
            if (Bool(req, "load", false))
            {
                Studio.Studio studio = null;
                try { studio = Singleton<Studio.Studio>.Instance; } catch (Exception) { }
                if (studio == null)
                {
                    note = "当前不在工作室场景，卡片已保存但未加载";
                }
                else
                {
                    try
                    {
                        // 默认"替换同名角色"，而不是再加一个：不然反复 load 会在原点叠一摞同名模型，
                        // 渲染出来互相穿插，会被误读成"穿模"。要真的并排多个角色请传 add:true。
                        string genName = cf.parameter != null ? cf.parameter.fullname : null;
                        bool addMode = Bool(req, "add", false);
                        replaced = 0;
                        if (!addMode && !string.IsNullOrEmpty(genName))
                            replaced = DeleteStudioChars(genName);
                        if (male) studio.AddMale(dest); else studio.AddFemale(dest);
                        loaded = true;
                        // 卡里存的服装状态可能是"已脱"（官方预设卡就是这样），加载后显式恢复穿着
                        StartCoroutine(DressAfterLoad(genName));
                    }
                    catch (Exception e) { note = "工作室加载失败: " + e.Message; }
                }
            }

            Instance.Logger.LogInfo("已生成人物卡 " + saveAs + "（基底 " + Path.GetFileName(basePath) + "）");
            AddActivity("√ 已生成 " + saveAs + "（基底 " + Path.GetFileName(basePath) + "，应用 " +
                applier.Applied.Count + " 项，跳过 " + applier.Skipped.Count + " 项，回读验证" + (verified ? "通过" : "失败") + "）" +
                (loaded ? "，已加载到工作室" : ""));
            var sceneChars = new List<ChaControl>();
            int sceneCount = loaded ? StudioCharCount(sceneChars) : 0;
            return Json.Obj("ok", true, "saved", dest, "base", basePath, "verified", verified,
                "loaded", loaded, "note", note, "replaced_same_name", replaced, "scene_chars", sceneCount,
                "applied", applier.Applied, "skipped", applier.Skipped);
        }

        private void ApplySection(Applier applier, Dictionary<string, object> req, string key, object target)
        {
            if (!req.ContainsKey(key) || target == null) return;
            var dict = req[key] as Dictionary<string, object>;
            if (dict != null) applier.Apply(target, dict, key);
        }

        // 饰品：鱼鳍耳、尾巴、头饰等。type 1~10 对应分类 121~130，位置由游戏按默认值计算
        private void ApplyAccessories(Applier applier, Dictionary<string, object> req, ChaFileControl cf)
        {
            // 注意：clear_accessories 必须独立于 accessories 数组判断，
            // 否则"只清空、不加饰品"时这里直接 return，基座卡的残留饰品（发饰/项链）会留在卡片里。
            bool clear = Bool(req, "clear_accessories", false);
            List<object> list = req.ContainsKey("accessories") ? req["accessories"] as List<object> : null;
            if (!clear && list == null) return;
            if (cf.coordinate == null || cf.coordinate.Length == 0) return;

            int ci = cf.status != null ? cf.status.coordinateType : 0;
            if (ci < 0 || ci >= cf.coordinate.Length) ci = 0;
            ChaFileCoordinate coord = cf.coordinate[ci];
            ChaFileAccessory acc = coord == null ? null : coord.accessory;
            if (acc == null || acc.parts == null)
            {
                applier.Skip("accessories", "该套装的饰品数据不可用");
                return;
            }

            // 官方预设卡常把 20 个饰品槽占满，生成新人物时需要先腾空才能放自己的饰品
            if (clear && acc.parts != null)
            {
                _pendingAcs.Clear();
                int cleared = 0;
                for (int s = 0; s < acc.parts.Length; s++)
                {
                    if (acc.parts[s] != null && acc.parts[s].type != AccessoryEmptyType) cleared++;
                    acc.parts[s] = new ChaFileAccessory.PartsInfo();
                }
                applier.Note("clear_accessories", "已清空 " + cleared + " 个原有饰品槽");
            }
            if (list == null) return;

            for (int ai = 0; ai < list.Count; ai++)
            {
                var item = list[ai] as Dictionary<string, object>;
                if (item == null) continue;
                string label = "accessories[" + ai + "]";

                int type = AccessoryTypeOf(item);
                if (type < 1 || type > 10)
                {
                    applier.Skip(label + ".type", "饰品部位不合法（可用 hair/head/face/neck/body/waist/leg/arm/hand）");
                    continue;
                }
                double? wid = Num(item, "id");
                if (!wid.HasValue) { applier.Skip(label + ".id", "缺少 id"); continue; }
                if (!ValidatePartId(applier, label + ".id", (int)wid.Value, 120 + type)) continue;

                double? wantSlot = Num(item, "slot");
                int slot = wantSlot.HasValue ? (int)wantSlot.Value : -1;
                if (slot < 0)
                {
                    for (int s = 0; s < acc.parts.Length; s++)
                        if (acc.parts[s] == null || acc.parts[s].type == AccessoryEmptyType) { slot = s; break; }
                }
                if (slot < 0 || slot >= acc.parts.Length)
                {
                    applier.Skip(label, "没有空闲饰品槽（最多 " + acc.parts.Length + " 个，可用 clear_accessories=true 先清空）");
                    continue;
                }
                if (acc.parts[slot] == null) acc.parts[slot] = new ChaFileAccessory.PartsInfo();
                ChaFileAccessory.PartsInfo ap = acc.parts[slot];
                ap.type = 120 + type;   // 卡片里存的是饰品分类号（120=空，121=发饰，122=头饰…）
                ap.id = (int)wid.Value;
                applier.Note(label + ".slot" + slot, AccessoryTypeNames[type] + " id=" + (int)wid.Value);
                _pendingAcs.Add(new PendingAcs { Slot = slot, Type = 120 + type, Id = (int)wid.Value, Parent = Str(item, "parent", null) });

                var colors = item.ContainsKey("colors") ? item["colors"] as List<object> : null;
                if (colors != null)
                {
                    if (ap.color == null) ap.color = new Color[colors.Count];
                    for (int c = 0; c < colors.Count && c < ap.color.Length; c++)
                    {
                        try { ap.color[c] = ParseColorValue(colors[c]); applier.Note(label + ".color" + c, colors[c]); }
                        catch (Exception e) { applier.Skip(label + ".color" + c, e.Message); }
                    }
                }
                string parent = Str(item, "parent", null);
                if (!string.IsNullOrEmpty(parent)) { ap.parentKey = parent; applier.Note(label + ".parent", parent); }
            }
        }

        // 加载完角色后让它穿上衣服、戴上饰品：走游戏自身的运行时接口，比只改卡片数据可靠
        private IEnumerator DressAfterLoad(string name)
        {
            for (int i = 0; i < 40; i++) yield return null;   // 等角色对象初始化完
            string found;
            ChaControl cc = FindStudioChar(name, out found);
            if (cc == null) yield break;
            try
            {
                cc.SetClothesStateAll(0);                     // 0 = 穿着
                AddActivity("√ 已让 " + found + " 穿上服装");
            }
            catch (Exception e) { AddActivity("! 恢复服装状态失败: " + e.Message); }
            // 饰品：逐槽用游戏自身的接口设置（默认父骨骼 + 默认位置），比只写卡片数据可靠
            List<PendingAcs> pend = new List<PendingAcs>(_pendingAcs);
            for (int i = 0; i < pend.Count; i++)
            {
                PendingAcs pa = pend[i];
                string parent = pa.Parent;
                if (string.IsNullOrEmpty(parent))
                {
                    try { parent = cc.GetAccessoryDefaultParentStr(pa.Slot); }
                    catch (Exception) { }
                }
                string err = null;
                try { cc.ChangeAccessoryNoAsync(pa.Slot, pa.Type, pa.Id, parent, false, true); }
                catch (Exception e) { err = e.Message; }
                if (err != null) AddActivity("! 饰品槽 " + pa.Slot + " 设置失败: " + err);
                // ⚠ 必须再走一次 ChangeAccessoryColor：ChangeAccessoryNoAsync 只挂模型，
                // **不会应用卡片里的颜色**，于是饰品一律是默认色（实测未染色的狐尾渲染成浅米色大块，
                // 把裙子整个挡住）。这一步才是"把颜色用上"。
                try { cc.ChangeAccessoryColor(pa.Slot); }
                catch (Exception e) { AddActivity("! 饰品槽 " + pa.Slot + " 颜色应用失败: " + e.Message); }
                yield return null;
            }
            string err2 = null;
            try { cc.UpdateAccessoryMoveAllFromInfo(); cc.SetAccessoryStateAll(true); }
            catch (Exception e) { err2 = e.Message; }
            if (err2 != null) AddActivity("! 饰品位置设置失败: " + err2);
            else if (pend.Count > 0) AddActivity("√ 已为 " + found + " 戴上 " + pend.Count + " 件饰品");
        }

        // 部位索引 -> clothesState 槽位（鞋子占 shoes_inner/shoes_outer 两个槽）
        private static void SetClothesState(Applier applier, ChaFileControl cf, int partIdx, string label, Dictionary<string, object> part)
        {
            if (cf.status == null || cf.status.clothesState == null) return;
            double? wantState = Num(part, "state");       // 0=穿着 1=半脱 2/3=脱下
            bool? wear = part.ContainsKey("wear") && part["wear"] is bool ? (bool?)part["wear"] : null;
            int st = wantState.HasValue ? (int)Math.Max(0, Math.Min(3, wantState.Value))
                                        : (wear.HasValue && !wear.Value ? 2 : 0);
            int[] slots = partIdx == 7 ? new int[] { 7, 8 } : new int[] { partIdx };
            bool ok = false;
            foreach (int si in slots)
            {
                if (si >= 0 && si < cf.status.clothesState.Length) { cf.status.clothesState[si] = (byte)st; ok = true; }
            }
            if (ok) applier.Note("clothes." + label + ".state", st == 0 ? "穿着" : (st == 1 ? "半脱" : "脱下"));
        }

        // 数值越界会被游戏判为非法数据，这里先钳到合法范围，并把钳制行为明确记录下来
        private static void ClampParameter(Dictionary<string, object> req, Applier applier, ChaFileParameter p)
        {
            if (p == null || !req.ContainsKey("parameter")) return;
            var prm = req["parameter"] as Dictionary<string, object>;
            if (prm == null) return;

            double? pm = Num(prm, "personality");
            if (pm.HasValue)
            {
                int v = (int)pm.Value;
                int clamped = Math.Max(0, Math.Min(43, v));
                if (clamped != v) applier.Skip("parameter.personality", "取值 " + v + " 越界，已钳到 " + clamped + "（合法 0~43）");
                p.personality = clamped;
            }
            if (Num(prm, "birthMonth").HasValue)
            {
                byte v = p.birthMonth;
                byte c = (byte)Math.Max(1, Math.Min(12, (int)v));
                if (c != v) applier.Skip("parameter.birthMonth", "取值 " + v + " 越界，已钳到 " + c + "（合法 1~12）");
                p.birthMonth = c;
            }
            if (Num(prm, "birthDay").HasValue)
            {
                byte v = p.birthDay;
                byte c = (byte)Math.Max(1, Math.Min(31, (int)v));
                if (c != v) applier.Skip("parameter.birthDay", "取值 " + v + " 越界，已钳到 " + c + "（合法 1~31）");
                p.birthDay = c;
            }
            if (Num(prm, "bloodType").HasValue)
            {
                byte v = p.bloodType;
                byte c = (byte)Math.Min(3, (int)v);
                if (c != v) applier.Skip("parameter.bloodType", "取值 " + v + " 越界，已钳到 " + c + "（合法 0~3）");
                p.bloodType = c;
            }
        }

        private static int HairIndex(string key)
        {
            switch (key.ToLowerInvariant())
            {
                case "back": case "後": case "后": return 0;
                case "front": case "前": return 1;
                case "side": case "侧": case "側": return 2;
                case "option": case "extension": return 3;
                default:
                    int n;
                    if (int.TryParse(key, out n)) return n;
                    return -1;
            }
        }

        private static int ClothesIndex(string key)
        {
            switch (key.ToLowerInvariant())
            {
                case "top": case "上装": return 0;
                case "bot": case "bottom": case "下装": return 1;
                case "bra": case "胸罩": return 2;
                case "shorts": return 3;
                case "gloves": case "手套": return 4;
                case "panst": case "丝袜": return 5;
                case "socks": case "袜子": return 6;
                case "shoes": case "鞋": return 7;
                default:
                    int n;
                    if (int.TryParse(key, out n)) return n;
                    return -1;
            }
        }

        // ============ /screenshot：截图（可选 focus=true 自动对准人物） ============
        private object Screenshot(string body)
        {
            Dictionary<string, object> req = Json.Parse(body) as Dictionary<string, object>;
            string file = req == null ? null : Str(req, "file", null);
            if (string.IsNullOrEmpty(file)) file = "ai_shot_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            file = SanitizeFileName(EnsurePng(file));
            bool focus = Bool(req ?? new Dictionary<string, object>(), "focus", false);
            string name = Str(req, "name", null);
            string framing = Str(req, "framing", "bust");   // full 全身 / bust 半身 / head 头部
            string view = Str(req, "view", "front");        // front 正面 / back 背面 / left 左 / right 右

            string dir = Path.Combine(GameRoot, "UserData/AICharBridge");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string abs = Path.Combine(dir, file);

            var tcs = new TaskCompletionSource<object>();
            _mainThread.Enqueue(delegate
            {
                try { StartCoroutine(ShotCoroutine(focus, name, framing, view, abs, tcs)); }
                catch (Exception e) { tcs.SetException(e); }
            });
            // 超时必须大于协程内的最长等待（等网格 10s + 稳定 1.5s + 渲染），否则会误报失败
            if (!tcs.Task.Wait(30000))
                throw new Exception("截图超时：游戏主线程 30 秒内未完成（可能正在加载或窗口最小化）");
            if (tcs.Task.IsFaulted) throw tcs.Task.Exception.InnerException ?? tcs.Task.Exception;
            if (File.Exists(abs))
            {
                AddActivity("√ 截图完成: " + file);
                var resp = Json.Obj("ok", true, "file", abs);
                if (tcs.Task.Result is Dictionary<string, object>) resp = (Dictionary<string, object>)tcs.Task.Result;
                resp["file"] = abs;
                return resp;
            }
            throw new Exception("截图未生成: " + abs);
        }

        private IEnumerator ShotCoroutine(bool focus, string name, string framing, string view, string abs, TaskCompletionSource<object> tcs)
        {
            Camera cam = Camera.main;
            if (cam == null) { tcs.SetException(new Exception("没有可用的主相机")); yield break; }

            Behaviour ctrlBehaviour = null;
            Light tempLight = null;
            Color oldAmbient = RenderSettings.ambientLight;
            float oldFov = cam.fieldOfView;
            var result = new Dictionary<string, object>();
            string focusNote = "";
            if (focus)
            {
                string tname;
                ChaControl target = FindStudioChar(name, out tname);
                if (target != null)
                {
                    // 这段没有 yield，可以整体 try/catch：失败时必须还原相机控制权/临时灯，
                    // 否则 tcs 不完成（调用方干等超时）+ 用户相机被留在脚本禁用状态
                    try
                    {
                        Studio.CameraControl ctrl = cam.GetComponent<Studio.CameraControl>();
                        if (ctrl != null) { ctrlBehaviour = ctrl; ctrlBehaviour.enabled = false; }
                        float dist, hgt;
                        FramingOf(framing, out dist, out hgt);
                        Vector3 vdir = ViewDir(target, view);
                        FrameCharacter(cam, target, dist, hgt, vdir);

                        // 空场景常没有灯，补一盏从正面打光，截完即删（不改动用户场景）
                        if (!HasDirectionalLight())
                        {
                            GameObject go = new GameObject("AICharBridgeTempLight");
                            tempLight = go.AddComponent<Light>();
                            tempLight.type = LightType.Directional;
                            tempLight.intensity = 1.15f;
                            go.transform.rotation = Quaternion.LookRotation((vdir + Vector3.down * 0.3f).normalized);
                            RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f);
                        }
                        AddActivity("√ 相机已对准 " + tname + "（" + view + " / " + framing + "）");
                        result["focus"] = tname;
                        result["framing"] = framing;
                        result["view"] = view;
                    }
                    catch (Exception e)
                    {
                        RestoreCamera(ctrlBehaviour, tempLight, oldAmbient, cam, oldFov);
                        tcs.SetException(e);
                        yield break;
                    }

                    // AddFemale 是异步加载，等角色网格真正就绪再截（最多 10 秒）
                    for (int wait = 0; wait < 20; wait++)
                    {
                        if (target.GetComponentsInChildren<SkinnedMeshRenderer>().Length >= 5) break;
                        yield return new WaitForSecondsRealtime(0.5f);
                    }
                    yield return new WaitForSecondsRealtime(1.5f); // 材质/贴图稳定
                }
                else
                {
                    focusNote = string.IsNullOrEmpty(name)
                        ? "场景里没有角色，按当前视角截图"
                        : "没有找到名字含「" + name + "」的角色，按当前视角截图";
                    AddActivity("! " + focusNote);
                    result["focus"] = null;
                }
            }
            if (!string.IsNullOrEmpty(focusNote)) result["note"] = focusNote;

            yield return new WaitForEndOfFrame();
            try
            {
                int w = cam.pixelWidth, h = cam.pixelHeight;
                Texture2D tex = new Texture2D(w, h, TextureFormat.RGB24, false);
                RenderTexture.active = cam.targetTexture;
                tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                tex.Apply();
                RenderTexture.active = null;
                byte[] png = tex.EncodeToPNG();
                Destroy(tex);
                File.WriteAllBytes(abs, png);
            }
            catch (Exception e)
            {
                RestoreCamera(ctrlBehaviour, tempLight, oldAmbient, cam, oldFov);
                tcs.SetException(e);
                yield break;
            }
            RestoreCamera(ctrlBehaviour, tempLight, oldAmbient, cam, oldFov);
            result["ok"] = true;
            tcs.TrySetResult(result);
        }

        private static void RestoreCamera(Behaviour ctrl, Light temp, Color oldAmbient, Camera cam, float oldFov)
        {
            if (cam != null) cam.fieldOfView = oldFov;   // 还原用户原来的视角
            if (ctrl != null) ctrl.enabled = true;   // 交还相机控制权，用户视角不会被留下改动
            if (temp != null)
            {
                RenderSettings.ambientLight = oldAmbient;
                Destroy(temp.gameObject);
            }
        }

        private static bool HasDirectionalLight()
        {
            Light[] lights = FindObjectsOfType<Light>();
            for (int i = 0; i < lights.Length; i++)
                if (lights[i] != null && lights[i].enabled && lights[i].type == LightType.Directional) return true;
            return false;
        }

        private static Vector3 FlattenedForward(ChaControl target)
        {
            Vector3 fwd = target.transform.forward;
            fwd.y = 0f;
            if (fwd.sqrMagnitude < 0.01f) fwd = Vector3.forward;
            return fwd.normalized;
        }

        // ================= 截图：自动取景 + 任意机位 + 多视角拼图 =================
        // 思路参考 dsh-blender-plugin：任意 from/look_at、按包围盒自动取景、
        // 一次调用出多视角拼图、近空帧自诊断并给出修正建议。

        private const float CaptureFov = 35f;   // 固定视角，取景与游戏内相机设置无关

        // 角色渲染包围盒（含头发/饰品；排除粒子与异常大的包围盒）
        // 有些特效/场景渲染器的 bounds 会异常大并把取景撑松，这里按"离角色距离 + 尺寸"过滤。
        private static int _lastBoundsUsed;

        // 头部骨骼位置：包围盒会被呆毛/长发/饰品撑高，取景要用骨骼定位才准
        private static bool HeadBonePos(ChaControl cc, out Vector3 pos)
        {
            pos = Vector3.zero;
            if (cc == null) return false;
            Transform[] ts = cc.GetComponentsInChildren<Transform>();
            for (int i = 0; i < ts.Length; i++)
            {
                if (ts[i] == null) continue;
                string n = ts[i].name;
                if (n == "cf_J_Head" || n == "cf_J_FaceBase" || n == "cf_J_HeadBase")
                {
                    pos = ts[i].position;
                    return true;
                }
            }
            return false;
        }

        private static bool CharacterBounds(ChaControl cc, out Bounds b)
        {
            b = new Bounds();
            if (cc == null) return false;
            Vector3 root = cc.transform.position;
            Renderer[] rs = cc.GetComponentsInChildren<Renderer>();
            bool has = false;
            Bounds acc = new Bounds();
            int used = 0;
            for (int i = 0; i < rs.Length; i++)
            {
                Renderer r = rs[i];
                if (r == null || !r.enabled || r is ParticleSystemRenderer) continue;
                Bounds rb = r.bounds;
                if (rb.size.magnitude > 3f) continue;                 // 明显异常的超大包围盒
                if ((rb.center - root).magnitude > 2f) continue;      // 离角色太远
                if (!has) { acc = rb; has = true; }
                else acc.Encapsulate(rb);
                used++;
            }
            _lastBoundsUsed = used;
            if (!has) return false;
            b = acc;
            return true;
        }

        // 按包围盒自动取景：不再靠猜距离，头/脚不会被裁掉
        private static void AutoFrame(ChaControl cc, string framing, out Vector3 focus, out float fitHeight, out float fitWidth)
        {
            string f = (framing ?? "full").ToLowerInvariant();
            Bounds b;
            Vector3 head;
            bool hasHead = HeadBonePos(cc, out head);

            // 头/脸/半身/躯干以"头部骨骼"为锚点：包围盒会被呆毛、长发、饰品撑高，
            // 用包围盒顶部定位会把镜头对到头上面去。
            if (hasHead && (f == "face" || f == "head" || f == "eyes" || f == "bust" || f == "torso"))
            {
                switch (f)
                {
                    case "eyes": focus = new Vector3(head.x, head.y + 0.03f, head.z); fitHeight = 0.13f; fitWidth = 0.17f; break;
                    case "face": focus = new Vector3(head.x, head.y + 0.015f, head.z); fitHeight = 0.26f; fitWidth = 0.22f; break;
                    case "head": focus = new Vector3(head.x, head.y + 0.05f, head.z); fitHeight = 0.46f; fitWidth = 0.39f; break;
                    case "bust": focus = new Vector3(head.x, head.y - 0.32f, head.z); fitHeight = 0.90f; fitWidth = 0.77f; break;
                    default: focus = new Vector3(head.x, head.y - 0.52f, head.z); fitHeight = 0.70f; fitWidth = 0.60f; break;
                }
                return;
            }

            if (cc != null && CharacterBounds(cc, out b))
            {
                float top = b.max.y;
                switch (f)
                {
                    case "eyes":
                    case "face":
                    case "head": focus = new Vector3(b.center.x, top - 0.10f, b.center.z); fitHeight = 0.40f; fitWidth = 0.34f; break;
                    case "torso": focus = new Vector3(b.center.x, b.center.y + b.size.y * 0.12f, b.center.z); fitHeight = 0.72f; fitWidth = 0.62f; break;
                    case "bust": focus = new Vector3(b.center.x, top - 0.34f, b.center.z); fitHeight = 0.95f; fitWidth = 0.81f; break;
                    default:
                        focus = b.center;
                        fitHeight = Mathf.Max(b.size.y * 1.08f, 1.2f);
                        fitWidth = Mathf.Max(b.size.x * 1.08f, 0.6f);
                        break;
                }
                return;
            }
            focus = (cc != null ? cc.transform.position : Vector3.zero) + new Vector3(0f, 1.0f, 0f);
            fitHeight = f == "eyes" ? 0.14f : (f == "face" ? 0.28f : (f == "head" ? 0.45f : (f == "bust" ? 1.0f : 1.8f)));
            fitWidth = fitHeight * 0.85f;
        }

        private static float DistanceForFit(float fitHeight)
        {
            return (fitHeight * 0.5f) / Mathf.Tan(CaptureFov * 0.5f * Mathf.Deg2Rad) + 0.25f;
        }

        // "x,y,z" -> Vector3（任意机位用）
        private static bool ParseVec3(string s, out Vector3 v)
        {
            v = Vector3.zero;
            if (string.IsNullOrEmpty(s)) return false;
            string[] p = s.Split(',');
            if (p.Length < 3) return false;
            float x, y, z;
            if (!float.TryParse(p[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
            if (!float.TryParse(p[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out y)) return false;
            if (!float.TryParse(p[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
            v = new Vector3(x, y, z);
            return true;
        }

        private static Vector3 ResolveViewDir(ChaControl cc, string view)
        {
            if (cc == null) return Vector3.forward;
            return ViewDir(cc, view);
        }

        // 背景色取画面四边的众数；返回非背景像素占比（近 0 = 画面基本是空的）
        private static float Coverage(Color32[] px, int w, int h)
        {
            if (px == null || px.Length == 0 || w < 1 || h < 1) return 0f;
            var counts = new Dictionary<int, int>();
            int bg = 0, best = -1;
            for (int x = 0; x < w; x++)
            {
                for (int k = 0; k < 2; k++)
                {
                    int idx = k == 0 ? x : (h - 1) * w + x;
                    int packed = (px[idx].r << 16) | (px[idx].g << 8) | px[idx].b;
                    int c;
                    counts.TryGetValue(packed, out c);
                    counts[packed] = c + 1;
                    if (counts[packed] > best) { best = counts[packed]; bg = packed; }
                }
            }
            for (int y = 0; y < h; y++)
            {
                for (int k = 0; k < 2; k++)
                {
                    int idx = k == 0 ? y * w : y * w + w - 1;
                    int packed = (px[idx].r << 16) | (px[idx].g << 8) | px[idx].b;
                    int c;
                    counts.TryGetValue(packed, out c);
                    counts[packed] = c + 1;
                    if (counts[packed] > best) { best = counts[packed]; bg = packed; }
                }
            }
            byte br = (byte)((bg >> 16) & 255), bgg = (byte)((bg >> 8) & 255), bb = (byte)(bg & 255);
            int diff = 0;
            for (int i = 0; i < px.Length; i++)
                if (Math.Abs(px[i].r - br) + Math.Abs(px[i].g - bgg) + Math.Abs(px[i].b - bb) > 40) diff++;
            return diff / (float)px.Length;
        }

        // 统一的机位计算：显式 from/look 优先，否则按视角名绕角色取景
        private static void CameraPose(Camera cam, bool explicitView, ChaControl target, string view,
            Vector3 focusPt, float dist, Vector3 from, Vector3 look)
        {
            if (explicitView)
            {
                cam.transform.position = from;
                cam.transform.LookAt(look);
                return;
            }
            Vector3 dir = ResolveViewDir(target, view);
            cam.transform.position = focusPt + dir * dist + Vector3.up * 0.02f;
            cam.transform.LookAt(focusPt);
        }

        // 画面粗指纹：按 32 格采样，抗微小抖动（物理头发），用于判断"画面是否已稳定"
        private static string PanelSignature(Color32[] px, int w, int h)
        {
            if (px == null || px.Length == 0) return "empty";
            int stepX = Mathf.Max(1, w / 32), stepY = Mathf.Max(1, h / 32);
            var sb = new StringBuilder(4096);
            for (int y = 0; y < h; y += stepY)
                for (int x = 0; x < w; x += stepX)
                {
                    Color32 c = px[y * w + x];
                    sb.Append((char)(48 + (c.r >> 4))).Append((char)(48 + (c.g >> 4))).Append((char)(48 + (c.b >> 4)));
                }
            return Md5(Encoding.UTF8.GetBytes(sb.ToString()));
        }

        private static string Md5(byte[] data)
        {
            using (var md5 = System.Security.Cryptography.MD5.Create())
            {
                byte[] h = md5.ComputeHash(data);
                var sb = new StringBuilder(h.Length * 2);
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        private static string Sha256File(string path)
        {
            try
            {
                using (var sha = System.Security.Cryptography.SHA256.Create())
                using (FileStream fs = File.OpenRead(path))
                {
                    byte[] h = sha.ComputeHash(fs);
                    var sb = new StringBuilder(h.Length * 2);
                    foreach (byte b in h) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch (Exception e) { return "n/a (" + e.Message + ")"; }
        }

        // ============ /capture：自动取景 + 多视角拼成一张图 ============
        private object Capture(string body)
        {
            Dictionary<string, object> req = Json.Parse(body) as Dictionary<string, object>;
            if (req == null) req = new Dictionary<string, object>();
            string name = Str(req, "name", null);
            string framing = Str(req, "framing", "full");
            string file = Str(req, "file", null);
            if (string.IsNullOrEmpty(file)) file = "cap_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            file = SanitizeFileName(EnsurePng(file));
            int panelH = 480;   // 每格高度（像素），宽度按角色宽高比自动定
            object pwo;
            if (req.TryGetValue("panel_width", out pwo) && pwo is double) panelH = Mathf.Clamp((int)(double)pwo, 240, 1080);
            bool focus = Bool(req, "focus", true);

            var views = new List<string>();
            object vo;
            if (req.TryGetValue("views", out vo) && vo is List<object>)
                foreach (object o in (List<object>)vo) if (o is string) views.Add((string)o);
            if (views.Count == 0) views.Add(Str(req, "view", "front"));

            string dir = Path.Combine(GameRoot, "UserData/AICharBridge");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string abs = Path.Combine(dir, file);

            var tcs = new TaskCompletionSource<object>();
            _mainThread.Enqueue(delegate
            {
                try { StartCoroutine(CaptureCoroutine(req, views.ToArray(), framing, name, panelH, abs, focus, tcs)); }
                catch (Exception e) { tcs.SetException(e); }
            });
            if (!tcs.Task.Wait(60000)) throw new Exception("截图超时：主线程 60 秒未完成（可能在加载）");
            if (tcs.Task.IsFaulted) throw tcs.Task.Exception.InnerException ?? tcs.Task.Exception;
            return tcs.Task.Result;
        }

        private IEnumerator CaptureCoroutine(Dictionary<string, object> req, string[] views, string framing,
            string name, int panelH, string abs, bool focus, TaskCompletionSource<object> tcs)
        {
            Camera mainCam = Camera.main;
            if (mainCam == null) { tcs.SetException(new Exception("没有可用的主相机")); yield break; }

            var result = new Dictionary<string, object>();
            var warnings = new List<object>();
            var panels = new List<object>();
            Light temp = null;
            Color oldAmbient = RenderSettings.ambientLight;
            GameObject camGo = null;
            RenderTexture rt = null;
            int ss = 2;

            ChaControl target = null;
            string tname = "";
            if (focus)
            {
                target = FindStudioChar(name, out tname);
                if (target != null) result["focus"] = tname;
                else warnings.Add(Json.Obj("kind", "no_subject",
                    "detail", string.IsNullOrEmpty(name) ? "场景里没有角色" : "没有名字含「" + name + "」的角色"));
            }

            if (target != null)
            {
                // 先补齐穿着（见 EnsureDressed）：/generate 的"恢复穿着"是异步的，
                // 紧跟其后的截图会拍到裤袜/饰品还没挂上的中间态。
                bool wantDress = !req.ContainsKey("dress") || Bool(req, "dress", true);
                bool dressed = false;
                try { dressed = wantDress && EnsureDressed(target); }
                catch (Exception e) { tcs.SetException(e); yield break; }
                if (dressed)
                {
                    AddActivity("拍照前补齐穿着状态");
                    yield return new WaitForSecondsRealtime(0.6f);
                }
                for (int i = 0; i < 20; i++)
                {
                    if (target.GetComponentsInChildren<SkinnedMeshRenderer>().Length >= 5) break;
                    yield return new WaitForSecondsRealtime(0.5f);
                }
            }

            // 任意机位（from/look_at 世界坐标）优先；否则按包围盒自动取景
            // 这段没有 yield，整体包 try/catch：失败让 tcs 落到 500，而不是让调用方等 60 秒超时
            Vector3 focusPt; float fitHeight, fitWidth; float dist;
            float aspect;
            bool explicitView = false;
            Vector3 from, look;
            try
            {
                bool hasFrom = ParseVec3(Str(req, "from", null), out from);
                bool hasLook = ParseVec3(Str(req, "look_at", null), out look);
                if (hasFrom && hasLook)
                {
                    focusPt = look; fitHeight = 0f; fitWidth = 0f; dist = 0f; aspect = 1f; explicitView = true;
                    result["spec"] = Json.Obj("from", from.ToString(), "look_at", look.ToString());
                }
                else
                {
                    AutoFrame(target, framing, out focusPt, out fitHeight, out fitWidth);
                    aspect = Mathf.Clamp(fitWidth / Mathf.Max(fitHeight, 0.01f), 0.6f, 1.6f);
                    float halfTan = Mathf.Tan(CaptureFov * 0.5f * Mathf.Deg2Rad);
                    float distV = (fitHeight * 0.5f) / halfTan;
                    float distH = (fitWidth * 0.5f) / (halfTan * aspect);
                    dist = Mathf.Max(distV, distH) + 0.25f;
                    result["framing"] = Json.Obj("mode", framing, "fit_height", (double)fitHeight,
                        "fit_width", (double)fitWidth, "aspect", (double)aspect, "distance", (double)dist);
                    Bounds bb;
                    if (target != null && CharacterBounds(target, out bb))
                        result["subject_bounds"] = Json.Obj(
                            "size", Json.Obj("x", (double)bb.size.x, "y", (double)bb.size.y, "z", (double)bb.size.z),
                            "center", Json.Obj("x", (double)bb.center.x, "y", (double)bb.center.y, "z", (double)bb.center.z),
                            "renderers_used", _lastBoundsUsed);
                }

                // 空场景补光（截完即删，不改动用户场景）
                if (target != null && !HasDirectionalLight())
                {
                    GameObject go = new GameObject("AICharBridgeTempLight");
                    temp = go.AddComponent<Light>();
                    temp.type = LightType.Directional;
                    temp.intensity = 1.15f;
                    Vector3 ldir = explicitView ? (look - from) : FlattenedForward(target);
                    ldir.y = 0f;
                    if (ldir.sqrMagnitude < 0.01f) ldir = Vector3.forward;
                    go.transform.rotation = Quaternion.LookRotation((ldir.normalized + Vector3.down * 0.3f).normalized);
                    RenderSettings.ambientLight = new Color(0.55f, 0.55f, 0.55f);
                }
            }
            catch (Exception e) { tcs.SetException(e); yield break; }

            // 离屏渲染：临时相机 + RenderTexture。不占用户视口、不带游戏 UI（UI 走画布/IMGUI，不进 RT）
            int ph = Mathf.Clamp(panelH, 240, 1080);
            int pw = Mathf.Clamp(Mathf.RoundToInt(ph * aspect), 240, 1200);
            Texture2D sheet = null;
            Color32[] sheetPx = null;
            string prevMd5 = null;

            // 建临时相机与 RenderTexture（出错就中止；这段不含 yield，可安全包在 try 里）
            Camera shot = null;
            try
            {
                camGo = new GameObject("AICharBridgeShotCam");
                shot = camGo.AddComponent<Camera>();
                shot.CopyFrom(mainCam);
                shot.cullingMask = mainCam.cullingMask & ~(1 << 5);   // 去掉 UI 层
                shot.clearFlags = CameraClearFlags.SolidColor;
                shot.backgroundColor = new Color(0.09f, 0.11f, 0.16f);
                shot.fieldOfView = CaptureFov;
                shot.aspect = pw / (float)ph;
                shot.enabled = false;
                double? ssReq = Num(req, "ss");
                ss = ssReq.HasValue ? Mathf.Clamp((int)ssReq.Value, 1, 3) : 2;   // 默认 2 倍超采样
                rt = MakeRT(pw * ss, ph * ss);
                result["supersample"] = ss;
                shot.targetTexture = rt;
                sheet = new Texture2D(pw * views.Length, ph, TextureFormat.RGB24, false);
                sheetPx = new Color32[pw * views.Length * ph];
            }
            catch (Exception e)
            {
                if (rt != null) { rt.Release(); Destroy(rt); }
                if (camGo != null) Destroy(camGo);
                if (temp != null) { RenderSettings.ambientLight = oldAmbient; Destroy(temp.gameObject); }
                tcs.SetException(e);
                yield break;
            }

            // 等画面稳定：服装/头发是异步加载的，加载中拍出来会缺件。
            // 连续两次粗指纹一致才算稳定（借鉴 dsh-blender-plugin 的帧哈希思路）。
            // 注意：这段有 yield，不能整体放 try/catch（C# 不允许 try 内 yield 出去），
            // 所以把每轮不含 yield 的探测体单独包起来 —— 否则这里抛异常 tcs 永远不完成
            // （调用方干等 60 秒还拿到误导性的"超时"），且 rt/临时相机/补光全部泄漏。
            string prevSig = null;
            bool settled = false;
            for (int attempt = 0; attempt < 14 && !settled; attempt++)
            {
                Color32[] sigPx = null;
                bool failed = false;
                try
                {
                    CameraPose(shot, explicitView, target, views[0], focusPt, dist, from, look);
                    shot.Render();
                    sigPx = ReadRT(rt, null, pw * ss, ph * ss);
                    if (ss > 1) sigPx = Downsample(sigPx, pw * ss, ph * ss, pw, ph);
                }
                catch (Exception e)
                {
                    failed = true;
                    if (rt != null) { rt.Release(); Destroy(rt); }
                    if (camGo != null) Destroy(camGo);
                    if (temp != null) { RenderSettings.ambientLight = oldAmbient; Destroy(temp.gameObject); }
                    tcs.SetException(e);
                }
                if (failed) yield break;
                string sigNow = PanelSignature(sigPx, pw, ph);
                if (prevSig == sigNow) { settled = true; break; }
                prevSig = sigNow;
                yield return new WaitForSecondsRealtime(0.35f);
            }
            result["settled"] = settled;

            try
            {
                for (int vi = 0; vi < views.Length; vi++)
                {
                    CameraPose(shot, explicitView, target, views[vi], focusPt, dist, from, look);
                    shot.Render();                                  // 同步渲染，不必等帧末

                    Color32[] src = ReadRT(rt, null, pw * ss, ph * ss);
                    if (ss > 1) src = Downsample(src, pw * ss, ph * ss, pw, ph);
                    float coverage = Coverage(src, pw, ph);
                    string md5 = PanelSignature(src, pw, ph);

                    int x0 = vi * pw;
                    int rowLen = pw * views.Length;
                    for (int y = 0; y < ph; y++)
                        for (int x = 0; x < pw; x++)
                            sheetPx[y * rowLen + x0 + x] = src[y * pw + x];

                    var pj = Json.Obj("view", explicitView ? "custom" : views[vi], "coverage", (double)coverage, "md5", md5);
                    if (prevMd5 != null && prevMd5 == md5) pj["identical_to_previous"] = true;
                    prevMd5 = md5;
                    panels.Add(pj);
                    if (coverage < 0.01f)
                        warnings.Add(Json.Obj("kind", "frame_looks_empty", "view", views[vi], "coverage", (double)coverage,
                            "suggest", "角色可能不在画面内：framing 用 full，或核对 name；也可直接给 from/look_at 坐标"));
                }
            }
            catch (Exception e)
            {
                if (rt != null) { rt.Release(); Destroy(rt); }
                if (camGo != null) Destroy(camGo);
                if (temp != null) { RenderSettings.ambientLight = oldAmbient; Destroy(temp.gameObject); }
                tcs.SetException(e);
                yield break;
            }

            sheet.SetPixels32(sheetPx);
            sheet.Apply();
            byte[] outPng = sheet.EncodeToPNG();
            Destroy(sheet);
            File.WriteAllBytes(abs, outPng);

            if (rt != null) { rt.Release(); Destroy(rt); }
            if (camGo != null) Destroy(camGo);
            if (temp != null) { RenderSettings.ambientLight = oldAmbient; Destroy(temp.gameObject); }

            result["ok"] = true;
            result["file"] = abs;
            result["panels"] = panels;
            result["md5"] = Md5(outPng);
            result["panel_size"] = Json.Obj("w", pw, "h", ph, "offscreen", true);
            if (warnings.Count > 0) result["warnings"] = warnings;
            AddActivity("√ 截图 " + Path.GetFileName(abs) + "（" + views.Length + " 视角，离屏渲染" +
                (warnings.Count > 0 ? "，" + warnings.Count + " 条警告" : "") + "）");
            tcs.TrySetResult(result);
        }

        private static readonly string[] FaceShapeNames = new string[] {
            "FaceBaseW","FaceUpZ","FaceUpY","FaceUpSize","FaceLowZ","FaceLowW","ChinLowY","ChinLowZ",
            "ChinY","ChinW","ChinZ","ChinTipY","ChinTipZ","ChinTipW","CheekBoneW","CheekBoneZ",
            "CheekW","CheekZ","CheekY","EyebrowY","EyebrowX","EyebrowRotZ","EyebrowInForm","EyebrowOutForm",
            "EyelidsUpForm1","EyelidsUpForm2","EyelidsUpForm3","EyelidsLowForm1","EyelidsLowForm2","EyelidsLowForm3",
            "EyeY","EyeX","EyeZ","EyeTilt","EyeH","EyeW","EyeInX","EyeOutY",
            "NoseTipH","NoseY","NoseBridgeH","MouthY","MouthW","MouthZ","MouthUpForm","MouthLowForm",
            "MouthCornerForm","EarSize","EarRotY","EarRotZ","EarUpForm","EarLowForm" };

        // 读一个 float[] 成员（属性/字段都试，反射优先）。
        // 为什么不能直接写 fc.shapeValueFace：实测在这条路径上它取到的是 null，
        // 而反射读得到 —— 于是 /inspect 的 face_shape/body_shape 一直是空的。
        // 后果就是我只能"猜"滑条值（还猜错了：把原版卡普遍为 0 的 ChinTipW 推到 0.5）。
        private static float[] ReadFloatArray(object obj, string name)
        {
            if (obj == null) return null;
            Type t = obj.GetType();
            const BindingFlags F = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            try
            {
                PropertyInfo p = t.GetProperty(name, F);
                if (p != null && p.CanRead && p.GetIndexParameters().Length == 0)
                {
                    float[] v = p.GetValue(obj, null) as float[];
                    if (v != null) return v;
                }
            }
            catch (Exception) { }
            try
            {
                FieldInfo f = t.GetField(name, F);
                if (f != null)
                {
                    float[] v = f.GetValue(obj) as float[];
                    if (v != null) return v;
                }
            }
            catch (Exception) { }
            return null;
        }

        private static object FaceShapes(ChaFileFace fc)
        {
            var d = new Dictionary<string, object>();
            float[] v = ReadFloatArray(fc, "shapeValueFace");
            if (v == null) return d;
            for (int i = 0; i < v.Length && i < FaceShapeNames.Length; i++)
                d[FaceShapeNames[i]] = (double)v[i];
            return d;
        }

        // 身体滑条索引（对应 ChaFileDefine.BodyShapeIdx）
        private static readonly string[] BodyShapeNames = new string[] {
            "Height", "HeadSize", "NeckW", "NeckZ", "BustSize", "BustY", "BustRotX", "BustX",
            "BustRotY", "BustSharp", "BustForm", "AreolaBulge", "NipWeight", "NipStand",
            "BodyShoulderW", "BodyShoulderZ", "BodyUpW", "BodyUpZ", "BodyLowW", "BodyLowZ",
            "WaistY", "Belly", "WaistUpW", "WaistUpZ", "WaistLowW", "WaistLowZ",
            "Hip", "HipRotX", "ThighUpW", "ThighUpZ", "ThighLowW", "ThighLowZ",
            "KneeLowW", "KneeLowZ", "Calf", "AnkleW", "AnkleZ",
            "ShoulderW", "ShoulderZ", "ArmUpW", "ArmUpZ", "ElbowW", "ElbowZ", "ArmLow" };

        private static int BodyShapeIndex(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            for (int i = 0; i < BodyShapeNames.Length; i++)
                if (string.Equals(BodyShapeNames[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        // 容易把身体顶出衣服的滑条（体型越极端越容易穿模）
        // 腿也在内：裤袜/袜子是独立网格，不跟随极端腿型；基座卡把 ThighLowW 压到 0.078、
        // Calf 0.180 时，大腿/膝盖正面会从袜子里顶出来（实测可见斑点状穿模）。
        private static readonly string[] ClipProneShapes = new string[] {
            "BustSize", "BustX", "BustY", "BustForm", "BustSharp", "BodyUpW", "BodyUpZ",
            "WaistUpW", "WaistLowW", "Hip", "ThighUpW", "ThighUpZ", "ArmUpW", "ShoulderW", "BodyShoulderW",
            "ThighLowW", "ThighLowZ", "KneeLowW", "KneeLowZ", "Calf", "AnkleW", "AnkleZ",
            "BodyLowW", "BodyLowZ", "WaistLowZ", "Belly" };

        private static bool IsClipProne(string name)
        {
            for (int i = 0; i < ClipProneShapes.Length; i++)
                if (string.Equals(ClipProneShapes[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static readonly string[] LegShapes = new string[] {
            "ThighUpW", "ThighUpZ", "ThighLowW", "ThighLowZ",
            "KneeLowW", "KneeLowZ", "Calf", "AnkleW", "AnkleZ" };

        private static bool IsLegShape(string name)
        {
            for (int i = 0; i < LegShapes.Length; i++)
                if (string.Equals(LegShapes[i], name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ================= 参考图对标：图像测量 + 打分 =================
        // 对照 dsh-blender-plugin 的 img_scan / qc_compare：把"像不像"变成可计算、可留痕的数字。

        private const int NormW = 96, NormH = 192;      // 归一化画布（比对剪影用）
        private static readonly Color ShotBg = new Color32(23, 28, 41, 255);   // 离屏渲染底色
        private const byte ShotBgR = 23, ShotBgG = 28, ShotBgB = 41;

        private class Img
        {
            public int W, H;
            public Color32[] Px;
            public bool[] Mask;
            public int X0, Y0, X1, Y1;
            public float Coverage;
        }

        // 四边众数色（参考图背景通常是纯白/纯色）
        private static int BorderMode(Color32[] px, int w, int h)
        {
            var counts = new Dictionary<int, int>();
            int bg = 0, best = -1;
            for (int x = 0; x < w; x++)
                for (int k = 0; k < 2; k++)
                {
                    int idx = k == 0 ? x : (h - 1) * w + x;
                    int pk = (px[idx].r << 16) | (px[idx].g << 8) | px[idx].b;
                    int c; counts.TryGetValue(pk, out c); counts[pk] = c + 1;
                    if (counts[pk] > best) { best = counts[pk]; bg = pk; }
                }
            for (int y = 0; y < h; y++)
                for (int k = 0; k < 2; k++)
                {
                    int idx = k == 0 ? y * w : y * w + w - 1;
                    int pk = (px[idx].r << 16) | (px[idx].g << 8) | px[idx].b;
                    int c; counts.TryGetValue(pk, out c); counts[pk] = c + 1;
                    if (counts[pk] > best) { best = counts[pk]; bg = pk; }
                }
            return bg;
        }

        // 剪影：离屏渲染用"与已知底色精确比对"（边缘更干净），参考图用四边众数 + L1 阈值
        private static bool[] Silhouette(Color32[] px, int w, int h, bool exactBg,
            out int x0, out int y0, out int x1, out int y1, out float coverage)
        {
            byte br, bgc, bb; int l1;
            if (exactBg) { br = ShotBgR; bgc = ShotBgG; bb = ShotBgB; l1 = 0; }
            else
            {
                int bg = BorderMode(px, w, h);
                br = (byte)((bg >> 16) & 255); bgc = (byte)((bg >> 8) & 255); bb = (byte)(bg & 255); l1 = 60;
            }
            bool[] m = new bool[px.Length];
            int minX = w, minY = h, maxX = -1, maxY = -1, count = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    Color32 c = px[y * w + x];
                    int d = Math.Abs(c.r - br) + Math.Abs(c.g - bgc) + Math.Abs(c.b - bb);
                    bool fg = exactBg ? (d > 12) : (d > l1);
                    if (fg)
                    {
                        m[y * w + x] = true; count++;
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            x0 = minX; y0 = minY; x1 = maxX; y1 = maxY;
            coverage = px.Length == 0 ? 0f : count / (float)px.Length;
            return m;
        }

        // 从四边向内容差洪泛：能到达的算背景。被深色线稿围住的白色区域（白围裙/白衣）
        // 不会被误判成背景——这是纯色阈值做不到的。
        private static bool[] SilhouetteFlood(Color32[] px, int w, int h)
        {
            int bg = BorderMode(px, w, h);
            byte br = (byte)((bg >> 16) & 255), bgc = (byte)((bg >> 8) & 255), bb = (byte)(bg & 255);
            bool[] isBg = new bool[px.Length];
            var stack = new Stack<int>();
            for (int x = 0; x < w; x++)
            {
                for (int k = 0; k < 2; k++)
                {
                    int idx = k == 0 ? x : (h - 1) * w + x;
                    if (!isBg[idx])
                    {
                        Color32 c = px[idx];
                        if (Math.Abs(c.r - br) <= 30 && Math.Abs(c.g - bgc) <= 30 && Math.Abs(c.b - bb) <= 30)
                        { isBg[idx] = true; stack.Push(idx); }
                    }
                }
            }
            for (int y = 0; y < h; y++)
            {
                for (int k = 0; k < 2; k++)
                {
                    int idx = k == 0 ? y * w : y * w + w - 1;
                    if (!isBg[idx])
                    {
                        Color32 c = px[idx];
                        if (Math.Abs(c.r - br) <= 30 && Math.Abs(c.g - bgc) <= 30 && Math.Abs(c.b - bb) <= 30)
                        { isBg[idx] = true; stack.Push(idx); }
                    }
                }
            }
            while (stack.Count > 0)
            {
                int idx = stack.Pop();
                int x = idx % w, y = idx / w;
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + (k == 0 ? 1 : (k == 1 ? -1 : 0));
                    int ny = y + (k == 2 ? 1 : (k == 3 ? -1 : 0));
                    if (nx < 0 || ny < 0 || nx >= w || ny >= h) continue;
                    int ni = ny * w + nx;
                    if (isBg[ni]) continue;
                    Color32 c = px[ni];
                    if (Math.Abs(c.r - br) <= 30 && Math.Abs(c.g - bgc) <= 30 && Math.Abs(c.b - bb) <= 30)
                    { isBg[ni] = true; stack.Push(ni); }
                }
            }
            bool[] mask = new bool[px.Length];
            for (int i = 0; i < px.Length; i++) mask[i] = !isBg[i];
            return mask;
        }

        private static Img Wrap(Color32[] px, int w, int h, bool exactBg)
        {
            Img im = new Img { W = w, H = h, Px = px };
            int x0, y0, x1, y1;
            float cov;
            if (exactBg) im.Mask = Silhouette(px, w, h, true, out x0, out y0, out x1, out y1, out cov);
            else im.Mask = SilhouetteFlood(px, w, h);
            BBoxOf(im, out x0, out y0, out x1, out y1, out cov);
            im.X0 = x0; im.Y0 = y0; im.X1 = x1; im.Y1 = y1; im.Coverage = cov;
            return im;
        }

        private static void BBoxOf(Img im, out int x0, out int y0, out int x1, out int y1, out float coverage)
        {
            x0 = im.W; y0 = im.H; x1 = -1; y1 = -1;
            int count = 0;
            for (int y = 0; y < im.H; y++)
                for (int x = 0; x < im.W; x++)
                    if (im.Mask[y * im.W + x])
                    {
                        count++;
                        if (x < x0) x0 = x;
                        if (x > x1) x1 = x;
                        if (y < y0) y0 = y;
                        if (y > y1) y1 = y;
                    }
            coverage = im.Mask.Length == 0 ? 0f : count / (float)im.Mask.Length;
        }

        private static Img LoadImg(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            Texture2D tex = new Texture2D(2, 2, TextureFormat.RGB24, false);
            bool ok;
            try { ok = ImageConversion.LoadImage(tex, bytes); }
            catch (Exception) { ok = false; }
            if (!ok)
            {
                Destroy(tex);
                tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(tex, bytes)) { Destroy(tex); throw new Exception("图片解码失败: " + path); }
            }
            int w = tex.width, h = tex.height;
            Color32[] px = tex.GetPixels32();
            Destroy(tex);
            return Wrap(px, w, h, false);
        }

        private static Texture2D ToTexture(Color32[] px, int w, int h)
        {
            Texture2D t = new Texture2D(w, h, TextureFormat.RGB24, false);
            t.SetPixels32(px);
            t.Apply();
            return t;
        }

        private static Color32[] Crop(Color32[] src, int sw, int x0, int y0, int cw, int ch)
        {
            Color32[] outPx = new Color32[cw * ch];
            for (int y = 0; y < ch; y++)
                for (int x = 0; x < cw; x++)
                    outPx[y * cw + x] = src[(y0 + y) * sw + (x0 + x)];
            return outPx;
        }

        // 归一化剪影：按包围盒等比缩放，底部对齐 + 水平居中（人物比对用底部对齐更稳）
        private static bool[] Normalize(Img im)
        {
            bool[] n = new bool[NormW * NormH];
            int bw = im.X1 - im.X0 + 1, bh = im.Y1 - im.Y0 + 1;
            if (bw <= 0 || bh <= 0) return n;
            float scale = Mathf.Min(NormW / (float)bw, NormH / (float)bh);
            int nw = Mathf.Max(1, Mathf.RoundToInt(bw * scale));
            int nh = Mathf.Max(1, Mathf.RoundToInt(bh * scale));
            int offX = (NormW - nw) / 2;
            int offY = NormH - nh;                       // 底对齐
            for (int y = 0; y < nh; y++)
            {
                int sy = im.Y0 + Mathf.Min(bh - 1, Mathf.RoundToInt(y / scale));
                for (int x = 0; x < nw; x++)
                {
                    int sx = im.X0 + Mathf.Min(bw - 1, Mathf.RoundToInt(x / scale));
                    if (im.Mask[sy * im.W + sx]) n[(offY + y) * NormW + offX + x] = true;
                }
            }
            return n;
        }

        private static float Iou(bool[] a, bool[] b, float coreFrac)
        {
            int x0 = coreFrac > 0f ? Mathf.RoundToInt(NormW * (0.5f - coreFrac * 0.5f)) : 0;
            int x1 = coreFrac > 0f ? Mathf.RoundToInt(NormW * (0.5f + coreFrac * 0.5f)) : NormW;
            int inter = 0, uni = 0;
            for (int y = 0; y < NormH; y++)
                for (int x = x0; x < x1; x++)
                {
                    int i = y * NormW + x;
                    if (a[i] && b[i]) inter++;
                    if (a[i] || b[i]) uni++;
                }
            return uni == 0 ? 0f : inter / (float)uni;
        }

        // 前景颜色的 4bit/通道直方图（64 桶）
        private static int[] Hist(Img im)
        {
            int[] h = new int[64];
            for (int i = 0; i < im.Mask.Length; i++)
                if (im.Mask[i])
                {
                    Color32 c = im.Px[i];
                    h[((c.r >> 6) << 4) | ((c.g >> 6) << 2) | (c.b >> 6)]++;
                }
            return h;
        }

        private static float HistSim(int[] a, int[] b)
        {
            int sa = 0, sb = 0, d = 0;
            for (int i = 0; i < a.Length; i++) { sa += a[i]; sb += b[i]; d += Math.Abs(a[i] - b[i]); }
            if (sa == 0 || sb == 0) return 0f;
            return Mathf.Clamp01(1f - d / (float)(sa + sb));
        }

        // 分带主色（按剪影包围盒竖直三等分），用于"发色/上衣色/下装色"这类分区比对
        private static int[] BandColors(Img im, int bands)
        {
            int[] res = new int[bands];
            int bh = im.Y1 - im.Y0 + 1;
            if (bh <= 0) return res;
                for (int b = 0; b < bands; b++)
                {
                    // 行序自下而上（见 CropRegion 的说明）：band 0 要对应"最上面"的带（头），
                    // 所以从 im.Y1 往下取，而不是从 im.Y0 往上取。
                    int hi = im.Y1 - bh * b / bands;
                    int lo = im.Y1 - bh * (b + 1) / bands + 1;
                    if (lo < im.Y0) lo = im.Y0;
                    if (hi > im.Y1) hi = im.Y1;
                    var counts = new Dictionary<int, int>();
                    int bestC = -1, bestN = 0;
                    for (int y = lo; y <= hi; y++)
                        for (int x = im.X0; x <= im.X1; x++)
                        {
                            int i = y * im.W + x;
                            if (i < 0 || i >= im.Mask.Length || !im.Mask[i]) continue;
                            Color32 c = im.Px[i];
                            int q = ((c.r >> 5) << 10) | ((c.g >> 5) << 5) | (c.b >> 5);
                            int n; counts.TryGetValue(q, out n); counts[q] = n + 1;
                            if (counts[q] > bestN)
                            {
                                bestN = counts[q];
                                bestC = (((q >> 10) & 31) << 3) | 4;
                                int gg = (((q >> 5) & 31) << 3) | 4, bb2 = ((q & 31) << 3) | 4;
                                bestC = (bestC << 16) | (gg << 8) | bb2;
                            }
                        }
                    res[b] = bestC;
                }
            return res;
        }

        // 搜索打分：权重可由请求的 weights 覆盖（搜脸部细节时把 appearance 调高）
        private static float FitTotal(Dictionary<string, object> w, float iouCore, float iouFull, float hist, float band, float app)
        {
            float wc = 0.35f, wf = 0.10f, wh = 0.15f, wb = 0.25f, wa = 0.15f;
            if (w != null)
            {
                double? v;
                v = Num(w, "iou_core"); if (v.HasValue) wc = (float)v.Value;
                v = Num(w, "iou_full"); if (v.HasValue) wf = (float)v.Value;
                v = Num(w, "color_hist"); if (v.HasValue) wh = (float)v.Value;
                v = Num(w, "band_color"); if (v.HasValue) wb = (float)v.Value;
                v = Num(w, "appearance"); if (v.HasValue) wa = (float)v.Value;
            }
            return wc * iouCore + wf * iouFull + wh * hist + wb * band + wa * app;
        }

        // 外观相似度：按剪影包围盒对齐、缩放到同一画布后比灰度。
        // 剪影 IoU / 颜色直方图测不出"眼型"这类细节（眼睛只占脸几个百分点），
        // 搜脸部细节要用这一项：framing=face + ref_crop=head + weights.appearance 调高。
        private static float AppSim(Img a, Img b)
        {
            const int N = 64;
            float[][] ca = ColorBox(a, N), cb = ColorBox(b, N);
            if (ca == null || cb == null) return 0f;
            float sum = 0f;
            int used = 0;
            for (int i = 0; i < ca.Length; i++)
            {
                bool fa = ca[i][3] > 0.5f, fb = cb[i][3] > 0.5f;
                if (!fa && !fb) continue;   // 双方都是背景的格子不参与（否则背景差异会把分差压平）
                used++;
                sum += (Math.Abs(ca[i][0] - cb[i][0]) + Math.Abs(ca[i][1] - cb[i][1]) + Math.Abs(ca[i][2] - cb[i][2])) / 3f;
            }
            if (used == 0) return 0f;
            return Mathf.Clamp01(1f - (sum / used) / 255f);
        }

        // 每格返回 [r,g,b,前景占比]：块内平均（不是点采样，避免噪声），前景色只统计 mask 内的像素
        private static float[][] ColorBox(Img im, int n)
        {
            int bw = im.X1 - im.X0 + 1, bh = im.Y1 - im.Y0 + 1;
            if (bw <= 0 || bh <= 0 || im.Mask == null || im.Px == null) return null;
            var cells = new float[n * n][];
            for (int y = 0; y < n; y++)
            {
                int y0 = im.Y0 + y * bh / n;
                int y1 = im.Y0 + Mathf.Max(y + 1, (y + 1) * bh / n) - 1;
                if (y1 >= im.H) y1 = im.H - 1;
                for (int x = 0; x < n; x++)
                {
                    int x0 = im.X0 + x * bw / n;
                    int x1 = im.X0 + Mathf.Max(x + 1, (x + 1) * bw / n) - 1;
                    if (x1 >= im.W) x1 = im.W - 1;
                    float rr = 0, gg = 0, bb = 0;
                    int cnt = 0, fg = 0;
                    for (int sy = y0; sy <= y1; sy++)
                        for (int sx = x0; sx <= x1; sx++)
                        {
                            int idx = sy * im.W + sx;
                            cnt++;
                            if (!im.Mask[idx]) continue;
                            Color32 c = im.Px[idx];
                            rr += c.r; gg += c.g; bb += c.b; fg++;
                        }
                    if (cnt == 0) { cells[y * n + x] = new float[] { 0, 0, 0, 0 }; continue; }
                    if (fg > 0) cells[y * n + x] = new float[] { rr / fg, gg / fg, bb / fg, fg / (float)cnt };
                    else cells[y * n + x] = new float[] { 0, 0, 0, 0 };
                }
            }
            return cells;
        }

        private static float[] GrayBox(Img im, int n)
        {
            int bw = im.X1 - im.X0 + 1, bh = im.Y1 - im.Y0 + 1;
            if (bw <= 0 || bh <= 0) return null;
            float[] g = new float[n * n];
            for (int y = 0; y < n; y++)
            {
                int sy = im.Y0 + Mathf.Min(bh - 1, y * bh / n);
                for (int x = 0; x < n; x++)
                {
                    int sx = im.X0 + Mathf.Min(bw - 1, x * bw / n);
                    Color32 c = im.Px[sy * im.W + sx];
                    g[y * n + x] = 0.299f * c.r + 0.587f * c.g + 0.114f * c.b;
                }
            }
            return g;
        }

        private static float BandSim(int[] a, int[] b)
        {
            int n = 0; float sum = 0f;
            for (int i = 0; i < a.Length && i < b.Length; i++)
            {
                if (a[i] < 0 || b[i] < 0) continue;
                int ar = (a[i] >> 16) & 255, ag = (a[i] >> 8) & 255, ab = a[i] & 255;
                int br2 = (b[i] >> 16) & 255, bg2 = (b[i] >> 8) & 255, bb2 = b[i] & 255;
                sum += (Math.Abs(ar - br2) + Math.Abs(ag - bg2) + Math.Abs(ab - bb2)) / 765f;
                n++;
            }
            return n == 0 ? 0f : 1f - sum / n;
        }

        private static string HexOf(int packed)
        {
            if (packed < 0) return null;
            return "#" + ((packed >> 16) & 255).ToString("X2") + ((packed >> 8) & 255).ToString("X2") + (packed & 255).ToString("X2");
        }

        private static Dictionary<string, object> MetricsJson(Img im)
        {
            int[] bands = BandColors(im, 3);
            var bandList = new List<object>();
            string[] names = new string[] { "head", "torso", "legs" };
            for (int i = 0; i < bands.Length; i++) bandList.Add(Json.Obj("band", names[i], "color", HexOf(bands[i])));
            return Json.Obj(
                "size", Json.Obj("w", im.W, "h", im.H),
                "coverage", (double)im.Coverage,
                "bbox", Json.Obj("x0", im.X0, "y0", im.Y0, "x1", im.X1, "y1", im.Y1,
                                 "w", im.X1 - im.X0 + 1, "h", im.Y1 - im.Y0 + 1),
                "aspect", (double)((im.X1 - im.X0 + 1) / (float)Mathf.Max(1, im.Y1 - im.Y0 + 1)),
                "bands", bandList);
        }

        // 参考图目录（切好的分格缓存在这里）
        private string RefDir { get { return Path.Combine(GameRoot, "UserData/AICharBridge/ref"); } }

        private string RefMetaPath(string name)
        {
            return Path.Combine(RefDir, SanitizeFileName(name) + ".json");
        }

        // ---------- /reference：载入参考图并按"有内容的列"切成多格（三视图） ----------
        private object Reference(string body)
        {
            Dictionary<string, object> req = Json.Parse(body) as Dictionary<string, object>;
            if (req == null) req = new Dictionary<string, object>();
            string file = Str(req, "file", null);
            if (string.IsNullOrEmpty(file)) throw new Exception("用法: {\"file\": \"C:/path/to/ref.jpg\"} 或放在 UserData/AICharBridge 下的文件名");
            string path = file;
            if (!File.Exists(path)) path = Path.Combine(GameRoot, "UserData/AICharBridge", file);
            if (!File.Exists(path)) throw new Exception("找不到参考图: " + file);

            string name = Str(req, "name", null);
            if (string.IsNullOrEmpty(name)) name = Path.GetFileNameWithoutExtension(path);
            if (!Directory.Exists(RefDir)) Directory.CreateDirectory(RefDir);

            return RunOnMain(delegate
            {
                Img whole = LoadImg(path);
                double? wantP = Num(req, "panels");
                int wantPanels = wantP.HasValue ? Mathf.Clamp((int)wantP.Value, 2, 8) : 0;
                // 没指定时：宽图（>1.2 宽高比）且切不出多格 → 按三视图惯例试 3 格
                if (wantPanels == 0 && whole.W > whole.H * 1.2f) wantPanels = 3;
                List<int[]> runs = SplitPanels(whole, wantPanels);

                // 允许显式指定分格 x 区间："0-400,420-820"
                string ranges = Str(req, "ranges", null);
                if (!string.IsNullOrEmpty(ranges))
                {
                    runs = new List<int[]>();
                    foreach (string part in ranges.Split(','))
                    {
                        string[] ab = part.Split('-');
                        if (ab.Length == 2)
                        {
                            int a, b;
                            if (int.TryParse(ab[0], out a) && int.TryParse(ab[1], out b) && b > a)
                                runs.Add(new int[] { a, b });
                        }
                    }
                }

                var panels = new List<object>();
                for (int i = 0; i < runs.Count; i++)
                {
                    int cx0 = runs[i][0], cx1 = runs[i][1];
                    int pad = Mathf.Max(2, whole.W / 200);
                    cx0 = Mathf.Max(0, cx0 - pad); cx1 = Mathf.Min(whole.W - 1, cx1 + pad);
                    int cw = cx1 - cx0 + 1, ch = whole.Y1 - whole.Y0 + 1;
                    Color32[] crop = Crop(whole.Px, whole.W, cx0, whole.Y0, cw, ch);
                    Img im = Wrap(crop, cw, ch, false);
                    string outPath = Path.Combine(RefDir, SanitizeFileName(name) + "_" + i + ".png");
                    Texture2D t = ToTexture(crop, cw, ch);
                    try { File.WriteAllBytes(outPath, t.EncodeToPNG()); } finally { Destroy(t); }
                    panels.Add(Json.Obj("index", i, "file", outPath,
                        "metrics", MetricsJson(im),
                        "silhouette", MaskToRows(im)));
                }

                // 缓存整体信息与各格，便于后续 compare 直接复用
                var meta = Json.Obj("ok", true, "source", path, "name", name,
                    "image", Json.Obj("w", whole.W, "h", whole.H),
                    "bg_color", HexOf(BorderMode(whole.Px, whole.W, whole.H)),
                    "ink_profile", InkProfile(whole),
                    "panel_count", panels.Count, "panels", panels,
                    "hint", panels.Count == 1 ? "只切出 1 格：若这是三视图，用 ranges:\"0-420,430-840,850-1280\" 手工指定，或传 panels:3" : null);
                File.WriteAllText(RefMetaPath(name), Json.Write(meta), Encoding.UTF8);
                AddActivity("√ 参考图已切分: " + name + " → " + panels.Count + " 格");
                return meta;
            });
        }

        // 剪影按行转成字符串（细看形状用；每行 48 个采样点）
        // 行序自下而上（见 CropRegion 的说明）：必须从 im.Y1（头顶）往下打印，否则输出是倒着的，
        // 读图的人和模型都会把脚看成头。
        private static string MaskToRows(Img im)
        {
            var sb = new StringBuilder();
            int bw = im.X1 - im.X0 + 1, bh = im.Y1 - im.Y0 + 1;
            if (bw <= 0 || bh <= 0) return "";
            int rows = 16;
            for (int r = 0; r < rows; r++)
            {
                int y = im.Y1 - bh * r / rows;
                for (int c = 0; c < 48; c++)
                {
                    int x = im.X0 + bw * c / 48;
                    sb.Append(im.Mask[y * im.W + x] ? '#' : '.');
                }
                sb.Append('\n');
            }
            return sb.ToString();
        }

        // 每 64 列采一个前景像素数，作为"内容分布"诊断曲线
        private static object InkProfile(Img img)
        {
            var arr = new List<object>();
            int step = Mathf.Max(1, img.W / 64);
            for (int x0 = 0; x0 < img.W; x0 += step)
            {
                int v = 0;
                for (int y = img.Y0; y <= img.Y1; y++)
                    for (int x = x0; x < Mathf.Min(img.W, x0 + step); x++)
                        if (img.Mask[y * img.W + x]) v++;
                arr.Add(v);
            }
            return arr;
        }

        private static List<int[]> SplitPanels(Img img, int wantPanels)
        {
            var keep = new List<int[]>();
            if (img.X1 <= img.X0) return keep;
            int[] ink = new int[img.W];
            for (int y = img.Y0; y <= img.Y1; y++)
                for (int x = 0; x < img.W; x++)
                    if (img.Mask[y * img.W + x]) ink[x]++;
            // 三视图之间常被飘带/尾巴连住，纯"空列"切不开。
            // 做法：先按空列粗切；若切不出多格（或调用方指定了 N），再在"墨量最低的列"下刀。
            int minInk = Mathf.Max(4, (img.Y1 - img.Y0) / 40);
            int minGap = Mathf.Max(4, img.W / 80);
            var runs = new List<int[]>();
            int start = -1, gapRun = 0;
            for (int x = 0; x < img.W; x++)
            {
                bool has = ink[x] > minInk;
                if (has) { if (start < 0) start = x; gapRun = 0; }
                else
                {
                    gapRun++;
                    if (start >= 0 && gapRun >= minGap) { runs.Add(new int[] { start, x - gapRun }); start = -1; }
                }
            }
            if (start >= 0) runs.Add(new int[] { start, img.W - 1 });
            int minW = Mathf.Max(8, img.W / 25);
            foreach (int[] r in runs) if (r[1] - r[0] + 1 >= minW) keep.Add(r);

            if (keep.Count >= 2 || wantPanels < 2) return keep;

            // 用"最低墨量列"强制切成 wantPanels 格：每刀在自己 1/N 邻域里找墨量最小的列
            keep.Clear();                     // 丢掉上面那次粗切的结果，避免和强制切分的区间叠加
            int n = Mathf.Clamp(wantPanels, 2, 8);
            int lo = img.X0, hi = img.X1;
            int span = hi - lo + 1;
            var cuts = new List<int>();
            for (int k = 1; k < n; k++)
            {
                int center = lo + span * k / n;
                int win = Mathf.Max(6, span / (2 * n));
                int bestX = center, bestInk = int.MaxValue;
                for (int x = Mathf.Max(lo + 4, center - win); x <= Mathf.Min(hi - 4, center + win); x++)
                {
                    // 用 ±2 列平滑，避免噪声列被当成谷底
                    int v = 0;
                    for (int d = -2; d <= 2; d++)
                    {
                        int xx = Mathf.Clamp(x + d, 0, img.W - 1);
                        v += ink[xx];
                    }
                    if (v < bestInk) { bestInk = v; bestX = x; }
                }
                cuts.Add(bestX);
            }
            cuts.Sort();
            int prev = lo;
            foreach (int c in cuts)
            {
                if (c - prev >= minW) keep.Add(new int[] { prev, c - 1 });
                prev = c;
            }
            if (hi - prev + 1 >= minW) keep.Add(new int[] { prev, hi });
            if (keep.Count < 2) { keep.Clear(); keep.Add(new int[] { lo, hi }); }   // 兜底：整张当一格
            return keep;
        }

        // ---------- 离屏渲染单视角为 Img（compare / fit 复用） ----------
        private static Img OffscreenShot(Camera mainCam, ChaControl target, string view, string framing, int pw, int ph)
        {
            Vector3 focusPt; float fitH, fitW;
            AutoFrame(target, framing, out focusPt, out fitH, out fitW);
            float aspect = Mathf.Clamp(fitW / Mathf.Max(fitH, 0.01f), 0.6f, 1.6f);
            float halfTan = Mathf.Tan(CaptureFov * 0.5f * Mathf.Deg2Rad);
            float dist = Mathf.Max((fitH * 0.5f) / halfTan, (fitW * 0.5f) / (halfTan * aspect)) + 0.25f;

            GameObject go = new GameObject("AICharBridgeMeasureCam");
            RenderTexture rt = null;
            try
            {
                Camera cam = go.AddComponent<Camera>();
                cam.CopyFrom(mainCam);
                cam.cullingMask = mainCam.cullingMask & ~(1 << 5);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = ShotBg;                 // 用固定底色，剪影按精确色比对
                cam.fieldOfView = CaptureFov;
                cam.aspect = pw / (float)ph;
                cam.enabled = false;
                rt = MakeRT(pw * 2, ph * 2);
                cam.targetTexture = rt;

                Vector3 dir = ResolveViewDir(target, view);
                cam.transform.position = focusPt + dir * dist + Vector3.up * 0.02f;
                cam.transform.LookAt(focusPt);
                cam.Render();

                RenderTexture.active = rt;
                Texture2D t = new Texture2D(pw, ph, TextureFormat.RGB24, false);
                t.ReadPixels(new Rect(0, 0, pw, ph), 0, 0);
                t.Apply();
                RenderTexture.active = null;
                Color32[] px = t.GetPixels32();
                Destroy(t);
                return Wrap(px, pw, ph, true);               // exactBg = true
            }
            finally
            {
                if (rt != null) { rt.Release(); Destroy(rt); }
                Destroy(go);
            }
        }

        // 参考图切好的某一格的 Img
        private Img LoadRefPanel(string refName, int index)
        {
            string metaPath = RefMetaPath(refName);
            if (!File.Exists(metaPath))
            {
                // 允许直接给图片路径（未切分的单图）
                string direct = Path.Combine(GameRoot, "UserData/AICharBridge", refName);
                if (File.Exists(direct)) return LoadImg(direct);
                throw new Exception("还没切分过这张参考图，请先调用 /reference: " + refName);
            }
            string p = Path.Combine(RefDir, SanitizeFileName(refName) + "_" + index + ".png");
            if (!File.Exists(p)) throw new Exception("参考图没有第 " + index + " 格（先看 /reference 返回的 panel_count）");
            return LoadImg(p);
        }

        // 拍照/测量前强制穿着：AddFemale 之后的"恢复穿着"是协程（DressAfterLoad），
        // 而 /generate 的响应不等它 —— 于是紧跟其后的截图会拍到"裤袜还没穿上"的中间态
        // （实测活动日志：17:14:42 截图，17:14:45 才 SetClothesStateAll）。
        // 所以把"穿着"放进渲染路径本身，让稳定判定去等衣服真的加载出来。
        // 只对"有 id 但状态是脱"的部位生效：id=0（本来就没穿）不受影响。
        private static bool EnsureDressed(ChaControl cc)
        {
            if (cc == null) return false;
            try
            {
                ChaFileControl cf = cc.chaFile;
                if (cf == null || cf.status == null || cf.status.clothesState == null) return false;
                bool anyOff = false;
                for (int i = 0; i < cf.status.clothesState.Length; i++)
                    if (cf.status.clothesState[i] != 0) { anyOff = true; break; }
                if (!anyOff) return false;
                cc.SetClothesStateAll(0);
                return true;
            }
            catch (Exception) { return false; }
        }

        private static int RendererCount(ChaControl cc)
        {
            if (cc == null) return 0;
            Renderer[] rs = cc.GetComponentsInChildren<Renderer>();
            int n = 0;
            for (int i = 0; i < rs.Length; i++)
                if (rs[i] != null && rs[i].enabled && !(rs[i] is ParticleSystemRenderer)) n++;
            return n;
        }

        // 离屏渲染用的 RT：必须跟随游戏的抗锯齿设置。
        // 直接 new RenderTexture(w,h,24) 是**没有 MSAA** 的，而游戏主画面有 —— 于是截图看起来
        // 边缘锯齿/坑洼，很容易被误判成"模型破了/脸不平"。实测这一步影响很大。
        private static RenderTexture MakeRT(int w, int h) { return MakeRT(w, h, 0); }

        // aaOverride: 0=跟随游戏设置，其它值强制（用于对照实验，比如 aa=1 关掉抗锯齿）
        private static RenderTexture MakeRT(int w, int h, int aaOverride)
        {
            int aa = 1;
            try { aa = aaOverride > 0 ? aaOverride : QualitySettings.antiAliasing; } catch (Exception) { }
            if (aa < 1) aa = 1;
            if (aa > 8) aa = 8;
            RenderTexture rt = null;
            if (aa > 1)
            {
                try
                {
                    rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                    rt.antiAliasing = aa;
                    if (!rt.Create()) { try { rt.Release(); } catch (Exception) { } rt = null; }
                }
                catch (Exception) { rt = null; }
            }
            if (rt == null) rt = new RenderTexture(w, h, 24);   // 保底：普通 RT
            return rt;
        }

        // 盒式降采样（2x -> 1x）。
        // 为什么要自己做抗锯齿：本机质量设置里 AA 是关的，给 RenderTexture 设 antiAliasing
        // **实测无效**（aa=1 与 aa=4 两张图逐像素完全相同，meanAbsDiff=0.000），
        // 而角落锯齿看起来就像"脸坑坑洼洼/模型破了"。所以改成"渲染 2 倍再降采样"，
        // 不依赖 MSAA 支持，效果确定。
        private static Color32[] Downsample(Color32[] src, int sw, int sh, int dw, int dh)
        {
            var dst = new Color32[dw * dh];
            for (int y = 0; y < dh; y++)
            {
                int sy = y * sh / dh;
                for (int x = 0; x < dw; x++)
                {
                    int sx = x * sw / dw;
                    int r = 0, g = 0, b = 0, n = 0;
                    for (int j = 0; j < sh / dh; j++)
                        for (int i = 0; i < sw / dw; i++)
                        {
                            int idx = (sy + j) * sw + (sx + i);
                            if (idx < 0 || idx >= src.Length) continue;
                            r += src[idx].r; g += src[idx].g; b += src[idx].b; n++;
                        }
                    if (n == 0) n = 1;
                    dst[y * dw + x] = new Color32((byte)(r / n), (byte)(g / n), (byte)(b / n), 255);
                }
            }
            return dst;
        }

        // 从（可能是 MSAA 的）渲染目标读像素。
        // ⚠ MSAA 的 RenderTexture **不能直接 ReadPixels**（Unity 会报错/读不到），
        // 必须先 Blit 解析到一张普通 RT 再读。plain 由调用方复用（每次 GetTemporary 太浪费）。
        private static Color32[] ReadRT(RenderTexture rt, RenderTexture plain, int w, int h)
        {
            RenderTexture tmp = null;
            RenderTexture src = rt;
            if (rt != null && rt.antiAliasing > 1)
            {
                if (plain != null) src = plain;
                else { tmp = RenderTexture.GetTemporary(w, h, 0, RenderTextureFormat.ARGB32); src = tmp; }
                Graphics.Blit(rt, src);   // 采样 MSAA 目标即完成解析
            }
            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = src;
            Texture2D t = new Texture2D(w, h, TextureFormat.RGB24, false);
            try
            {
                t.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                t.Apply();
                return t.GetPixels32();
            }
            finally
            {
                RenderTexture.active = prev;
                Destroy(t);
                if (tmp != null) RenderTexture.ReleaseTemporary(tmp);
            }
        }

        private class ShotBox { public Img Img; public bool Settled; public int Attempts; public bool Frozen; public Exception Error; }

        // 协程里的 yield 不能放进 try/catch（C# 限制），所以协程体内抛出的异常会直接散到 Unity、
        // TaskCompletionSource 永远不完成 —— HTTP 调用方只能干等 30~300 秒超时，还拿到误导性的
        // "超时"错误。这个包装器逐帧推进内层协程，把异常抓进 box.Error，让调用方能落到 500 返回。
        // 所有 ShotSettled 的调用点都必须走这里。
        private static IEnumerator ShotSettledSafe(Camera mainCam, ChaControl target, string view, string framing,
            int pw, int ph, ShotBox box, bool dress = true)
        {
            IEnumerator inner = Instance.ShotSettled(mainCam, target, view, framing, pw, ph, box, dress);
            while (true)
            {
                object current;
                try
                {
                    if (!inner.MoveNext()) yield break;
                    current = inner.Current;
                }
                catch (Exception e) { box.Error = e; yield break; }
                yield return current;
            }
        }

        // 测量前的"确定性"处理：角色每次重新加载后，头发的 DynamicBone 会从头开始摆动、
        // 眼睛还在眨 —— 结果就是"除了被搜的那个参数，画面还有 10%+ 的像素在变"，
        // 像素指标会被这些噪声淹没（实测：两个只有眼型不同的候选，diff 覆盖 14% 像素）。
        // 这里把物理骨骼停掉、强制睁眼，让同一个参数两次渲染尽量一致。
        private static List<Behaviour> FreezeDynamics(ChaControl target)
        {
            var saved = new List<Behaviour>();
            if (target == null) return saved;
            try
            {
                DynamicBone[] bones = target.GetComponentsInChildren<DynamicBone>(true);
                foreach (DynamicBone b in bones)
                {
                    if (b == null || !b.enabled) continue;
                    saved.Add(b);
                    b.enabled = false;
                }
            }
            catch (Exception) { }
            // 强制睁眼：眨眼会让两张只差眼型的图彻底不可比（用反射调用，接口缺失也不影响主流程）
            try
            {
                MethodInfo mi = target.GetType().GetMethod("ChangeEyesBlinkFlag", BindingFlags.Public | BindingFlags.Instance);
                if (mi != null && mi.GetParameters().Length == 1) mi.Invoke(target, new object[] { false });
                MethodInfo mo = target.GetType().GetMethod("ChangeEyesOpen", BindingFlags.Public | BindingFlags.Instance);
                if (mo != null && mo.GetParameters().Length == 1) mo.Invoke(target, new object[] { true });
            }
            catch (Exception) { }
            return saved;
        }

        private static void RestoreDynamics(List<Behaviour> saved)
        {
            if (saved == null) return;
            for (int i = 0; i < saved.Count; i++)
                if (saved[i] != null) saved[i].enabled = true;
        }

        // 等画面稳定的单视角离屏渲染：服装/头发是异步加载的，不等就会拍到"缺件"的中间态。
        // 必须作为协程调用（等待期间要让主线程继续跑，异步资源才会被应用）。
        private IEnumerator ShotSettled(Camera mainCam, ChaControl target, string view, string framing,
            int pw, int ph, ShotBox box, bool dress = true)
        {
            // 先补齐穿着（见 EnsureDressed 的说明）：否则可能拍到裤袜/饰品还没挂上的中间态
            if (dress)
            {
                bool changed = EnsureDressed(target);
                if (changed) yield return new WaitForSecondsRealtime(0.6f);
            }

            // 先冻结物理骨骼，再做稳定判定 —— 不冻结的话头发每帧都在飘，"稳定"判据永远只能等到
            // 物理停下来，而且两次加载之间的落点不同，测量不可复现。
            List<Behaviour> frozen = FreezeDynamics(target);            box.Frozen = frozen.Count > 0;

            // 等网格加载完（服装/头发异步加载；像素指纹在加载停顿的瞬间也可能"看起来稳定"）
            int lastCount = -1, stable = 0;
            for (int i = 0; i < 18 && stable < 3; i++)
            {
                int c = RendererCount(target);
                if (c > 0 && c == lastCount) stable++;
                else { stable = 0; lastCount = c; }
                yield return new WaitForSecondsRealtime(0.35f);
            }

            GameObject go = new GameObject("AICharBridgeMeasureCam");
            RenderTexture rt = null;
            try
            {
                Camera cam = go.AddComponent<Camera>();
                cam.CopyFrom(mainCam);
                cam.cullingMask = mainCam.cullingMask & ~(1 << 5);
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = ShotBg;
                cam.fieldOfView = CaptureFov;
                cam.aspect = pw / (float)ph;
                cam.enabled = false;
                rt = MakeRT(pw, ph);
                cam.targetTexture = rt;

                Vector3 focusPt; float fitH, fitW;
                AutoFrame(target, framing, out focusPt, out fitH, out fitW);
                float aspect = Mathf.Clamp(fitW / Mathf.Max(fitH, 0.01f), 0.6f, 1.6f);
                float halfTan = Mathf.Tan(CaptureFov * 0.5f * Mathf.Deg2Rad);
                float dist = Mathf.Max((fitH * 0.5f) / halfTan, (fitW * 0.5f) / (halfTan * aspect)) + 0.25f;
                Vector3 dir = ResolveViewDir(target, view);
                cam.transform.position = focusPt + dir * dist + Vector3.up * 0.02f;
                cam.transform.LookAt(focusPt);

                // 稳定判定：连续两次粗指纹一致
                string prev = null;
                box.Settled = false;
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    box.Attempts = attempt + 1;
                    cam.Render();
                    Color32[] px = ReadRT(rt, null, pw * 2, ph * 2);
                    px = Downsample(px, pw * 2, ph * 2, pw, ph);
                    string sig = PanelSignature(px, pw, ph);
                    if (prev == sig)
                    {
                        box.Settled = true;
                        box.Img = Wrap(px, pw, ph, true);
                        break;
                    }
                    prev = sig;
                    box.Img = Wrap(px, pw, ph, true);   // 先记下，超时也有结果
                    yield return new WaitForSecondsRealtime(0.4f);
                }
            }
            finally
            {
                if (rt != null) { rt.Release(); Destroy(rt); }
                Destroy(go);
                RestoreDynamics(frozen);
            }
        }

        // 参考图按部位裁剪：搜脸部细节时必须把参考也裁到头部区域，否则尺度对不上、指标无区分度
        //
        // ⚠ 行序（踩过的坑）：Unity 的 Texture2D.GetPixels32() 与 Blender 的 image.pixels 一样是
        // **自下而上**的 —— 数组第 0 行是图像底部。所以 im.Y0 是画面**最低**的行（脚），im.Y1 是最高（头）。
        // 曾按"Y0 = 顶"写，结果 ref_crop:"head" 裁出来的是**鞋子**（把参考图的腿当脸去比对，
        // 眼型搜索因此全程在跟鞋子打分）。凡是按"上/下"取区间的地方都必须反着来。
        private static Img CropRegion(Img im, string region)
        {
            if (string.IsNullOrEmpty(region) || region == "full") return im;
            int bh = im.Y1 - im.Y0 + 1;
            if (bh <= 0) return im;
            // 用"距顶部多少"来表达：top = im.Y1
            int y0 = im.Y0, y1 = im.Y1;
            if (region == "head") y0 = im.Y1 - Mathf.Max(2, Mathf.RoundToInt(bh * 0.28f)) + 1;          // 顶部 28%
            else if (region == "torso") { y1 = im.Y1 - Mathf.RoundToInt(bh * 0.28f); y0 = im.Y1 - Mathf.RoundToInt(bh * 0.62f) + 1; }  // 28%~62%
            else if (region == "legs") y1 = im.Y1 - Mathf.RoundToInt(bh * 0.62f);                        // 底部 38%
            if (y0 < im.Y0) y0 = im.Y0;
            if (y1 > im.Y1) y1 = im.Y1;
            if (y1 <= y0) return im;
            int cw = im.X1 - im.X0 + 1, ch = y1 - y0 + 1;
            Color32[] crop = Crop(im.Px, im.W, im.X0, y0, cw, ch);
            return Wrap(crop, cw, ch, false);
        }

        // ---------- /compare：拍自己的一个视角，与参考图某格打分 ----------
        private object Compare(string body)
        {
            Dictionary<string, object> req = Json.Parse(body) as Dictionary<string, object>;
            if (req == null) req = new Dictionary<string, object>();
            string refName = Str(req, "ref", null);
            if (string.IsNullOrEmpty(refName)) throw new Exception("用法: {\"ref\":\"参考图名\", \"panel\":0, \"view\":\"front\"}");
            double? pIdx = Num(req, "panel");
            int panel = pIdx.HasValue ? (int)pIdx.Value : 0;
            string view = Str(req, "view", "front");
            string framing = Str(req, "framing", "full");
            string name = Str(req, "name", null);
            string outFile = Str(req, "file", null);
            if (string.IsNullOrEmpty(outFile)) outFile = "cmp_" + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".png";
            outFile = SanitizeFileName(EnsurePng(outFile));

            string dir = Path.Combine(GameRoot, "UserData/AICharBridge");
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string abs = Path.Combine(dir, outFile);

            var tcs = new TaskCompletionSource<object>();
            _mainThread.Enqueue(delegate
            {
                try { StartCoroutine(CompareCoroutine(refName, panel, view, framing, name, abs, req, tcs)); }
                catch (Exception e) { tcs.SetException(e); }
            });
            if (!tcs.Task.Wait(180000)) throw new Exception("对比超时：主线程 180 秒未完成");
            if (tcs.Task.IsFaulted) throw tcs.Task.Exception.InnerException ?? tcs.Task.Exception;
            return tcs.Task.Result;
        }

        private IEnumerator CompareCoroutine(string refName, int panel, string view, string framing,
            string name, string abs, Dictionary<string, object> req, TaskCompletionSource<object> tcs)
        {
                Camera mainCam = Camera.main;
                if (mainCam == null) { tcs.SetException(new Exception("没有可用的主相机")); yield break; }
                string tname;
                ChaControl target = FindStudioChar(name, out tname);
                if (target == null) { tcs.SetException(new Exception("场景里没有可拍的角色（先用 load:true 把角色加进工作室）")); yield break; }

                Img refIm;
                try { refIm = CropRegion(LoadRefPanel(refName, panel), Str(req, "ref_crop", "full")); }
                catch (Exception e) { tcs.SetException(e); yield break; }   // 参考图有问题要立刻报错，不能等超时
                var box = new ShotBox();
                yield return StartCoroutine(ShotSettledSafe(mainCam, target, view, framing, 384, 512, box));
                if (box.Error != null) { tcs.SetException(box.Error); yield break; }
                Img myIm = box.Img;
                if (myIm == null) { tcs.SetException(new Exception("渲染失败")); yield break; }

                try
                {
                float wCore = 0.35f, wFull = 0.10f, wHist = 0.15f, wBand = 0.25f, wApp = 0.15f;
                object wo;
                if (req != null && req.TryGetValue("weights", out wo))
                {
                    var wd = wo as Dictionary<string, object>;
                    if (wd != null)
                    {
                        double? v;
                        v = Num(wd, "iou_core"); if (v.HasValue) wCore = (float)v.Value;
                        v = Num(wd, "iou_full"); if (v.HasValue) wFull = (float)v.Value;
                        v = Num(wd, "color_hist"); if (v.HasValue) wHist = (float)v.Value;
                        v = Num(wd, "band_color"); if (v.HasValue) wBand = (float)v.Value;
                        v = Num(wd, "appearance"); if (v.HasValue) wApp = (float)v.Value;
                    }
                }

                bool[] rn = Normalize(refIm), mn = Normalize(myIm);
                float iouCore = Iou(rn, mn, 0.5f);      // 中央 50% 宽（避开 T-pose 手臂）
                float iouFull = Iou(rn, mn, 0f);
                float histSim = HistSim(Hist(refIm), Hist(myIm));
                int[] rb = BandColors(refIm, 3), mb = BandColors(myIm, 3);
                float bandSim = BandSim(rb, mb);
                float appSim = AppSim(refIm, myIm);
                float total = wCore * iouCore + wFull * iouFull + wHist * histSim + wBand * bandSim + wApp * appSim;

                // 并排拼图：参考格 | 我的渲染（同高，最近邻缩放）
                int H = 512;
                int rw = Mathf.Max(1, Mathf.RoundToInt(refIm.X1 - refIm.X0 + 1) * H / Mathf.Max(1, refIm.Y1 - refIm.Y0 + 1));
                int mw = Mathf.Max(1, Mathf.RoundToInt(myIm.W * H / (float)myIm.H));
                int gap = 8;
                Color32[] sheet = new Color32[(rw + gap + mw) * H];
                for (int i = 0; i < sheet.Length; i++) sheet[i] = new Color32(23, 28, 41, 255);
                BlitNearest(refIm.Px, refIm.W, refIm.X0, refIm.Y0, refIm.X1 - refIm.X0 + 1, refIm.Y1 - refIm.Y0 + 1,
                    sheet, rw + gap + mw, 0, 0, rw, H);
                BlitNearest(myIm.Px, myIm.W, 0, 0, myIm.W, myIm.H, sheet, rw + gap + mw, rw + gap, 0, mw, H);
                Texture2D sheetTex = ToTexture(sheet, rw + gap + mw, H);
                try { File.WriteAllBytes(abs, sheetTex.EncodeToPNG()); } finally { Destroy(sheetTex); }

                string verdict = total >= 0.62f ? "close" : (total >= 0.48f ? "partial" : "far");
                AddActivity("√ 对标 " + refName + "#" + panel + " vs " + view + "：总分 " + total.ToString("0.000") + "（" + verdict + "）");

                var res = Json.Obj("ok", true, "ref", refName, "panel", panel, "view", view, "focus", tname,
                    "score", Json.Obj("total", (double)total, "iou_core", (double)iouCore, "iou_full", (double)iouFull,
                                      "color_hist", (double)histSim, "band_color", (double)bandSim,
                                      "appearance", (double)appSim, "verdict", verdict),
                    "weights", Json.Obj("iou_core", (double)wCore, "iou_full", (double)wFull,
                                        "color_hist", (double)wHist, "band_color", (double)wBand, "appearance", (double)wApp),
                    "settled", box.Settled, "settle_attempts", box.Attempts, "frozen_dynamics", box.Frozen,
                    "band_colors", Json.Obj("ref", new object[] { HexOf(rb[0]), HexOf(rb[1]), HexOf(rb[2]) },
                                            "mine", new object[] { HexOf(mb[0]), HexOf(mb[1]), HexOf(mb[2]) }),
                    "montage", abs,
                    "note", "iou_core 只算中央 50% 宽，用来避开工作室 T-pose 手臂；判读时结合 montage 一起看");
                tcs.TrySetResult(res);
                }
                catch (Exception e) { tcs.SetException(e); }   // 打分/写文件失败要变成 500，不是 60 秒超时
        }

        private static void BlitNearest(Color32[] src, int sw, int sx, int sy, int cw, int ch,
            Color32[] dst, int dw, int dx, int dy, int outW, int outH)
        {
            for (int y = 0; y < outH; y++)
            {
                int syy = sy + Mathf.Min(ch - 1, y * ch / Mathf.Max(1, outH));
                for (int x = 0; x < outW; x++)
                {
                    int sxx = sx + Mathf.Min(cw - 1, x * cw / Mathf.Max(1, outW));
                    dst[(dy + y) * dw + dx + x] = src[syy * sw + sxx];
                }
            }
        }

        // ================= /fit：服务端搜索（零模型轮次） =================
        // 在已加载的角色上直接换发型/发色 -> 离屏渲染 -> 与参考图打分，返回候选榜。
        // 结束时会把最优参数重新应用（rt_loop 的坑：循环停下时停在最后一轮，不是最好那轮）。

        private object Fit(string body)
        {
            Dictionary<string, object> req = Json.Parse(body) as Dictionary<string, object>;
            if (req == null) req = new Dictionary<string, object>();
            string refName = Str(req, "ref", null);
            if (string.IsNullOrEmpty(refName)) throw new Exception("用法: {\"ref\":\"参考图\", \"kind\":\"hair_back\", \"candidates\":[1,2,3]}");
            double? pIdx = Num(req, "panel");
            int panel = pIdx.HasValue ? (int)pIdx.Value : 0;
            string view = Str(req, "view", "front");
            string framing = Str(req, "framing", "full");
            string name = Str(req, "name", null);
            string mode = Str(req, "mode", "hair");
            string refCrop = Str(req, "ref_crop", "full");
            var weights = req.ContainsKey("weights") ? req["weights"] as Dictionary<string, object> : null;
            if (mode == "reload") return FitReload(req);
            string kind = Str(req, "kind", "hair_back");
            string kindKey = kind.StartsWith("hair_", StringComparison.Ordinal) ? kind.Substring(5) : kind;
            int kindIdx = HairIndex(kindKey);
            if (kindIdx < 0) throw new Exception("kind 只支持 hair_back/hair_front/hair_side/hair_option（收到 " + kind + "）");

            double? topKd = Num(req, "top_k");
            int topK = topKd.HasValue ? Mathf.Clamp((int)topKd.Value, 1, 20) : 5;
            double? budgetd = Num(req, "budget_ms");
            int budgetMs = budgetd.HasValue ? Mathf.Clamp((int)budgetd.Value, 2000, 240000) : 60000;
            bool applyBest = Bool(req, "apply_best", true);
            bool saveShotsHair = Bool(req, "save_shots", false);

            var cands = new List<int>();
            object co;
            if (req.TryGetValue("candidates", out co) && co is List<object>)
                foreach (object o in (List<object>)co)
                    if (o is double) cands.Add((int)(double)o);
            if (cands.Count == 0) throw new Exception("candidates 为空：给一组要试的发型 id（用 kks_list_options 取）");

            // 可选的发色候选（对每个候选色，配合当前 id 一起试）
            var colorCands = new List<string>();
            object cco;
            if (req.TryGetValue("colors", out cco) && cco is List<object>)
                foreach (object o in (List<object>)cco)
                    if (o is string) colorCands.Add((string)o);

            var tcs = new TaskCompletionSource<object>();
            _mainThread.Enqueue(delegate
            {
                try
                {
                    StartCoroutine(FitCoroutine(refName, panel, view, framing, name, kindIdx, kind,
                        cands, colorCands, topK, budgetMs, applyBest, refCrop, weights, saveShotsHair, tcs));
                }
                catch (Exception e) { tcs.SetException(e); }
            });
            if (!tcs.Task.Wait(300000)) throw new Exception("搜索超时：主线程 300 秒未完成");
            if (tcs.Task.IsFaulted) throw tcs.Task.Exception.InnerException ?? tcs.Task.Exception;
            return tcs.Task.Result;
        }

        private IEnumerator FitCoroutine(string refName, int panel, string view, string framing, string name,
            int kindIdx, string kind, List<int> cands, List<string> colorCands, int topK, int budgetMs,
            bool applyBest, string refCrop, Dictionary<string, object> reqW, bool saveShots,
            TaskCompletionSource<object> tcs)
        {
                Camera mainCam = Camera.main;
                if (mainCam == null) { tcs.SetException(new Exception("没有可用的主相机")); yield break; }
                string tname;
                ChaControl target = FindStudioChar(name, out tname);
                if (target == null) { tcs.SetException(new Exception("场景里没有可调的角色（先用 load:true 加进工作室）")); yield break; }
                if (target.chaFile == null || target.chaFile.custom == null || target.chaFile.custom.hair == null)
                { tcs.SetException(new Exception("拿不到角色的发型数据")); yield break; }

                ChaFileHair.PartsInfo part = target.chaFile.custom.hair.parts[kindIdx];
                Img refIm;
                try { refIm = CropRegion(LoadRefPanel(refName, panel), refCrop); }
                catch (Exception e) { tcs.SetException(e); yield break; }   // 参考图缺失要立刻报错，不能干等 300 秒超时
                bool[] refN = Normalize(refIm);
                int[] refHist = Hist(refIm);
                int[] refBands = BandColors(refIm, 3);

                int origId = part.id;
                Color origBase = part.baseColor, origStart = part.startColor, origEnd = part.endColor;

                var board = new List<object>();
                var shotImgs = new Dictionary<int, Img>();
                string shotDir = null;
                if (saveShots)
                {
                    shotDir = Path.Combine(GameRoot, "UserData/AICharBridge");
                    if (!Directory.Exists(shotDir)) Directory.CreateDirectory(shotDir);
                }
                var sw = System.Diagnostics.Stopwatch.StartNew();
                int iter = 0, tested = 0;

                // 候选组合：先只换 id（色不动），若给了 colors 则每个 id 配一个色再各试一轮
                var combos = new List<int[]>();
                if (colorCands.Count == 0)
                    foreach (int id in cands) combos.Add(new int[] { id, -1 });
                else
                    foreach (int id in cands)
                        for (int ci = 0; ci < colorCands.Count; ci++) combos.Add(new int[] { id, ci });

                // 循环体含 yield，不能放 try/catch（C# 限制）；用 try/finally 兜底：
                // 若中途异常散出（tcs 未完成），finally 把角色从测试候选还原回原发型
                try
                {
                foreach (int[] combo in combos)
                {
                    if (sw.ElapsedMilliseconds > budgetMs) break;
                    iter++;
                    int id = combo[0];
                    part.id = id;
                    if (combo[1] >= 0)
                    {
                        Color col = ParseColorValue(colorCands[combo[1]]);
                        part.baseColor = col; part.startColor = col; part.endColor = col;
                    }
                    else
                    {
                        part.baseColor = origBase; part.startColor = origStart; part.endColor = origEnd;
                    }

                    // 运行时热换（同步接口），不必重载卡片
                    string err = null;
                    try
                    {
                        target.ChangeHair(kindIdx, id, true);
                        target.ChangeSettingHairColor(kindIdx, true, true, true);
                    }
                    catch (Exception e) { err = e.Message; }
                    if (err != null)
                    {
                        board.Add(Json.Obj("iter", iter, "id", id, "color", combo[1] >= 0 ? colorCands[combo[1]] : null,
                            "error", err));
                        continue;
                    }

                    // 等画面稳定再量（发型网格异步加载中拍到的分数没有意义）
                    var b2 = new ShotBox();
                    yield return StartCoroutine(ShotSettledSafe(mainCam, target, view, framing, 288, 384, b2));
                    if (b2.Error != null)
                    {
                        // 渲染管线出问题时继续测下一个候选也是白测：记下错误优雅收尾
                        //（不要 throw：协程里的异常只会被 Unity 吞掉，tcs 永远不完成 → 调用方干等超时）
                        board.Add(Json.Obj("iter", iter, "id", id,
                            "color", combo[1] >= 0 ? colorCands[combo[1]] : null,
                            "error", "渲染异常: " + b2.Error.Message));
                        break;
                    }
                    Img mine = b2.Img;
                    if (mine == null) continue;
                    float iouCore = Iou(refN, Normalize(mine), 0.5f);
                    float iouFull = Iou(refN, Normalize(mine), 0f);
                    float histSim = HistSim(refHist, Hist(mine));
                    float bandSim = BandSim(refBands, BandColors(mine, 3));
                    float appSim = AppSim(refIm, mine);
                    float total = FitTotal(reqW, iouCore, iouFull, histSim, bandSim, appSim);
                    tested++;
                    string leafHair = id + (combo[1] >= 0 ? "_" + colorCands[combo[1]] : "");
                    string shotPathHair = null;
                    if (shotDir != null)
                    {
                        shotPathHair = Path.Combine(shotDir, "fit_" + iter.ToString("00") + "_" + SanitizeFileName(leafHair) + ".png");
                        try { WriteImg(mine, shotPathHair); shotImgs[iter] = mine; }
                        catch (Exception) { shotPathHair = null; }
                    }
                    board.Add(Json.Obj("iter", iter, "id", id,
                        "color", combo[1] >= 0 ? colorCands[combo[1]] : null,
                        "candidate_leaf", leafHair, "shot", shotPathHair, "frozen", b2.Frozen,
                        "total", (double)total, "iou_core", (double)iouCore, "iou_full", (double)iouFull,
                        "color_hist", (double)histSim, "band_color", (double)bandSim, "appearance", (double)appSim,
                        "ms", sw.ElapsedMilliseconds));
                }

                // 排序取前 K
                board.Sort(delegate (object a, object b)
                {
                    double da = ((Dictionary<string, object>)a).ContainsKey("total") ? System.Convert.ToDouble(((Dictionary<string, object>)a)["total"]) : -1;
                    double db = ((Dictionary<string, object>)b).ContainsKey("total") ? System.Convert.ToDouble(((Dictionary<string, object>)b)["total"]) : -1;
                    return db.CompareTo(da);
                });
                var top = board.GetRange(0, Math.Min(topK, board.Count));
                var best = top.Count > 0 ? top[0] as Dictionary<string, object> : null;

                // 候选接触印相（左→右 = 得分高→低），让 AI 能看图复核，而不是只信数字
                string boardPng = null;
                var boardOrder = new List<object>();
                if (shotDir != null)
                {
                    var cells = new List<Img>();
                    for (int i = 0; i < top.Count; i++)
                    {
                        var e = top[i] as Dictionary<string, object>;
                        if (e == null || !e.ContainsKey("iter")) continue;
                        int itv;
                        try { itv = System.Convert.ToInt32(e["iter"]); } catch (Exception) { continue; }
                        object lf;
                        boardOrder.Add((e.TryGetValue("candidate_leaf", out lf) && lf is string ? (string)lf : itv.ToString())
                            + ":" + (e.ContainsKey("total") ? System.Convert.ToDouble(e["total"]).ToString("0.000") : "-"));
                        if (shotImgs.ContainsKey(itv)) cells.Add(shotImgs[itv]);
                    }
                    if (cells.Count > 0) boardPng = MontageToFile(cells, Path.Combine(shotDir, "fit_board.png"));
                }

                // 回到最优（循环停下时场景停在最后一轮，不是最好那轮）
                bool restored = false;
                if (best != null && best.ContainsKey("id"))
                {
                    int bestId = System.Convert.ToInt32(best["id"]);
                    string bestColor = best.ContainsKey("color") && best["color"] != null ? (string)best["color"] : null;
                    part.id = bestId;
                    if (bestColor != null)
                    {
                        Color c = ParseColorValue(bestColor);
                        part.baseColor = c; part.startColor = c; part.endColor = c;
                    }
                    else
                    {
                        part.baseColor = origBase; part.startColor = origStart; part.endColor = origEnd;
                    }
                    try
                    {
                        target.ChangeHair(kindIdx, bestId, true);
                        target.ChangeSettingHairColor(kindIdx, true, true, true);
                        restored = true;
                    }
                    catch (Exception) { }
                }
                else if (!applyBest)
                {
                    part.id = origId;
                    try { target.ChangeHair(kindIdx, origId, true); target.ChangeSettingHairColor(kindIdx, true, true, true); } catch (Exception) { }
                }

                AddActivity("√ 搜索 " + kind + "：" + tested + " 个候选 / " + sw.ElapsedMilliseconds + " ms，最优 " +
                    (best != null && best.ContainsKey("id") ? best["id"].ToString() : "无"));

                tcs.TrySetResult(Json.Obj("ok", true, "kind", kind, "view", view, "ref", refName, "panel", panel,
                    "iterations", iter, "tested", tested, "elapsed_ms", sw.ElapsedMilliseconds,
                    "budget_ms", budgetMs, "top", top, "board", board,
                    "board_png", boardPng, "board_order", boardOrder,
                    "restored_best", restored, "original_id", origId,
                    "note", "分数口径与 kks_compare 一致；结束时已把最优参数应用回角色。save_shots=true 时每个候选留一张 PNG，board_png 是接触印相（左→右 = 得分高→低）"));
                }
                finally
                {
                    // tcs 已完成 = 正常结束（最优已应用回角色，无需再动）；
                    // 未完成 = 异常散出 —— 还原发型 + 把异常落回 tcs（否则调用方只能等超时）
                    if (!tcs.Task.IsCompleted)
                    {
                        try
                        {
                            part.id = origId; part.baseColor = origBase; part.startColor = origStart; part.endColor = origEnd;
                            target.ChangeHair(kindIdx, origId, true);
                            target.ChangeSettingHairColor(kindIdx, true, true, true);
                        }
                        catch (Exception) { }
                        AddActivity("! 发型搜索中断，已恢复原发型");
                        tcs.TrySetException(new Exception("搜索协程异常中断（已恢复原发型，根因见 BepInEx 日志）"));
                    }
                }
        }

        // ================= 通用搜索（mode=reload）：任意生成参数 =================
        // 眼型、唇线、花纹、饰品……这些没有现成的运行时刷新接口，走"写临时卡 -> 替换角色 -> 渲染 -> 打分"。
        // 好处：什么参数都能搜；代价：每轮约 2~4 秒（受 AddFemale 加载耗时限制）。

        private static Dictionary<string, object> DeepMerge(Dictionary<string, object> baseD, Dictionary<string, object> overD)
        {
            var outD = new Dictionary<string, object>();
            if (baseD != null) foreach (KeyValuePair<string, object> kv in baseD) outD[kv.Key] = kv.Value;
            if (overD == null) return outD;
            foreach (KeyValuePair<string, object> kv in overD)
            {
                var bn = outD.ContainsKey(kv.Key) ? outD[kv.Key] as Dictionary<string, object> : null;
                var on = kv.Value as Dictionary<string, object>;
                outD[kv.Key] = (bn != null && on != null) ? DeepMerge(bn, on) : kv.Value;
            }
            return outD;
        }

        // "face.pupil.id" + 3 -> {"face":{"pupil":{"id":3}}}
        private static Dictionary<string, object> PathToDict(string path, object value)
        {
            string[] parts = path.Split('.');
            var root = new Dictionary<string, object>();
            Dictionary<string, object> cur = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var nxt = new Dictionary<string, object>();
                cur[parts[i]] = nxt;
                cur = nxt;
            }
            cur[parts[parts.Length - 1]] = value;
            return root;
        }

        // {"face":{"pupil":{"id":3}}} -> "3"（候选值本身，比整棵嵌套对象好读）
        private static string LeafOf(Dictionary<string, object> cand)
        {
            if (cand == null) return "";
            object cur = cand;
            for (int i = 0; i < 16; i++)
            {
                var d = cur as Dictionary<string, object>;
                if (d == null || d.Count != 1) break;
                object only = null;
                foreach (var kv in d) { only = kv.Value; break; }
                if (only is Dictionary<string, object>) { cur = only; continue; }
                return LeafText(only);
            }
            return Json.Write(cand);
        }

        private static string LeafText(object v)
        {
            if (v == null) return "null";
            if (v is string) return (string)v;
            if (v is bool) return ((bool)v) ? "true" : "false";
            if (v is double)
            {
                double d = (double)v;
                return d == Math.Floor(d) ? ((long)d).ToString() : d.ToString("0.####");
            }
            return Json.Write(v);
        }

        private static void WriteImg(Img im, string path)
        {
            if (im == null || im.Px == null || im.W <= 0 || im.H <= 0) return;
            Texture2D t = ToTexture(im.Px, im.W, im.H);
            try { File.WriteAllBytes(path, t.EncodeToPNG()); } finally { Destroy(t); }
        }

        // 把若干张等尺寸的候选图横排成一张接触印相，供 AI 直接看图判断（顺序 = 传入顺序）
        private static string MontageToFile(List<Img> cells, string path)
        {
            if (cells == null || cells.Count == 0) return null;
            int cw = cells[0].W, ch = cells[0].H, gap = 6;
            int rowW = cw * cells.Count + gap * (cells.Count - 1);
            Color32[] sheetPx = new Color32[rowW * ch];
            for (int i = 0; i < sheetPx.Length; i++) sheetPx[i] = new Color32(23, 28, 41, 255);
            for (int c = 0; c < cells.Count; c++)
                BlitNearest(cells[c].Px, cells[c].W, 0, 0, cw, ch, sheetPx, rowW, c * (cw + gap), 0, cw, ch);
            Texture2D t = ToTexture(sheetPx, rowW, ch);
            try { File.WriteAllBytes(path, t.EncodeToPNG()); return path; }
            catch (Exception) { return null; }
            finally { Destroy(t); }
        }

        // 从某个分类里取 id 列表（自动生成候选用）
        private object OptionIds(string query)
        {
            string cat = QueryValue(query, "category");
            if (string.IsNullOrEmpty(cat)) throw new Exception("用法: /options_ids?category=eye");
            int catNo = -1;
            for (int i = 0; i < OptionNames.Length; i++) if (OptionNames[i] == cat) catNo = OptionCats[i];
            if (catNo < 0) throw new Exception("没有这个分类: " + cat);
            return RunOnMain(delegate
            {
                var ids = new List<object>();
                try
                {
                    ChaListControl ctl = Manager.Character.chaListCtrl;
                    Dictionary<int, ListInfoBase> info = ctl.GetCategoryInfo((ChaListDefine.CategoryNo)catNo);
                    if (info != null)
                    {
                        List<int> keys = new List<int>(info.Keys);
                        keys.Sort();
                        foreach (int k in keys)
                        {
                            ListInfoBase lb = info[k];
                            ids.Add(Json.Obj("id", k, "name", lb != null && lb.Name != null ? lb.Name.TrimEnd('\u180e') : ""));
                        }
                    }
                }
                catch (Exception e) { throw new Exception("读取分类失败: " + e.Message); }
                return Json.Obj("ok", true, "category", cat, "count", ids.Count, "ids", ids);
            });
        }

        private object FitReload(Dictionary<string, object> req)
        {
            string refName = Str(req, "ref", null);
            if (string.IsNullOrEmpty(refName)) throw new Exception("mode=reload 需要 ref（参考图名）");
            double? pIdx = Num(req, "panel");
            int panel = pIdx.HasValue ? (int)pIdx.Value : 0;
            string view = Str(req, "view", "front");
            string framing = Str(req, "framing", "full");
            string name = Str(req, "name", null);
            double? topKd = Num(req, "top_k");
            int topK = topKd.HasValue ? Mathf.Clamp((int)topKd.Value, 1, 20) : 5;
            double? budgetd = Num(req, "budget_ms");
            int budgetMs = budgetd.HasValue ? Mathf.Clamp((int)budgetd.Value, 5000, 600000) : 120000;

            // base_params：与 generate 同构的参数（不含 save_as/load）
            var baseParams = req.ContainsKey("base_params") ? req["base_params"] as Dictionary<string, object> : null;
            if (baseParams == null && req.ContainsKey("base"))
                baseParams = Json.Obj("base", req["base"]);
            if (baseParams == null) throw new Exception("mode=reload 需要 base_params（与 generate 同构）");
            if (!baseParams.ContainsKey("base")) baseParams["base"] = Str(req, "base", "お嬢様.png");
            baseParams["sex"] = Str(baseParams, "sex", Str(req, "sex", "female"));
            baseParams.Remove("save_as");
            baseParams.Remove("load");

            // 候选：显式 candidates（每个是覆盖用的部分参数），或 auto_candidates 自动从分类取
            var cands = new List<Dictionary<string, object>>();
            object co;
            if (req.TryGetValue("candidates", out co) && co is List<object>)
                foreach (object o in (List<object>)co)
                {
                    var d = o as Dictionary<string, object>;
                    if (d != null) cands.Add(d);
                }
            object ao;
            if (cands.Count == 0 && req.TryGetValue("auto_candidates", out ao))
            {
                var ad = ao as Dictionary<string, object>;
                if (ad == null) throw new Exception("auto_candidates 需要对象");
                string cat = Str(ad, "category", null);
                string path = Str(ad, "path", null);
                if (string.IsNullOrEmpty(cat) || string.IsNullOrEmpty(path))
                    throw new Exception("auto_candidates 需要 {category, path}");
                double? lim = Num(ad, "limit");
                double? st = Num(ad, "start");
                int limit = lim.HasValue ? Mathf.Clamp((int)lim.Value, 1, 60) : 12;
                int start = st.HasValue ? Mathf.Max(0, (int)st.Value) : 0;

                int catNo = -1;
                for (int i = 0; i < OptionNames.Length; i++) if (OptionNames[i] == cat) catNo = OptionCats[i];
                if (catNo < 0) throw new Exception("没有这个分类: " + cat);

                var ids = new List<int>();
                try
                {
                    ChaListControl ctl = Manager.Character.chaListCtrl;
                    Dictionary<int, ListInfoBase> info = ctl.GetCategoryInfo((ChaListDefine.CategoryNo)catNo);
                    if (info != null)
                    {
                        List<int> keys = new List<int>(info.Keys);
                        keys.Sort();
                        for (int i = start; i < keys.Count && ids.Count < limit; i++) ids.Add(keys[i]);
                    }
                }
                catch (Exception e) { throw new Exception("读取分类失败: " + e.Message); }
                foreach (int id in ids) cands.Add(PathToDict(path, (object)(double)id));
                if (cands.Count == 0) throw new Exception("分类 " + cat + " 里没有可用的 id");
            }
            if (cands.Count == 0) throw new Exception("没有候选：给 candidates 或 auto_candidates");

            var fw = req.ContainsKey("weights") ? req["weights"] as Dictionary<string, object> : null;
            bool saveShots = Bool(req, "save_shots", false);
            var tcs = new TaskCompletionSource<object>();
            _mainThread.Enqueue(delegate
            {
                try
                {
                    StartCoroutine(FitReloadCoroutine(refName, panel, view, framing, name, baseParams, cands,
                        topK, budgetMs, Str(req, "ref_crop", "full"), fw, saveShots, tcs));
                }
                catch (Exception e) { tcs.SetException(e); }
            });
            if (!tcs.Task.Wait(600000)) throw new Exception("搜索超时：主线程 600 秒未完成");
            if (tcs.Task.IsFaulted) throw tcs.Task.Exception.InnerException ?? tcs.Task.Exception;
            return tcs.Task.Result;
        }

        private IEnumerator FitReloadCoroutine(string refName, int panel, string view, string framing, string name,
            Dictionary<string, object> baseParams, List<Dictionary<string, object>> cands, int topK, int budgetMs,
            string refCrop, Dictionary<string, object> weights, bool saveShots, TaskCompletionSource<object> tcs)
        {
            Camera mainCam = Camera.main;
            if (mainCam == null) { tcs.SetException(new Exception("没有可用的主相机")); yield break; }

            Studio.Studio studio = null;
            try { studio = Singleton<Studio.Studio>.Instance; } catch (Exception) { }
            if (studio == null) { tcs.SetException(new Exception("mode=reload 只能在工作室里跑")); yield break; }

            Img refIm;
            try { refIm = CropRegion(LoadRefPanel(refName, panel), refCrop); }
            catch (Exception e) { tcs.SetException(e); yield break; }   // 参考图缺失要立刻报错，不能干等 300 秒超时
            bool[] refN = Normalize(refIm);
            int[] refHist = Hist(refIm);
            int[] refBands = BandColors(refIm, 3);

            string dir = Path.Combine(GameRoot, "UserData/chara/female");
            var board = new List<object>();
            var temps = new List<string>();
            var shotImgs = new Dictionary<int, Img>();
            string shotDir = null;
            if (saveShots)
            {
                shotDir = Path.Combine(GameRoot, "UserData/AICharBridge");
                if (!Directory.Exists(shotDir)) Directory.CreateDirectory(shotDir);
            }
            var sw = System.Diagnostics.Stopwatch.StartNew();
            int tested = 0;
            string prevSubjectName = name;

            // 循环体含 yield，不能放 try/catch（C# 限制）；try/finally 兜底：
            // 异常散出时清理 __fit_tmp_* 临时卡，别让它们残留在 chara/female 里
            try
            {
            for (int i = 0; i < cands.Count; i++)
            {
                if (sw.ElapsedMilliseconds > budgetMs) break;
                var full = DeepMerge(baseParams, cands[i]);
                string tempName = "__fit_tmp_" + i;
                full["save_as"] = tempName;
                full["overwrite"] = true;
                full["load"] = false;

                string tempPath = null;
                string genErr = null;
                try
                {
                    var g = GenerateOnMain(full) as Dictionary<string, object>;
                    if (g != null && g.ContainsKey("saved")) tempPath = (string)g["saved"];
                    else genErr = "生成返回为空";
                }
                catch (Exception e) { genErr = e.Message; }
                if (tempPath == null)
                {
                    board.Add(Json.Obj("iter", i + 1, "candidate", Json.Write(cands[i]), "error", genErr));
                    continue;
                }
                temps.Add(tempPath);

                // 替换场景里的目标角色（只删我们迭代的这个，不动别人的）
                int before = StudioCharacterCount();
                yield return StartCoroutine(ReplaceSubject(studio, tempPath, prevSubjectName));
                int after = StudioCharacterCount();
                var subj = FindStudioCharByName(prevSubjectName);
                if (subj == null) subj = FindStudioChar(null, out prevSubjectName);
                if (subj == null)
                {
                    board.Add(Json.Obj("iter", i + 1, "candidate", Json.Write(cands[i]), "error", "加载后找不到角色"));
                    continue;
                }

                var box = new ShotBox();
                yield return StartCoroutine(ShotSettledSafe(mainCam, subj, view, framing, 288, 384, box));
                if (box.Error != null)
                {
                    board.Add(Json.Obj("iter", i + 1, "candidate", Json.Write(cands[i]),
                        "error", "渲染异常: " + box.Error.Message));
                    continue;
                }
                Img mine = box.Img;
                if (mine == null)
                {
                    board.Add(Json.Obj("iter", i + 1, "candidate", Json.Write(cands[i]), "error", "渲染失败"));
                    continue;
                }

                float iouCore = Iou(refN, Normalize(mine), 0.5f);
                float iouFull = Iou(refN, Normalize(mine), 0f);
                float histSim = HistSim(refHist, Hist(mine));
                float bandSim = BandSim(refBands, BandColors(mine, 3));
                float appSim = AppSim(refIm, mine);
                float total = FitTotal(weights, iouCore, iouFull, histSim, bandSim, appSim);
                tested++;
                string leaf = LeafOf(cands[i]);
                string shotPath = null;
                if (shotDir != null)
                {
                    shotPath = Path.Combine(shotDir, "fit_" + (i + 1).ToString("00") + "_" + SanitizeFileName(leaf) + ".png");
                    try { WriteImg(mine, shotPath); shotImgs[i + 1] = mine; }
                    catch (Exception) { shotPath = null; }
                }
                board.Add(Json.Obj("iter", i + 1, "candidate", Json.Write(cands[i]), "candidate_leaf", leaf,
                    "shot", shotPath, "frozen", box.Frozen,
                    "total", (double)total, "iou_core", (double)iouCore, "iou_full", (double)iouFull,
                    "color_hist", (double)histSim, "band_color", (double)bandSim, "appearance", (double)appSim,
                    "ms", sw.ElapsedMilliseconds, "subjects_before", before, "subjects_after", after));
            }

            board.Sort(delegate (object a, object b)
            {
                double da = ((Dictionary<string, object>)a).ContainsKey("total") ? System.Convert.ToDouble(((Dictionary<string, object>)a)["total"]) : -1;
                double db = ((Dictionary<string, object>)b).ContainsKey("total") ? System.Convert.ToDouble(((Dictionary<string, object>)b)["total"]) : -1;
                return db.CompareTo(da);
            });
            var top = board.GetRange(0, Math.Min(topK, board.Count));

            // 候选接触印相（contact sheet）：按得分从左到右，让 AI 直接看图判断，不用只信数字
            string boardPng = null;
            var boardOrder = new List<object>();
            if (shotDir != null)
            {
                var cells = new List<Img>();
                for (int i = 0; i < top.Count; i++)
                {
                    var e = top[i] as Dictionary<string, object>;
                    if (e == null) continue;
                    object ito;
                    if (!e.TryGetValue("iter", out ito) || ito == null) continue;
                    int itv;
                    try { itv = System.Convert.ToInt32(ito); } catch (Exception) { continue; }
                    object lf;
                    string leafTxt = e.TryGetValue("candidate_leaf", out lf) && lf is string ? (string)lf : itv.ToString();
                    boardOrder.Add(leafTxt + ":" + (e.ContainsKey("total") ? System.Convert.ToDouble(e["total"]).ToString("0.000") : "-"));
                    if (shotImgs.ContainsKey(itv)) cells.Add(shotImgs[itv]);
                }
                if (cells.Count > 0)
                    boardPng = MontageToFile(cells, Path.Combine(shotDir, "fit_board.png"));
            }

            // 结束时把最优重新写一张正式卡并留在场景里（循环停下时停在最后一轮，不是最好那轮）
            string bestCard = null;
            if (top.Count > 0 && ((Dictionary<string, object>)top[0]).ContainsKey("total"))
            {
                var bestCand = Json.Parse((string)((Dictionary<string, object>)top[0])["candidate"]) as Dictionary<string, object>;
                var full = DeepMerge(baseParams, bestCand);
                string saveAs = Str(baseParams, "save_as", null);
                if (string.IsNullOrEmpty(saveAs)) saveAs = "AI-fit-best";
                full["save_as"] = saveAs;
                full["overwrite"] = true;
                full["load"] = false;
                try
                {
                    var g = GenerateOnMain(full) as Dictionary<string, object>;
                    if (g != null && g.ContainsKey("saved")) bestCard = (string)g["saved"];
                }
                catch (Exception) { }
                if (bestCard != null)
                    yield return StartCoroutine(ReplaceSubject(studio, bestCard, prevSubjectName));
            }

            foreach (string t in temps)
            {
                try { if (File.Exists(t) && t != bestCard) File.Delete(t); } catch (Exception) { }
            }

            AddActivity("√ reload 搜索：" + tested + " 个候选 / " + sw.ElapsedMilliseconds + " ms，最优已写回 " +
                (bestCard != null ? Path.GetFileName(bestCard) : "无"));

                tcs.TrySetResult(Json.Obj("ok", true, "mode", "reload", "ref", refName, "panel", panel, "view", view,
                    "tested", tested, "elapsed_ms", sw.ElapsedMilliseconds, "budget_ms", budgetMs,
                    "top", top, "board", board, "best_card", bestCard,
                    "board_png", boardPng, "board_order", boardOrder,
                    "note", "每轮写临时卡并替换场景角色；结束时最优候选已写成正式卡并留在场景里。save_shots=true 时每个候选留一张 PNG，board_png 是接触印相（左→右 = 得分高→低）"));
            }
            finally
            {
                // tcs 已完成 = 正常结束（上面已清过临时卡）；未完成 = 异常散出，这里兜底：
                // 清临时卡 + 把异常落回 tcs（否则调用方只能等 300 秒超时）
                if (!tcs.Task.IsCompleted)
                {
                    foreach (string t in temps)
                    {
                        try { if (File.Exists(t)) File.Delete(t); } catch (Exception) { }
                    }
                    AddActivity("! reload 搜索中断，已清理临时卡（场景角色停在最后的候选状态）");
                    tcs.TrySetException(new Exception("reload 搜索协程异常中断（已清理临时卡，根因见 BepInEx 日志）"));
                }
            }
        }

        private static ChaControl FindStudioCharByName(string name)
        {
            string found;
            return Instance.FindStudioChar(name, out found);
        }

        // 删掉场景里的目标角色，再加载新卡（只动目标，不碰其他对象）
        private IEnumerator ReplaceSubject(Studio.Studio studio, string cardPath, string subjectName)
        {
            var toDelete = new List<Studio.ObjectInfo>();
            if (studio != null && studio.dicObjectCtrl != null)
            {
                foreach (Studio.ObjectCtrlInfo info in new List<Studio.ObjectCtrlInfo>(studio.dicObjectCtrl.Values))
                {
                    var oci = info as Studio.OCIChar;
                    if (oci == null) continue;
                    if (string.IsNullOrEmpty(subjectName))
                    {
                        toDelete.Add(oci.objectInfo);
                        continue;
                    }
                    ChaControl cc = GetChaControl(oci);
                    string cn = cc != null && cc.chaFile != null && cc.chaFile.parameter != null ? (cc.chaFile.parameter.fullname ?? "") : "";
                    if (cn.IndexOf(subjectName, StringComparison.Ordinal) >= 0) toDelete.Add(oci.objectInfo);
                }
            }
            foreach (Studio.ObjectInfo oi in toDelete)
            {
                try { Studio.Studio.DeleteInfo(oi, true); }
                catch (Exception e) { AddActivity("! DeleteInfo 失败: " + e.Message); }
            }
            // 兜底：树节点也删掉（有些版本只删了对象、节点还在，或反之）
            var remain = new List<Studio.ObjectCtrlInfo>();
            if (studio != null && studio.dicObjectCtrl != null)
                foreach (Studio.ObjectCtrlInfo ci in studio.dicObjectCtrl.Values) remain.Add(ci);
            foreach (Studio.ObjectCtrlInfo info in remain)
            {
                var oci = info as Studio.OCIChar;
                if (oci == null || oci.treeNodeObject == null) continue;
                if (toDelete.Contains(oci.objectInfo))
                {
                    try { Studio.Studio.DeleteNode(oci.treeNodeObject); }
                    catch (Exception e) { AddActivity("! DeleteNode 失败: " + e.Message); }
                }
            }
            yield return new WaitForSecondsRealtime(0.4f);
            try { studio.AddFemale(cardPath); } catch (Exception e) { AddActivity("! 加载失败: " + e.Message); }
            yield return null;
        }

        // ================= /selftest：能力锁 + 端到端自测 =================
        private object SelfTest()
        {
            var checks = new List<object>();
            bool ok = true;
            Action<string, bool, string> add = delegate (string nm, bool pass, string detail)
            {
                checks.Add(Json.Obj("name", nm, "ok", pass, "detail", detail));
                if (!pass) ok = false;
            };

            // 能力锁：真的调一遍分发表（GET 期望 200；POST 期望"存在但参数不合法"而不是 404）。
            // 这样只要有人删/改坏某个端点，自测立刻变红。
            var bad = new List<object>();
            foreach (string r in RouteManifest)
            {
                int code = HandleRequest("PROBE", r, "", "").Code;
                if (code != 200) bad.Add(r + "→" + code);
            }
            add("routes_dispatch", bad.Count == 0,
                bad.Count == 0 ? RouteManifest.Length + " 个端点在册（PROBE 校验，零副作用）" : "缺失: " + string.Join(",", bad.ToArray()));

            // 关键实现是否还在（防止重构时被删）
            string[] mustHave = new string[] { "Silhouette", "SilhouetteFlood", "Normalize", "Iou", "BandColors",
                "OffscreenShot", "SplitPanels", "PanelSignature",
                "CropRegion", "FreezeDynamics", "MontageToFile", "DiscoverShapeTables",
                "Audit", "Txn", "EnsureDressed", "ClearScene", "StudioCharCount" };
            string src = "";
            try { src = File.ReadAllText(typeof(AICharBridge).Assembly.Location); } catch (Exception) { }
            var lostImpl = new List<object>();
            foreach (string m in mustHave)
                if (!src.Contains(m)) lostImpl.Add(m);
            add("impl_present", lostImpl.Count == 0, lostImpl.Count == 0 ? mustHave.Length + " 个核心实现齐全" : "缺失: " + string.Join(",", lostImpl.ToArray()));

            // 端到端：渲染一帧 + 量一次剪影
            try
            {
                object r = RunOnMain(delegate
                {
                    Camera cam = Camera.main;
                    if (cam == null) throw new Exception("没有主相机");
                    string tn;
                    ChaControl cc = FindStudioChar(null, out tn);
                    if (cc == null) return Json.Obj("subject", false);
                    Img im = OffscreenShot(cam, cc, "front", "full", 288, 384);
                    return Json.Obj("subject", true, "coverage", (double)im.Coverage,
                        "bbox_w", im.X1 - im.X0 + 1, "bbox_h", im.Y1 - im.Y0 + 1);
                }, 30000);
                var d = r as Dictionary<string, object>;
                bool hasSubj = d != null && System.Convert.ToBoolean(d["subject"]);
                if (hasSubj)
                {
                    double cov = System.Convert.ToDouble(d["coverage"]);
                    add("offscreen_measure", cov > 0.02, "剪影覆盖率 " + cov.ToString("0.000") + "（>0.02 视为有效）");
                }
                else add("offscreen_measure", true, "场景无角色，跳过（切到工作室并加角色后再测）");
            }
            catch (Exception e) { add("offscreen_measure", false, e.Message); }

            return Json.Obj("kind", ok ? "ok" : "fail", "ok", ok, "checks", checks,
                "tool_count_expected", 16, "route_count", RouteManifest.Length,
                "note", "能力锁：端点/核心实现缺失即 fail，防止接口悄悄退化");
        }

        // ============ /doctor：唯一验收标准（kind=ok）+ 构建溯源 ============
        // 参考 dsh-blender-plugin：安装/可用与否只看 doctor 的结果，并报告"跑的是不是当前这份构建"
        private object Doctor()
        {
            var checks = new List<object>();
            var hardFail = new List<string>();
            var prov = new Dictionary<string, object>();

            // 构建溯源
            try
            {
                string dll = typeof(AICharBridge).Assembly.Location;
                prov["assembly"] = dll;
                if (File.Exists(dll))
                {
                    FileInfo fi = new FileInfo(dll);
                    prov["size_bytes"] = fi.Length;
                    prov["built_at"] = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
                    prov["sha256"] = Sha256File(dll);
                }
                else
                {
                    checks.Add(Json.Obj("name", "assembly_present", "ok", false, "detail", "找不到程序集文件: " + dll, "hard", true));
                    hardFail.Add("assembly_present");
                }
                prov["pid"] = System.Diagnostics.Process.GetCurrentProcess().Id;
                prov["unity"] = Application.unityVersion;
                prov["plugin"] = "AICharBridge 1.0.0";
            }
            catch (Exception e)
            {
                checks.Add(Json.Obj("name", "assembly_present", "ok", false, "detail", e.Message, "hard", true));
                hardFail.Add("assembly_present");
            }

            // 清单与场景（主线程）
            try
            {
                object r = RunOnMain(delegate
                {
                    var res = new Dictionary<string, object>();
                    res["top_count"] = CountInCategory(105);
                    res["hair_count"] = CountInCategory(101);
                    int subj = 0;
                    try { subj = StudioCharacterCount(); } catch (Exception) { }
                    res["subject_count"] = subj;
                    res["scene"] = SceneManager.GetActiveScene().name;
                    res["in_studio"] = Singleton<Studio.Studio>.Instance != null;
                    return res;
                }, 20000);
                var d = r as Dictionary<string, object>;
                int topCount = System.Convert.ToInt32(d["top_count"]);
                bool listOk = topCount > 0;
                checks.Add(Json.Obj("name", "chara_list_loaded", "ok", listOk, "detail", "服装列表 " + topCount + " 项", "hard", true));
                if (!listOk) hardFail.Add("chara_list_loaded");
                int subjN = System.Convert.ToInt32(d["subject_count"]);
                checks.Add(Json.Obj("name", "subject_present", "ok", subjN > 0, "detail", "场景内角色 " + subjN + " 个（空场景属正常）", "hard", false));
                prov["scene"] = d["scene"];
                prov["in_studio"] = d["in_studio"];
            }
            catch (Exception e)
            {
                checks.Add(Json.Obj("name", "chara_list_loaded", "ok", false, "detail", e.Message, "hard", true));
                hardFail.Add("chara_list_loaded");
            }

            // 卡片目录可写
            try
            {
                string probe = Path.Combine(GameRoot, "UserData/chara/female/_doctor_probe.tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                checks.Add(Json.Obj("name", "cards_dir_writable", "ok", true, "detail", "UserData/chara/female 可写", "hard", true));
            }
            catch (Exception e)
            {
                checks.Add(Json.Obj("name", "cards_dir_writable", "ok", false, "detail", e.Message, "hard", true));
                hardFail.Add("cards_dir_writable");
            }

            // 预设卡可见
            try
            {
                string pdir = Path.Combine(GameRoot, "DefaultData/chara/female");
                int n = Directory.Exists(pdir) ? Directory.GetFiles(pdir, "*.png").Length : 0;
                bool okk = n > 0;
                checks.Add(Json.Obj("name", "presets_visible", "ok", okk, "detail", "官方预设卡 " + n + " 张", "hard", true));
                if (!okk) hardFail.Add("presets_visible");
            }
            catch (Exception e)
            {
                checks.Add(Json.Obj("name", "presets_visible", "ok", false, "detail", e.Message, "hard", true));
                hardFail.Add("presets_visible");
            }

            // 端到端：真的渲染一帧出来
            try
            {
                string dir = Path.Combine(GameRoot, "UserData/AICharBridge");
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                string probe = Path.Combine(dir, "_doctor_probe.png");
                var req = new Dictionary<string, object>();
                var tcs = new TaskCompletionSource<object>();
                _mainThread.Enqueue(delegate
                {
                    try { StartCoroutine(CaptureCoroutine(req, new string[] { "front" }, "full", null, 300, probe, true, tcs)); }
                    catch (Exception ex) { tcs.SetException(ex); }
                });
                if (!tcs.Task.Wait(60000)) throw new Exception("截图检查超时（主线程无响应）");
                if (tcs.Task.IsFaulted) throw tcs.Task.Exception.InnerException ?? tcs.Task.Exception;
                var cap = tcs.Task.Result as Dictionary<string, object>;
                FileInfo fi = new FileInfo(probe);
                bool capOk = fi.Exists && fi.Length > 1000;
                checks.Add(Json.Obj("name", "capture", "ok", capOk, "detail", "渲染出 " + (fi.Exists ? fi.Length : 0) + " 字节", "hard", true));
                if (!capOk) hardFail.Add("capture");
                object pn;
                if (cap != null && cap.TryGetValue("panels", out pn))
                {
                    var lst = pn as List<object>;
                    if (lst != null && lst.Count > 0)
                    {
                        var p0 = lst[0] as Dictionary<string, object>;
                        object cov;
                        if (p0 != null && p0.TryGetValue("coverage", out cov)) prov["capture_coverage"] = cov;
                    }
                }
                try { File.Delete(probe); } catch (Exception) { }
            }
            catch (Exception e)
            {
                checks.Add(Json.Obj("name", "capture", "ok", false, "detail", e.Message, "hard", true));
                hardFail.Add("capture");
            }

            bool ok = hardFail.Count == 0;
            var cfg = Json.Obj("port", _port.Value, "game_root", GameRoot,
                "hud", _hud.Value, "hud_key", _hudKey.Value.ToString(),
                "char_dir", Path.Combine(GameRoot, "UserData/chara/female"),
                "capture_dir", Path.Combine(GameRoot, "UserData/AICharBridge"));

            return Json.Obj("kind", ok ? "ok" : "fail", "ok", ok,
                "checks", checks, "provenance", prov, "config", cfg,
                "failed", hardFail.ToArray(),
                "note", ok ? "通道正常（kind=ok 即可用）" : "有硬性检查未通过，见 failed");
        }

        private static int StudioCharacterCount()
        {
            Studio.Studio studio = null;
            try { studio = Singleton<Studio.Studio>.Instance; } catch (Exception) { }
            if (studio == null || studio.dicObjectCtrl == null) return 0;
            int n = 0;
            foreach (Studio.ObjectCtrlInfo info in studio.dicObjectCtrl.Values)
                if (info is Studio.OCIChar) n++;
            return n;
        }

        // face.shape: {"ChinW":0.5,"FaceLowW":0.45} —— 按名字写面部滑条。
        // 这个曾经**完全没实现**：Applier 的 Apply 会跳过 "shape" 键（交给专门的函数），
        // 但只写了 body 的专门函数，于是 face.shape 被无声丢弃（applied/skipped 里都不出现）。
        // 结果就是"下巴没法调"——基座卡把 FaceLowW/ChinTipW 拉到 0，尖下巴无从修正。
        private static int FaceShapeIndex(string name)
        {
            if (string.IsNullOrEmpty(name)) return -1;
            for (int i = 0; i < FaceShapeNames.Length; i++)
                if (string.Equals(FaceShapeNames[i], name, StringComparison.OrdinalIgnoreCase)) return i;
            return -1;
        }

        private static void ApplyFaceShape(Applier applier, Dictionary<string, object> req, ChaFileFace face)
        {
            if (face == null || !req.ContainsKey("face")) return;
            var fd = req["face"] as Dictionary<string, object>;
            if (fd == null) return;
            object so;
            if (!fd.TryGetValue("shape", out so)) return;
            var shape = so as Dictionary<string, object>;
            if (shape == null) return;
            if (face.shapeValueFace == null) face.shapeValueFace = new float[52];
            foreach (KeyValuePair<string, object> kv in shape)
            {
                int idx = FaceShapeIndex(kv.Key);
                if (idx < 0)
                {
                    // 大小写不一致时给个相近提示，别只丢一句"没这个滑条"
                    string hint = null;
                    for (int i = 0; i < FaceShapeNames.Length; i++)
                        if (string.Equals(FaceShapeNames[i], kv.Key, StringComparison.OrdinalIgnoreCase)) { hint = FaceShapeNames[i]; break; }
                    applier.Skip("face.shape." + kv.Key, hint != null ? ("是不是想写 " + hint + "？") : "没有这个面部滑条");
                    continue;
                }
                if (idx >= face.shapeValueFace.Length) { applier.Skip("face.shape." + kv.Key, "超出数组长度"); continue; }
                if (!(kv.Value is double)) { applier.Skip("face.shape." + kv.Key, "需要数字"); continue; }
                float v = Mathf.Clamp01((float)(double)kv.Value);
                face.shapeValueFace[idx] = v;
                applier.Note("face.shape." + kv.Key, (double)v);
            }
        }

        // body.shape: {"BustSize":0.3,"ThighUpW":0.4} —— 按名字写滑条，避免让 AI 数数组下标
        private static void ApplyBodyShape(Applier applier, Dictionary<string, object> req, ChaFileBody body)
        {
            if (body == null || !req.ContainsKey("body")) return;
            var bd = req["body"] as Dictionary<string, object>;
            if (bd == null) return;
            object so;
            if (!bd.TryGetValue("shape", out so)) return;
            var shape = so as Dictionary<string, object>;
            if (shape == null) return;
            if (body.shapeValueBody == null) body.shapeValueBody = new float[44];
            foreach (KeyValuePair<string, object> kv in shape)
            {
                int idx = BodyShapeIndex(kv.Key);
                if (idx < 0) { applier.Skip("body.shape." + kv.Key, "没有这个滑条（可用 kks_help params 查名字）"); continue; }
                if (idx >= body.shapeValueBody.Length) { applier.Skip("body.shape." + kv.Key, "超出数组长度"); continue; }
                if (!(kv.Value is double)) { applier.Skip("body.shape." + kv.Key, "需要数字"); continue; }
                float v = Mathf.Clamp01((float)(double)kv.Value);
                body.shapeValueBody[idx] = v;
                applier.Note("body.shape." + kv.Key, (double)v);
                if ((v < 0.05f || v > 0.95f) && IsClipProne(kv.Key))
                    applier.Skip("body.shape." + kv.Key, "极端值且该滑条容易把身体顶出衣服，留意穿模");
            }
        }

        // body.normalize（默认开）：把继承来的"易穿模"滑条拉回安全带。
        // 乳晕/乳头是贴着衣服往外顶的几何体，穿着衣服时即使中等值也会穿透，所以单独压平。
        private static void NormalizeBodyShape(Applier applier, Dictionary<string, object> req, ChaFileBody body)
        {
            if (body == null || body.shapeValueBody == null) return;
            bool normalize = true;
            object bo;
            if (req.TryGetValue("body", out bo))
            {
                var bd = bo as Dictionary<string, object>;
                object no;
                if (bd != null && bd.TryGetValue("normalize", out no) && no is bool) normalize = (bool)no;
            }
            if (!normalize) { applier.Note("body.normalize", "关闭（保留基座卡体型）"); return; }

            const float lo = 0.15f, hi = 0.85f;
            const float legLo = 0.32f, legHi = 0.80f;   // 腿更敏感：袜子是独立网格，腿型太极端会顶出来
            var changed = new List<object>();
            for (int i = 0; i < body.shapeValueBody.Length && i < BodyShapeNames.Length; i++)
            {
                if (!IsClipProne(BodyShapeNames[i])) continue;
                string nm = BodyShapeNames[i];
                float v = body.shapeValueBody[i];
                float c;
                if (nm == "AreolaBulge" || nm == "NipWeight" || nm == "NipStand") c = Mathf.Clamp(v, 0f, 0.12f);
                else if (IsLegShape(nm)) c = Mathf.Clamp(v, legLo, legHi);
                else c = Mathf.Clamp(v, lo, hi);
                if (Mathf.Abs(c - v) > 0.001f)
                {
                    body.shapeValueBody[i] = c;
                    changed.Add(Json.Obj("shape", nm, "from", (double)v, "to", (double)c));
                }
            }
            if (body.areolaSize > 0.65f) { body.areolaSize = 0.5f; changed.Add(Json.Obj("shape", "areolaSize", "to", 0.5)); }
            if (body.nipGlossPower > 0.6f) body.nipGlossPower = 0.4f;
            applier.Note("body.normalize", changed.Count == 0 ? "无需调整" : ("已拉回安全带 " + changed.Count + " 项"));
            if (changed.Count > 0) applier.Note("body.normalize.detail", Json.Write(changed));
        }

        // smooth_skin: 一键关掉会让皮肤显得"不平整/有肌肉感"的细节层
        private static void ApplySmoothSkin(Applier applier, Dictionary<string, object> req, ChaFileBody body, ChaFileFace face)
        {
            if (face != null)
            {
                face.detailPower = 0f;                        // 面部细节贴图：高了会显脏/不平
                face.cheekGlossPower = Mathf.Clamp(face.cheekGlossPower, 0.1f, 0.4f);
                face.lipGlossPower = Mathf.Clamp(face.lipGlossPower, 0.2f, 0.6f);
            }
            if (body == null) return;
            if (!Bool(req, "smooth_skin", false)) return;
            body.detailPower = 0f;
            body.drawAddLine = false;
            body.skinGlossPower = Mathf.Clamp(body.skinGlossPower, 0.15f, 0.5f);
            if (body.paintId != null) for (int i = 0; i < body.paintId.Length; i++) body.paintId[i] = 0;
            body.sunburnId = 0;
            body.sunburnColor = new Color(1f, 1f, 1f, 1f);
            if (body.paintColor != null) for (int i = 0; i < body.paintColor.Length; i++) body.paintColor[i] = new Color(1f, 1f, 1f, 1f);
            applier.Note("smooth_skin", "已关闭 detailPower/drawAddLine/paint/sunburn，并收敛光泽度");
        }

        // ================= 工具 ============
        private static string Str(Dictionary<string, object> d, string key, string def)
        {
            if (d == null) return def;
            object v;
            if (d.TryGetValue(key, out v) && v is string) return (string)v;
            return def;
        }

        private static bool Bool(Dictionary<string, object> d, string key, bool def)
        {
            if (d == null) return def;
            object v;
            if (d.TryGetValue(key, out v))
            {
                if (v is bool) return (bool)v;
                if (v is double) return ((double)v) != 0.0;
            }
            return def;
        }

        private static double? Num(Dictionary<string, object> d, string key)
        {
            if (d == null) return null;
            object v;
            if (d.TryGetValue(key, out v) && v is double) return (double)v;
            return null;
        }

        private static string EnsurePng(string name)
        {
            return name.ToLowerInvariant().EndsWith(".png", StringComparison.Ordinal) ? name : name + ".png";
        }

        // "#RGB" / "#RRGGBB" / "#RRGGBBAA" -> Color，非法值给出可读原因
        internal static Color ParseColorValue(object v)
        {
            string s = v as string;
            if (s == null) throw new Exception("颜色需要 \"#RRGGBB\" 或 \"#RGB\" 格式的字符串，收到: " + Json.Write(v));
            s = s.Trim().TrimStart('#');
            try
            {
                byte r, g, b, a = 255;
                if (s.Length == 3)
                {
                    r = (byte)(System.Convert.ToInt32(new string(s[0], 2), 16));
                    g = (byte)(System.Convert.ToInt32(new string(s[1], 2), 16));
                    b = (byte)(System.Convert.ToInt32(new string(s[2], 2), 16));
                }
                else if (s.Length == 6 || s.Length == 8)
                {
                    r = byte.Parse(s.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    g = byte.Parse(s.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    b = byte.Parse(s.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                    if (s.Length == 8) a = byte.Parse(s.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                }
                else throw new Exception("长度不对（应为 3、6 或 8 位十六进制）");
                return new Color32(r, g, b, a);
            }
            catch (Exception e)
            {
                throw new Exception("颜色 \"" + v + "\" 解析失败: " + e.Message + "，正确写法如 \"#FF9BB5\"");
            }
        }

        // 饰品部位名 -> 语义类型（None=0 Hair=1 Head=2 Face=3 Neck=4 Body=5 Waist=6 Leg=7 Arm=8 Hand=9）
        private static readonly string[] AccessoryTypeNames =
            new string[] { "none", "hair", "head", "face", "neck", "body", "waist", "leg", "arm", "hand", "kokan" };
        private const int AccessoryEmptyType = 120;   // 卡片里 120 表示该槽为空（ao_none）

        private static int AccessoryTypeOf(Dictionary<string, object> d)
        {
            object v;
            if (d == null || !d.TryGetValue("type", out v)) return -1;
            if (v is string)
            {
                string s = ((string)v).Trim().ToLowerInvariant();
                for (int i = 0; i < AccessoryTypeNames.Length; i++)
                    if (AccessoryTypeNames[i] == s) return i;
                int n;
                if (int.TryParse(s, out n)) return n;
                return -1;
            }
            if (v is double) return (int)(double)v;
            return -1;
        }

        private static string SanitizeFileName(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }

        // ================= 参数应用器（反射 + 类型转换） =================
        private class Applier
        {
            internal readonly List<object> Applied = new List<object>();
            internal readonly List<object> Skipped = new List<object>();

            public void Apply(object target, Dictionary<string, object> dict, string prefix)
            {
                foreach (KeyValuePair<string, object> kv in dict)
                {
                    string key = kv.Key;
                    // shape / normalize 由 ApplyBodyShape / NormalizeBodyShape 专门处理
                    if (key == "shape" || key == "normalize") continue;
                    // 服装颜色快捷键 color0/1/2/3 -> colorInfo[i].baseColor
                    if (key.StartsWith("color", StringComparison.Ordinal))
                    {
                        int ci;
                        if (key.Length == 6 && int.TryParse(key.Substring(5), out ci) && ci >= 0 && ci < 4)
                        {
                            object colorInfo = GetMember(target, "colorInfo");
                            if (colorInfo is Array && ((Array)colorInfo).Length > ci)
                            {
                                object ciObj = ((Array)colorInfo).GetValue(ci);
                                if (ApplyMember(ciObj, "baseColor", kv.Value, prefix + "." + key)) continue;
                            }
                        }
                    }
                    // 花纹叠加层：pattern0/1/2/3（0 = 无花纹）与 patternColor0/1/2/3
                    // 衣服上那层"浅色条纹/花色"就是它盖出来的，要纯色必须清掉
                    if (key.StartsWith("pattern", StringComparison.Ordinal))
                    {
                        string rest = key.Substring(7);
                        bool isColor = rest.StartsWith("Color", StringComparison.Ordinal);
                        if (isColor) rest = rest.Substring(5);
                        int ci;
                        if (int.TryParse(rest, out ci) && ci >= 0 && ci < 4)
                        {
                            object colorInfo = GetMember(target, "colorInfo");
                            if (colorInfo is Array && ((Array)colorInfo).Length > ci)
                            {
                                object ciObj = ((Array)colorInfo).GetValue(ci);
                                if (ApplyMember(ciObj, isColor ? "patternColor" : "pattern", kv.Value, prefix + "." + key)) continue;
                            }
                        }
                    }
                    ApplyMember(target, key, kv.Value, prefix);
                }
            }

            public void Apply(object target, Dictionary<string, object> dict)
            {
                Apply(target, dict, "");
            }

            public void Skip(string what, string why)
            {
                Skipped.Add(Json.Obj("item", what, "reason", why));
            }

            public void Note(string what, object value)
            {
                Applied.Add(Json.Obj("item", what, "value", value));
            }

            private bool ApplyMember(object target, string name, object value, string prefix)
            {
                if (target == null || value == null) return false;
                const BindingFlags F = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
                Type t = target.GetType();
                try
                {
                    PropertyInfo p = t.GetProperty(name, F);
                    if (p != null && p.CanWrite)
                    {
                        // 数组类（如 shapeValueFace/Body 滑条）逐元素写入：部分数组也能用，不会把原有数据整段替换掉
                        if (p.PropertyType == typeof(float[]) && value is List<object>)
                        {
                            int written;
                            float[] patched = PatchFloats(GetMember(target, name) as float[], (List<object>)value, out written);
                            p.SetValue(target, patched, null);
                            Applied.Add(Json.Obj("item", prefix + "." + name, "value", written + " 项"));
                            return true;
                        }
                        p.SetValue(target, Convert(value, p.PropertyType), null);
                        Applied.Add(Json.Obj("item", prefix + "." + name, "value", ValueForLog(value)));
                        return true;
                    }
                    // KKS 的 fullname 是只读组合属性（lastname + firstname），自动拆分
                    if (p != null && !p.CanWrite && target is ChaFileParameter && name == "fullname")
                    {
                        string full = (value as string) ?? "";
                        string last = full, first = "";
                        string[] parts = full.Split(new[] { ' ', '\u3000' }, 2); // 半角/全角空格
                        if (parts.Length == 2) { last = parts[0]; first = parts[1]; }
                        SetMember(target, "lastname", last, prefix);
                        SetMember(target, "firstname", first, prefix);
                        Applied.Add(Json.Obj("item", prefix + ".fullname", "value", full, "split", true));
                        return true;
                    }
                    FieldInfo f = t.GetField(name, F);
                    if (f != null)
                    {
                        if (f.FieldType == typeof(float[]) && value is List<object>)
                        {
                            int written;
                            float[] patched = PatchFloats(f.GetValue(target) as float[], (List<object>)value, out written);
                            f.SetValue(target, patched);
                            Applied.Add(Json.Obj("item", prefix + "." + name, "value", written + " 项"));
                            return true;
                        }
                        f.SetValue(target, Convert(value, f.FieldType));
                        Applied.Add(Json.Obj("item", prefix + "." + name, "value", ValueForLog(value)));
                        return true;
                    }
                    Skipped.Add(Json.Obj("item", prefix + "." + name, "reason", p != null ? "属性只读" : "字段不存在"));
                    return false;
                }
                catch (Exception e)
                {
                    Skipped.Add(Json.Obj("item", prefix + "." + name, "reason", e.Message));
                    return false;
                }
            }

            // 把 values 逐项写进 base 的副本；base 为 null 时按 values 长度新建
            private static float[] PatchFloats(float[] baseArr, List<object> values, out int written)
            {
                int n = values.Count;
                float[] result;
                if (baseArr == null) result = new float[n];
                else
                {
                    result = (float[])baseArr.Clone();
                    n = Math.Min(n, result.Length);
                }
                written = 0;
                for (int i = 0; i < n; i++)
                {
                    object o = values[i];
                    if (o == null) continue;
                    result[i] = (float)System.Convert.ToDouble(o, CultureInfo.InvariantCulture);
                    written++;
                }
                if (baseArr != null && values.Count > baseArr.Length)
                    throw new Exception("数组过长：该滑条只有 " + baseArr.Length + " 项，收到 " + values.Count + " 项");
                return result;
            }

            private static object GetMember(object target, string name)
            {
                const BindingFlags F = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
                PropertyInfo p = target.GetType().GetProperty(name, F);
                if (p != null) return p.GetValue(target, null);
                FieldInfo f = target.GetType().GetField(name, F);
                return f == null ? null : f.GetValue(target);
            }

            private bool SetMember(object target, string name, object value, string prefix)
            {
                const BindingFlags F = BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase;
                try
                {
                    PropertyInfo p = target.GetType().GetProperty(name, F);
                    if (p != null && p.CanWrite) { p.SetValue(target, value, null); return true; }
                    FieldInfo f = target.GetType().GetField(name, F);
                    if (f != null) { f.SetValue(target, value); return true; }
                }
                catch (Exception) { }
                Skipped.Add(Json.Obj("item", prefix + "." + name, "reason", "设置失败"));
                return false;
            }

            private object ValueForLog(object v)
            {
                if (v is Dictionary<string, object>) return Json.Write(v);
                if (v is List<object>) return Json.Write(v);
                return v;
            }

            private object Convert(object value, Type type)
            {
                if (type == typeof(float) || type == typeof(double))
                {
                    double d = System.Convert.ToDouble(value, CultureInfo.InvariantCulture);
                    return type == typeof(float) ? (object)(float)d : (object)d;
                }
                if (type == typeof(int)) return (int)Math.Round(System.Convert.ToDouble(value, CultureInfo.InvariantCulture));
                if (type == typeof(byte)) return (byte)Math.Round(System.Convert.ToDouble(value, CultureInfo.InvariantCulture));
                if (type == typeof(bool))
                {
                    if (value is bool) return value;
                    return System.Convert.ToDouble(value) != 0.0;
                }
                if (type == typeof(string)) return value is string ? value : Json.Write(value);
                if (type == typeof(Color)) return ParseColor(value);
                if (type == typeof(float[]))
                {
                    var arr = value as List<object>;
                    if (arr == null) throw new Exception("需要数组");
                    float[] fs = new float[arr.Count];
                    for (int i = 0; i < arr.Count; i++) fs[i] = (float)System.Convert.ToDouble(arr[i], CultureInfo.InvariantCulture);
                    return fs;
                }
                if (type.IsEnum) return Enum.Parse(type, Math.Round(System.Convert.ToDouble(value)).ToString());
                throw new Exception("不支持的目标类型 " + type.Name);
            }

            private Color ParseColor(object v)
            {
                return ParseColorValue(v);
            }
        }

        // ================= 极简 JSON（解析 + 序列化） =================
        public static class Json
        {
            public static Dictionary<string, object> Obj(params object[] kv)
            {
                var d = new Dictionary<string, object>();
                for (int i = 0; i + 1 < kv.Length; i += 2) d[(string)kv[i]] = kv[i + 1];
                return d;
            }

            public static object Parse(string s)
            {
                if (string.IsNullOrEmpty(s)) return null;
                int i = 0;
                return ParseValue(s, ref i);
            }

            private static void SkipWs(string s, ref int i)
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == '\t' || s[i] == '\r' || s[i] == '\n')) i++;
            }

            private static object ParseValue(string s, ref int i)
            {
                SkipWs(s, ref i);
                if (i >= s.Length) throw new Exception("JSON 意外结束");
                char c = s[i];
                if (c == '{')
                {
                    var d = new Dictionary<string, object>();
                    i++;
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    while (true)
                    {
                        SkipWs(s, ref i);
                        string key = ParseString(s, ref i);
                        SkipWs(s, ref i);
                        i++; // ':'
                        d[key] = ParseValue(s, ref i);
                        SkipWs(s, ref i);
                        if (s[i] == ',') { i++; continue; }
                        if (s[i] == '}') { i++; return d; }
                        throw new Exception("JSON 语法错误(对象)");
                    }
                }
                if (c == '[')
                {
                    var l = new List<object>();
                    i++;
                    SkipWs(s, ref i);
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    while (true)
                    {
                        l.Add(ParseValue(s, ref i));
                        SkipWs(s, ref i);
                        if (s[i] == ',') { i++; continue; }
                        if (s[i] == ']') { i++; return l; }
                        throw new Exception("JSON 语法错误(数组)");
                    }
                }
                if (c == '"') return ParseString(s, ref i);
                if (s.IndexOf("true", i, StringComparison.Ordinal) == i) { i += 4; return (object)true; }
                if (s.IndexOf("false", i, StringComparison.Ordinal) == i) { i += 5; return (object)false; }
                if (s.IndexOf("null", i, StringComparison.Ordinal) == i) { i += 4; return null; }
                int start = i;
                while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '-' || s[i] == '+' || s[i] == '.' || s[i] == 'e' || s[i] == 'E')) i++;
                return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
            }

            private static string ParseString(string s, ref int i)
            {
                var sb = new StringBuilder();
                i++; // 开引号
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\')
                    {
                        i++;
                        char c = s[i];
                        switch (c)
                        {
                            case '"': sb.Append('"'); break;
                            case '\\': sb.Append('\\'); break;
                            case '/': sb.Append('/'); break;
                            case 'b': sb.Append('\b'); break;
                            case 'f': sb.Append('\f'); break;
                            case 'n': sb.Append('\n'); break;
                            case 'r': sb.Append('\r'); break;
                            case 't': sb.Append('\t'); break;
                            case 'u':
                                sb.Append((char)int.Parse(s.Substring(i + 1, 4), NumberStyles.HexNumber));
                                i += 4;
                                break;
                            default: sb.Append(c); break;
                        }
                        i++;
                    }
                    else sb.Append(s[i++]);
                }
                i++; // 闭引号
                return sb.ToString();
            }

            public static string Write(object v)
            {
                var sb = new StringBuilder();
                WriteValue(v, sb);
                return sb.ToString();
            }

            private static void WriteValue(object v, StringBuilder sb)
            {
                if (v == null) { sb.Append("null"); return; }
                if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
                if (v is string) { WriteString((string)v, sb); return; }
                if (v is double)
                {
                    double d = (double)v;
                    sb.Append(d == Math.Floor(d) && Math.Abs(d) < 1e15
                        ? d.ToString("0", CultureInfo.InvariantCulture)
                        : d.ToString("R", CultureInfo.InvariantCulture));
                    return;
                }
                if (v is float) { sb.Append(((float)v).ToString("R", CultureInfo.InvariantCulture)); return; }
                if (v is int) { sb.Append(((int)v).ToString(CultureInfo.InvariantCulture)); return; }
                if (v is long) { sb.Append(((long)v).ToString(CultureInfo.InvariantCulture)); return; }
                if (v is Dictionary<string, object>)
                {
                    sb.Append('{');
                    bool first = true;
                    foreach (KeyValuePair<string, object> kv in (Dictionary<string, object>)v)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteString(kv.Key, sb);
                        sb.Append(':');
                        WriteValue(kv.Value, sb);
                    }
                    sb.Append('}');
                    return;
                }
                if (v is IEnumerable)
                {
                    sb.Append('[');
                    bool first = true;
                    foreach (object item in (IEnumerable)v)
                    {
                        if (!first) sb.Append(',');
                        first = false;
                        WriteValue(item, sb);
                    }
                    sb.Append(']');
                    return;
                }
                WriteString(v.ToString(), sb);
            }

            private static void WriteString(string s, StringBuilder sb)
            {
                sb.Append('"');
                foreach (char c in s)
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': sb.Append("\\r"); break;
                        case '\t': sb.Append("\\t"); break;
                        default:
                            if (c < 32) sb.Append("\\u").Append(((int)c).ToString("x4"));
                            else sb.Append(c);
                            break;
                    }
                }
                sb.Append('"');
            }
        }

        // ================= 极简 HTTP 服务器（TcpListener，无需 URLACL） =================
        public class BridgeServer
        {
            public class Response { public int Code; public string Body; }

            private readonly int _port;
            private readonly Func<string, string, string, string, Response> _handler;
            private TcpListener _listener;
            private Thread _thread;
            private volatile bool _running;

            public BridgeServer(int port, Func<string, string, string, string, Response> handler)
            {
                _port = port;
                _handler = handler;
            }

            public bool Start()
            {
                try
                {
                    _listener = new TcpListener(IPAddress.Loopback, _port);
                    _listener.Start();
                    _running = true;
                    _thread = new Thread(AcceptLoop);
                    _thread.IsBackground = true;
                    _thread.Start();
                    return true;
                }
                catch (Exception) { return false; }
            }

            public void Stop()
            {
                _running = false;
                try { _listener.Stop(); } catch (Exception) { }
            }

            private void AcceptLoop()
            {
                while (_running)
                {
                    TcpClient client;
                    try { client = _listener.AcceptTcpClient(); }
                    catch (Exception) { break; }
                    ThreadPool.QueueUserWorkItem(delegate { HandleClient(client); });
                }
            }

            private void HandleClient(TcpClient client)
            {
                try
                {
                    client.ReceiveTimeout = 10000;
                    using (TcpClient c = client)
                    using (NetworkStream ns = c.GetStream())
                    {
                        string head = ReadUntil(ns, "\r\n\r\n");
                        if (head == null) return;
                        string[] lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
                        string[] rl = lines[0].Split(' ');
                        if (rl.Length < 2) return;
                        string method = rl[0];
                        string target = rl[1];
                        string query = "";
                        int qi = target.IndexOf('?');
                        if (qi >= 0) { query = target.Substring(qi + 1); target = target.Substring(0, qi); }

                        int contentLength = 0;
                        foreach (string line in lines)
                        {
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                                int.TryParse(line.Substring(15).Trim(), out contentLength);
                        }
                        string body = "";
                        if (contentLength > 0 && contentLength < 10 * 1024 * 1024)
                            body = ReadExact(ns, contentLength);

                        Response r = _handler(method, target, query, body);
                        if (r == null) r = new Response { Code = 500, Body = "{\"ok\":false}" };
                        byte[] payload = Encoding.UTF8.GetBytes(r.Body ?? "");
                        string header = "HTTP/1.1 " + r.Code + " " + (r.Code == 200 ? "OK" : "ERR") + "\r\n" +
                                        "Content-Type: application/json; charset=utf-8\r\n" +
                                        "Content-Length: " + payload.Length + "\r\n" +
                                        "Connection: close\r\n\r\n";
                        byte[] hb = Encoding.ASCII.GetBytes(header);
                        ns.Write(hb, 0, hb.Length);
                        ns.Write(payload, 0, payload.Length);
                        ns.Flush();
                    }
                }
                catch (Exception) { }
            }

            private static string ReadUntil(NetworkStream ns, string delim)
            {
                var sb = new StringBuilder();
                byte[] buf = new byte[1];
                while (sb.Length < 65536)
                {
                    int n = ns.Read(buf, 0, 1);
                    if (n <= 0) return null;
                    sb.Append((char)buf[0]);
                    if (sb.Length >= delim.Length && sb.ToString(sb.Length - delim.Length, delim.Length) == delim)
                        return sb.ToString(0, sb.Length - delim.Length);
                }
                return null;
            }

            private static string ReadExact(NetworkStream ns, int count)
            {
                byte[] buf = new byte[count];
                int off = 0;
                while (off < count)
                {
                    int n = ns.Read(buf, off, count - off);
                    if (n <= 0) break;
                    off += n;
                }
                return Encoding.UTF8.GetString(buf, 0, off);
            }
        }
    }
}
