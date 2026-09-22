using System;
using System.Collections.Generic;
using System.Text;
using BepInEx.Logging;
using UnityEngine;

namespace StrikeCoin
{
    /// <summary>
    /// 一种自定义硬币颜色的功能特性。
    /// None = 占位（只注册 + 出图，不做事）。
    /// 以后填真实特性：在这里加一个枚举值，再去 Patches.ApplyEffect / TryRemoveOwnEffectCoin 里补分支。
    /// </summary>
    internal enum CoinEffect
    {
        None = 0,

        /// <summary>拼点首次胜利 -> 摧毁对手全部硬币（ORANGE）。在 ApplyEffect 里处理。</summary>
        DestroyAllCoins = 1,

        /// <summary>
        /// 本技能每参与一次拼点（不论输赢、平局也算），就删掉自己第一枚该色硬币（CYAN）。
        /// 在 TryRemoveOwnEffectCoin 里处理 —— 它不看胜负，所以不能挂在只处理赢家的 ApplyEffect 上。
        /// </summary>
        RemoveSelfFirstCoin = 2,
    }

    /// <summary>
    /// 一枚自定义硬币颜色的定义。
    /// Value 是运行时分配的（不写死），静态数据里只存 Id 这个字符串。
    /// </summary>
    internal sealed class CoinDef
    {
        /// <summary>静态数据里 coin 的 color 字段写的字符串（大小写不敏感）。</summary>
        internal string Id;

        /// <summary>运行时占用的 COIN_COLOR_TYPE 数值。</summary>
        internal int Value = -1;

        internal CoinEffect Effect = CoinEffect.None;

        /// <summary>内嵌 PNG 缺失时，程序化兜底硬币用的主色。</summary>
        internal Color32 Tint = new Color32(0x9E, 0x9E, 0xA8, 0xFF);

        /// <summary>内嵌资源前缀 = 色名，即 ORANGE_0.png / BLACK_2.png。</summary>
        internal string ArtPrefix { get { return Id; } }

        internal COIN_COLOR_TYPE Type { get { return (COIN_COLOR_TYPE)Value; } }

        internal bool HasEffect { get { return Effect != CoinEffect.None; } }
    }

    /// <summary>
    /// 全部自定义硬币颜色的注册表。启动时 Build() 一次，之后全靠它做索引查询。
    ///
    /// 为什么每种颜色都得占一个 COIN_COLOR_TYPE 数值：
    /// 图像出口 UISpriteDataManager.GetCoinSprite(color, state) 只收 (数值, 状态)，
    /// 拿不到 CoinModel 实例，没法按实例区分。所以"注册一种颜色"必然等于"占一个值"。
    /// </summary>
    internal static class CoinRegistry
    {
        private static readonly List<CoinDef> All = new List<CoinDef>();
        private static readonly Dictionary<string, CoinDef> ById =
            new Dictionary<string, CoinDef>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<int, CoinDef> ByValue = new Dictionary<int, CoinDef>();

        /// <summary>
        /// 已知色名的兜底着色（只用于内嵌图全缺时的程序化硬币）。
        /// 表里没有的自定义 id 一律用中性灰 —— 不会误导成"这是某种官方色"。
        /// </summary>
        private static readonly Dictionary<string, Color32> Tints =
            new Dictionary<string, Color32>(StringComparer.OrdinalIgnoreCase)
            {
                { "ORANGE", new Color32(0xFF, 0x9A, 0x1F, 0xFF) },
                { "BLACK",  new Color32(0x33, 0x33, 0x38, 0xFF) },
                { "BLUE",   new Color32(0x3A, 0x7B, 0xD5, 0xFF) },
                { "CYAN",   new Color32(0x2E, 0xD4, 0xD4, 0xFF) },
                { "WHITE",  new Color32(0xF0, 0xF0, 0xF0, 0xFF) },
            };

        private const string DefaultId = "ORANGE";

        /// <summary>按配置建表并分配枚举值。必须在 CoinColorAllocator.Scan() 之后调用。</summary>
        internal static void Build()
        {
            All.Clear();
            ById.Clear();
            ByValue.Clear();

            var ids = ParseList(ModConfig.CoinColorIds.Value);
            if (ids.Count == 0)
            {
                Log(LogLevel.Warning, "CoinColorIds 是空的，回退到只注册 " + DefaultId + "。");
                ids.Add(DefaultId);
            }

            // 两套效果各自一份名单：赢家才触发的 DestroyAllCoins，和不看胜负的 RemoveSelfFirstCoin。
            // 两种都列了的 id 以 DestroyAllCoins 为准（先赢后自删，两条分支互不冲突，
            // 但同一枚币同时挂两种语义容易看不懂，所以这里给个确定的优先级）。
            var active = new HashSet<string>(ParseList(ModConfig.ActiveEffectIds.Value),
                                             StringComparer.OrdinalIgnoreCase);
            var selfRemove = new HashSet<string>(ParseList(ModConfig.SelfRemoveCoinIds.Value),
                                                 StringComparer.OrdinalIgnoreCase);

            // 显式指定了起始值，就从这个值开始连续分配；中间只要撞上官方占用就整体回退自动。
            int baseValue = ModConfig.CoinColorBaseValue.Value;
            bool useBase = baseValue > 0;
            if (useBase && HasOfficialCollision(baseValue, ids.Count))
            {
                useBase = false;
                Log(LogLevel.Warning, "CoinColorBaseValue=" + baseValue + " 起的 " + ids.Count
                    + " 个值里撞上了官方硬币颜色，改用自动分配。"
                    + "（想固定值就换成官方没用到的区间；0 = 交给插件自动选）");
            }

            for (int i = 0; i < ids.Count; i++)
            {
                string id = ids[i];
                if (ById.ContainsKey(id))
                {
                    Log(LogLevel.Warning, "CoinColorIds 里有重复项 " + id + "，已忽略。");
                    continue;
                }

                int value = useBase ? baseValue + ByValue.Count : CoinColorAllocator.Allocate();
                var def = new CoinDef
                {
                    Id = id,
                    Value = value,
                    Effect = EffectOf(id, active, selfRemove),
                    Tint = TintOf(id),
                };

                All.Add(def);
                ById[id] = def;
                ByValue[value] = def;

                // 官方哪天真加了个同名的硬币颜色：仍然劫持（保证模组不因更新失效），但要让玩家看见。
                if (CoinColorAllocator.IsOfficialName(id))
                {
                    Log(LogLevel.Warning, "官方已经定义了名为 " + id + " 的硬币颜色（值 "
                        + CoinColorAllocator.ValueOf(id) + "）。按当前策略仍然劫持为自定义色 —— "
                        + "官方的同名硬币也会套用本模组的硬币图，并按本模组的规则生效。");
                }
            }

            Log(LogLevel.Info, "已注册硬币颜色: " + Describe()
                + (useBase ? "（CoinColorBaseValue 指定）" : "（自动分配）"));
        }

        internal static List<CoinDef> Defs { get { return All; } }

        internal static bool TryGetById(string id, out CoinDef def)
        {
            def = null;
            return !string.IsNullOrEmpty(id) && ById.TryGetValue(id, out def);
        }

        internal static bool TryGetByValue(int value, out CoinDef def)
        {
            return ByValue.TryGetValue(value, out def);
        }

        /// <summary>Verbose 日志用：自定义值翻译成色名，官方值仍是数字。</summary>
        internal static string NameOf(int value)
        {
            CoinDef def;
            return ByValue.TryGetValue(value, out def) ? def.Id : value.ToString();
        }

        /// <summary>启动日志用："ORANGE=5(摧毁) CYAN=8(自删) BLACK=6(占位) ..."。</summary>
        internal static string Describe()
        {
            var sb = new StringBuilder();
            foreach (var def in All)
            {
                sb.Append(def.Id).Append('=').Append(def.Value)
                  .Append(LabelOf(def.Effect)).Append(' ');
            }
            return sb.Length == 0 ? "(无)" : sb.ToString(0, sb.Length - 1);
        }

        private static string LabelOf(CoinEffect effect)
        {
            switch (effect)
            {
                case CoinEffect.DestroyAllCoins: return "(摧毁)";
                case CoinEffect.RemoveSelfFirstCoin: return "(自删)";
                default: return "(占位)";
            }
        }

        private static CoinEffect EffectOf(string id, HashSet<string> active, HashSet<string> selfRemove)
        {
            if (active.Contains(id)) return CoinEffect.DestroyAllCoins;
            if (selfRemove.Contains(id)) return CoinEffect.RemoveSelfFirstCoin;
            return CoinEffect.None;
        }

        private static bool HasOfficialCollision(int baseValue, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (CoinColorAllocator.IsOfficialValue(baseValue + i)) return true;
            }
            return false;
        }

        private static Color32 TintOf(string id)
        {
            Color32 tint;
            return Tints.TryGetValue(id, out tint) ? tint : new Color32(0x9E, 0x9E, 0xA8, 0xFF);
        }

        /// <summary>逗号分隔 -> 列表（去空白、去空项，保持顺序）。</summary>
        private static List<string> ParseList(string raw)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(raw)) return list;
            foreach (var part in raw.Split(','))
            {
                var s = part.Trim();
                if (s.Length > 0) list.Add(s);
            }
            return list;
        }

        private static void Log(LogLevel level, string message)
        {
            var log = StrikeCoinPlugin.LogInstance;
            if (log != null) log.Log(level, "[StrikeCoin] " + message);
        }



        public static void RegisterCustomCoinColor(string coinColor, int? coinId = null)
        {
            coinColor = coinColor.ToUpper();
            if (string.IsNullOrEmpty(coinColor) || ById.ContainsKey(coinColor)) return;

            int val = coinId.HasValue ? coinId.Value : CoinColorAllocator.Allocate();

            if (coinId.HasValue) CoinColorAllocator.Reserve(val);

            var def = new CoinDef
            {
                Id = coinColor,
                Value = val,
                Effect = CoinEffect.None,
                Tint = TintOf(coinColor)
            };

            All.Add(def);
            ById[coinColor] = def;
            ByValue[val] = def;

            StrikeCoinPlugin.LogInstance?.LogInfo($"Registered color from file: {coinColor} with id = {val}");
        }
    }
}
