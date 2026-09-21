using BepInEx.Configuration;

namespace StrikeCoin
{
    /// <summary>
    /// 不能叫 Config —— 会和 BasePlugin.Config 撞名。
    /// 注意 BepInEx 配置只支持 String / Boolean / 各整数 / Single / Double / Decimal / Enum，
    /// Color / Vector 一律不行。
    ///
    /// 键改名说明：CoinColorId -> CoinColorIds（复数，逗号分隔）、
    /// CoinColorValue -> CoinColorBaseValue、SpriteFile -> 移除（美术前缀固定等于色名）。
    /// BepInEx 是按「键名 + 默认值」绑定的，改名后旧 cfg 里那几条会自然失效，
    /// 新默认值直接生效 —— 这样就不用手工改 cfg 了。
    /// </summary>
    internal static class ModConfig
    {
        internal static ConfigEntry<string> CoinColorIds;
        internal static ConfigEntry<int> CoinColorBaseValue;
        internal static ConfigEntry<bool> FallbackUnknownColor;
        internal static ConfigEntry<string> ActiveEffectIds;
        internal static ConfigEntry<bool> DestroyAllCoins;
        internal static ConfigEntry<string> SelfRemoveCoinIds;
        internal static ConfigEntry<bool> RemoveSelfFirstCoin;
        internal static ConfigEntry<int> SelfRemoveMode;
        internal static ConfigEntry<int> TriggerMode;
        internal static ConfigEntry<bool> Verbose;

        internal static void Init(ConfigFile config)
        {
            CoinColorIds = config.Bind("General", "CoinColorIds", "ORANGE,BLACK,BLUE,CYAN,WHITE",
                "注册的自定义硬币颜色，逗号分隔，大小写不敏感。\n"
                + "静态数据里 coin 的 color 字段写成其中任意一个，就会被识别为该自定义色。\n"
                + "顺序决定枚举值的分配顺序；美术资源名也用它（内嵌 <色名>_<状态>.png）。");

            CoinColorBaseValue = config.Bind("General", "CoinColorBaseValue", 0,
                "自定义色占用的 COIN_COLOR_TYPE 起始值，按 CoinColorIds 的顺序连续分配。\n"
                + "0 = 自动：启动时扫一遍官方已占用的值，从最大值+1 开始（推荐 —— 官方将来新增硬币颜色也不会撞车）。\n"
                + "非 0 = 从该值起连续分配（如 5 表示 5/6/7/8/9）；只要区间里撞上官方颜色，就整体退回自动分配并打 Warning。");

            FallbackUnknownColor = config.Bind("General", "FallbackUnknownColor", true,
                "静态数据里出现的 color 字符串既不是官方颜色名、也不是 CoinColorIds 里的任何一个时，\n"
                + "原版 Enum.Parse 会直接抛异常，技能一加载就炸。开启后改成按 GOLD 处理并打 Error 日志。\n"
                + "关掉 = 还原版行为（抛异常）。");

            ActiveEffectIds = config.Bind("Effect", "ActiveEffectIds", "ORANGE",
                "哪些自定义色真正执行「拼点首次胜利 -> 摧毁对手全部硬币」。逗号分隔。\n"
                + "没列进来的颜色仍然会注册、会出图，但拼点胜利时只打一条占位日志，不做任何事。");

            DestroyAllCoins = config.Bind("Effect", "DestroyAllCoins", true,
                "触发时摧毁对手所有存活硬币。关掉只写日志，方便验证触发时机。");

            SelfRemoveCoinIds = config.Bind("Effect", "SelfRemoveCoinIds", "CYAN",
                "哪些自定义色执行「本技能每拼点一次（不论输赢，平局也算）-> 删掉自己第一枚该色硬币」。逗号分隔。\n"
                + "和 ActiveEffectIds 重叠时以 ActiveEffectIds（摧毁）为准。");

            RemoveSelfFirstCoin = config.Bind("Effect", "RemoveSelfFirstCoin", true,
                "自删效果的总开关。关掉只写日志，方便验证触发时机。");

            SelfRemoveMode = config.Bind("Effect", "SelfRemoveMode", 0,
                "0 = 真删（默认）：调用官方 SkillModel.RemoveCoinModelFromListByIndex，硬币从技能里彻底移除、界面消失。\n"
                + "1 = 摧毁兜底：不移除，只置 _isDestroyed + _isUsableInDuel（和 ORANGE 摧毁对手的写法一致），\n"
                + "   硬币还在列表里、显示为破碎态。真删导致战斗 UI 槽位错乱时切到 1。");

            TriggerMode = config.Bind("Effect", "TriggerMode", 0,
                "0 = 严格：本回合正在翻的那枚硬币必须是有特效的自定义色才触发（默认，对应“这枚币自己赢”）。\n"
                + "1 = 宽松：技能里只要存在存活的该类硬币就触发。\n"
                + "如果开 Verbose 后发现严格模式一直不触发，改 1 即可。\n"
                + "只对 ActiveEffectIds（赢了才触发）那套效果生效；SelfRemoveCoinIds 不看胜负，不受此项影响。");

            Verbose = config.Bind("Debug", "Verbose", false,
                "每次拼点都打印双方每一枚硬币的颜色 / 存活 / 可用状态（自定义色会显示色名而不是数字）。定位触发问题时打开。");
        }
    }
}
