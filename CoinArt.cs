using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace StrikeCoin
{
    /// <summary>
    /// 自定义硬币的图像。
    ///
    /// 美术**全部内嵌在 DLL 里**（StrikeCoin.Art.&lt;色名&gt;_&lt;状态&gt;.png），
    /// 部署只需要一个 StrikeCoin.dll，插件目录下不需要放任何 PNG。
    ///
    /// 优先级：
    ///   1. 内嵌 &lt;色名&gt;_&lt;状态&gt;.png   （按 COIN_UI_STATE 分图）
    ///   2. 内嵌 &lt;色名&gt;.png              （一张图吃所有状态）
    ///   3. 代码画的着色硬币                  （按 CoinDef.Tint，缺图时也不会变成空白）
    ///
    /// 注：没有做"回主菜单时 Destroy 重建"那套生命周期。
    /// Lethe 是在 LobbyUIPresenter.Initialize 的 Postfix 里做的，但那个方法是 Slot 6 虚方法，
    /// IL2CPP 下不能 patch。这里最多只缓存 (色数 × 状态数) 个 Sprite，常驻不销毁反而更稳
    /// （Sprite.Create 出来的对象不属于场景，不会随场景卸载）。
    /// </summary>
    internal static class CoinArt
    {
        private const int Size = 128;
        private const string ResourcePrefix = "StrikeCoin.Art.";

        private static readonly Dictionary<int, Sprite> Cache = new Dictionary<int, Sprite>();
        private static readonly HashSet<string> Missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<int> UnknownLogged = new HashSet<int>();
        private static readonly HashSet<string> Loaded = new HashSet<string>(StringComparer.Ordinal);

        private static Dictionary<string, string> _resources; // 资源名 -> manifest 全名

        /// <summary>从原版金色硬币身上量出来的 pivot / pixelsPerUnit / 参考宽度。</summary>
        private static Vector2 _pivot = new Vector2(0.5f, 0.5f);
        private static float _pixelsPerUnit = 100f;
        private static float _refWidth = 0f;
        private static bool _metricsLogged;

        /// <summary>建一遍内嵌资源索引，并把结果打进日志 —— 打包没生效时能一眼看出。</summary>
        internal static void Init()
        {
            try
            {
                _resources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var asm = typeof(CoinArt).Assembly;
                int png = 0;
                foreach (var name in asm.GetManifestResourceNames())
                {
                    if (!name.StartsWith(ResourcePrefix, StringComparison.OrdinalIgnoreCase)) continue;
                    _resources[name] = name;
                    if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) png++;
                }

                var log = StrikeCoinPlugin.LogInstance;
                if (log == null) return;
                if (png == 0)
                {
                    log.LogError("[StrikeCoin] DLL 里没有内嵌任何硬币图（应有 StrikeCoin.Art.<色名>_<状态>.png）。"
                        + "检查 csproj 的 EmbeddedResource 是否生效；现在所有自定义色都会用代码画的兜底硬币。");
                }
                else
                {
                    log.LogInfo("[StrikeCoin] 内嵌硬币图 " + png + " 张。");
                }
            }
            catch (Exception e)
            {
                StrikeCoinPlugin.LogInstance?.LogError("[StrikeCoin] 读取内嵌资源清单失败: " + e.Message);
            }
        }

        public static Sprite Get(CoinDef def, COIN_UI_STATE state)
        {
            if (def == null) return null;

            int raw = (int)state;

            // 官方将来新增 COIN_UI_STATE（比如 6 / 7）时，不要走"完好币"那条兜底 ——
            // 那会让新状态显示成一枚没翻过的硬币。统一复用正面（FRONT）那张。
            int key = raw;
            if (raw < 0 || raw > (int)COIN_UI_STATE.BROKEN_FRONT)
            {
                LogUnknownState(def, raw);
                key = (int)COIN_UI_STATE.FRONT;
            }

            int cacheKey = def.Value * 1000 + key;
            Sprite cached;
            if (Cache.TryGetValue(cacheKey, out cached) && cached != null) return cached;

            CaptureVanillaMetrics((COIN_UI_STATE)key);

            var tex = LoadEmbedded(def, key);
            if (tex != null)
            {
                tex.name = def.Id + "_" + key;
                ReportLoaded(def, key, "内嵌 PNG " + def.ArtPrefix + "_" + key + ".png", tex);
                return Wrap(cacheKey, tex, def.Id + "_");
            }

            var fallback = MakeProcedural(def, IsBroken((COIN_UI_STATE)key));
            ReportLoaded(def, key, "程序化兜底（没找到内嵌图）", fallback);
            return Wrap(cacheKey, fallback, "Procedural_");
        }

        /// <summary>
        /// 用原版金色硬币的 pivot / pixelsPerUnit 包装纹理，
        /// 并按纹理宽度等比换算 pixelsPerUnit —— 这样不管 PNG 多大，
        /// 显示出来的**世界尺寸**都和原版金色硬币一致。
        /// </summary>
        private static Sprite Wrap(int cacheKey, Texture2D tex, string tag)
        {
            float ppu = _pixelsPerUnit;
            if (_refWidth > 0f && tex.width > 0)
                ppu = _pixelsPerUnit * (tex.width / _refWidth);

            var sp = Sprite.Create(tex, new Rect(0, 0, tex.width, tex.height), _pivot, ppu);
            sp.name = tag + cacheKey;
            Cache[cacheKey] = sp;
            return sp;
        }

        /// <summary>内嵌 PNG -> Texture2D。找不到或解码失败都返回 null（已做去重，不会重复打日志）。</summary>
        private static Texture2D LoadEmbedded(CoinDef def, int state)
        {
            string name = Find(def.ArtPrefix + "_" + state) ?? Find(def.ArtPrefix);
            if (name == null)
            {
                ReportMissing(def, "StrikeCoin.Art." + def.ArtPrefix + "_" + state + ".png");
                return null;
            }

            try
            {
                var asm = typeof(CoinArt).Assembly;
                byte[] bytes;
                using (var stream = asm.GetManifestResourceStream(name))
                {
                    if (stream == null)
                    {
                        ReportMissing(def, name);
                        return null;
                    }
                    using (var ms = new MemoryStream())
                    {
                        stream.CopyTo(ms);
                        bytes = ms.ToArray();
                    }
                }

                var tex = PngDecoder.Load(bytes);
                if (tex == null)
                {
                    ReportMissing(def, name);
                    return null;
                }
                return tex;
            }
            catch (Exception e)
            {
                ReportMissing(def, name + " (解码失败: " + e.Message + ")");
                return null;
            }
        }

        /// <summary>按 manifest 全名找内嵌资源（大小写不敏感）。</summary>
        private static string Find(string fileName)
        {
            if (_resources == null || string.IsNullOrEmpty(fileName)) return null;
            string hit;
            return _resources.TryGetValue(ResourcePrefix + fileName + ".png", out hit) ? hit : null;
        }

        /// <summary>
        /// 每个 (色, 状态) 只打一次，说明这张图到底是哪来的。
        /// 之前 v1.2.0 把成功日志删了，结果"图没换"时根本看不出是内嵌 PNG 没加载、
        /// 还是加载了但走了别的取图路径 —— 排查全靠猜。
        /// </summary>
        private static void ReportLoaded(CoinDef def, int state, string what, Texture2D tex)
        {
            if (!Loaded.Add(def.Id + "|" + state)) return;
            StrikeCoinPlugin.LogInstance?.LogInfo("[StrikeCoin] 硬币图 " + def.Id + " 状态 " + state
                + " <- " + what + " (" + tex.width + "x" + tex.height + ")");
        }

        private static void ReportMissing(CoinDef def, string what)
        {
            if (!Missing.Add(def.Id + "|" + what)) return;
            StrikeCoinPlugin.LogInstance?.LogWarning("[StrikeCoin] " + def.Id + " 的硬币图不可用: " + what
                + " —— 已用代码画的兜底硬币（" + def.Id + " 主色）。"
                + "图要放在源码 art/ 目录下并重新编译，才会被内嵌进 DLL。");
        }

        /// <summary>
        /// 量一次原版金色硬币的几何参数。顺带把尺寸打进日志 ——
        /// 做美术时照着这个分辨率出图最省事。
        /// </summary>
        private static void CaptureVanillaMetrics(COIN_UI_STATE state)
        {
            if (_refWidth > 0f) return;
            try
            {
                var mgr = UISpriteDataManager.Instance;
                if (mgr == null) return;
                var sp = mgr.GetCoinSprite(COIN_COLOR_TYPE.GOLD, state);
                if (sp == null) return;

                var r = sp.rect;
                if (r.width <= 0f || r.height <= 0f) return;

                _pivot = new Vector2(sp.pivot.x / r.width, sp.pivot.y / r.height);
                _pixelsPerUnit = sp.pixelsPerUnit;
                _refWidth = r.width;

                if (!_metricsLogged)
                {
                    _metricsLogged = true;
                    StrikeCoinPlugin.LogInstance?.LogInfo(
                        "[StrikeCoin] 原版金色硬币参考参数: rect=" + (int)r.width + "x" + (int)r.height
                        + " pixelsPerUnit=" + sp.pixelsPerUnit
                        + " pivot=" + sp.pivot
                        + " (PNG 按这个尺寸出图即可，其它尺寸也会自动等比缩放)");
                }
            }
            catch (Exception e)
            {
                StrikeCoinPlugin.LogInstance?.LogWarning("[StrikeCoin] 读取原版硬币参数失败，用默认值: " + e.Message);
            }
        }

        /// <summary>官方新增的未知状态只提示一次，避免刷屏。</summary>
        private static void LogUnknownState(CoinDef def, int raw)
        {
            int key = def.Value * 1000 + raw;
            if (!UnknownLogged.Add(key)) return;
            StrikeCoinPlugin.LogInstance?.LogWarning("[StrikeCoin] " + def.Id + " 遇到未知的 COIN_UI_STATE " + raw
                + "（官方新增了硬币状态？），已复用 FRONT(2) 的图，不会显示成没翻过的完好币。");
        }

        private static bool IsBroken(COIN_UI_STATE state)
        {
            return state == COIN_UI_STATE.BROKEN_NONE
                || state == COIN_UI_STATE.BROKEN_BACK
                || state == COIN_UI_STATE.BROKEN_FRONT;
        }

        /// <summary>
        /// 兜底美术：纯代码画一枚硬币，主色取 CoinDef.Tint。
        /// new Texture2D + SetPixel + Apply 在本作里是可用的（RPGHelper 的 UiKit 已验证）。
        /// </summary>
        private static Texture2D MakeProcedural(CoinDef def, bool broken)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            float c = Size / 2f;
            float rOuter = Size / 2f - 2f;
            float rInner = rOuter - 7f;

            var clear = new Color32(0, 0, 0, 0);
            var rim = Shade(def.Tint, 0.42f);
            var face = def.Tint;
            var hi = Lighten(def.Tint, 0.35f);
            var crack = Shade(def.Tint, 0.28f);
            crack.a = 0xD8;

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float dx = x + 0.5f - c;
                    float dy = y + 0.5f - c;
                    float d = (float)Math.Sqrt(dx * dx + dy * dy);

                    if (d > rOuter) { tex.SetPixel(x, y, clear); continue; }
                    if (d > rInner) { tex.SetPixel(x, y, rim); continue; }

                    // 左上角打一点高光，让它在小尺寸下也能看出是个圆币
                    bool highlight = (-dx - dy) > rInner * 0.55f;
                    tex.SetPixel(x, y, highlight ? hi : face);
                }
            }

            if (broken)
            {
                // 一道从左上到右下的裂纹
                for (int i = 0; i < Size * 2; i++)
                {
                    float t = i / (float)(Size * 2);
                    int px = (int)(t * Size);
                    int py = (int)(t * Size + Math.Sin(t * 14f) * 7f);
                    for (int k = -1; k <= 1; k++)
                    {
                        int yy = py + k;
                        if (px < 0 || px >= Size || yy < 0 || yy >= Size) continue;
                        tex.SetPixel(px, yy, crack);
                    }
                }
            }

            tex.Apply();
            tex.name = "CoinArt_Procedural_" + def.Id + (broken ? "_Broken" : "");
            return tex;
        }

        private static Color32 Shade(Color32 c, float f)
        {
            return new Color32((byte)(c.r * f), (byte)(c.g * f), (byte)(c.b * f), c.a);
        }

        private static Color32 Lighten(Color32 c, float t)
        {
            return new Color32((byte)(c.r + (255 - c.r) * t),
                               (byte)(c.g + (255 - c.g) * t),
                               (byte)(c.b + (255 - c.b) * t), c.a);
        }
    }
}
