using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace StrikeCoin
{
    /// <summary>
    /// 全部 hook 点都已在 dump.cs 里逐一核对过 Slot 标记 —— 没有一个是虚方法。
    /// IL2CPP 下 Harmony patch 虚方法会导致"调用原方法"走回被 patch 的 override，
    /// 无限递归 -> StackOverflow -> 游戏闪退，所以 Apply() 里还加了一道 IsVirtual 断言兜底。
    /// </summary>
    internal static class Patches
    {
        private const string Tag = "[StrikeCoin] ";

        /// <summary>已经触发过的 CoinModel 实例（按原生指针记，避免托管包装器的引用相等问题）。</summary>
        private static readonly HashSet<IntPtr> Triggered = new HashSet<IntPtr>();

        /// <summary>占位色提示过一次的记名，避免每回合刷屏。</summary>
        private static readonly HashSet<string> PlaceholderLogged = new HashSet<string>();

        /// <summary>
        /// 每种自定义色各一个兜底 CoinColor 实例 —— 不能共用！
        /// TryGetSprite 会把查到的图缓存进实例自己的 _sprites，共用一个实例的话
        /// 五种色会互相串味。按颜色值索引，顺带把它们的原生指针记进 FallbackOwners，
        /// 好在 TryGetSprite 的 prefix 里反查"这个实例属于哪种色"。
        /// </summary>
        private static readonly Dictionary<int, CoinModel.CoinColor> FallbackColors =
            new Dictionary<int, CoinModel.CoinColor>();

        private static readonly Dictionary<IntPtr, CoinDef> FallbackOwners = new Dictionary<IntPtr, CoinDef>();

        private static int _errorBudget = 20;

        // ------------------------------------------------------------------ 安装

        /// <summary>
        /// 扫一遍官方枚举、给每种自定义色分配一个不撞车的值，并做「官方新增硬币」的兼容性自检。
        /// 必须在 Apply() 之前调用一次。
        /// </summary>
        internal static void Init()
        {
            CoinColorAllocator.Scan();
            Log(LogLevel.Info, "官方 COIN_COLOR_TYPE 成员: " + CoinColorAllocator.Describe()
                + (CoinColorAllocator.Scanned ? "" : "  (扫描失败，已退回历史值 5 起向后找空位)"));

            // 建表 + 分配值 + 逐色做同名冲突检测，都在 CoinRegistry.Build() 里。
            CoinRegistry.Build();
        }

        internal static void Apply(Harmony harmony)
        {
            _hooksOk = 0;
            _hooksMissing = 0;

            Patch(harmony,
                AccessTools.Method(typeof(SkillCoinData), "get_CoinColorColor"),
                "Prefix_SkillCoinData_CoinColorColor", null,
                "SkillCoinData.get_CoinColorColor (0x18149B300) —— 让 color 字段能写自定义色 id（CoinColorIds）");

            Patch(harmony,
                AccessTools.Method(typeof(CoinModel), "TryGetCoinColor"),
                null, "Postfix_CoinModel_TryGetCoinColor",
                "CoinModel.TryGetCoinColor (0x18248D2F0) —— 越界枚举值的兜底，否则返回 null 会 NRE");

            Patch(harmony,
                AccessTools.Method(typeof(UISpriteDataManager), "GetCoinSprite",
                    new[] { typeof(COIN_COLOR_TYPE), typeof(COIN_UI_STATE) }),
                "Prefix_UISpriteDataManager_GetCoinSprite", null,
                "UISpriteDataManager.GetCoinSprite (0x1815BF8F0) —— 全游戏硬币图像唯一出口");

            Patch(harmony,
                AccessTools.Method(typeof(CoinModel), "GetCoinSprite",
                    new[] { typeof(COIN_UI_STATE), typeof(bool) }),
                "Prefix_CoinModel_GetCoinSprite", null,
                "CoinModel.GetCoinSprite (0x18248D270) —— 绕过 CoinColor.TryGetSprite 的类型查表");

            Patch(harmony,
                AccessTools.Method(typeof(BattleActionModelManager), "Parrying"),
                null, "Postfix_Parrying",
                "BattleActionModelManager.Parrying (0x180B6AF10) —— 拼点主循环");

            // TryGetSprite 是 protected，AccessTools.Method 可能按名+签名找不到，
            // 这里显式带上 NonPublic 再找一次。
            Patch(harmony,
                FindMethod(typeof(CoinModel.CoinColor), "TryGetSprite", typeof(COIN_UI_STATE)),
                "Prefix_CoinColor_TryGetSprite", null,
                "CoinModel.CoinColor.TryGetSprite (0x1824961A0) —— 第二个取图出口，"
                + "兜底实例 get_type() 是 GOLD，不接管的话会查出金色图");

            // 全部 hook 都按「方法名」查找（不是硬编码 VA），官方改地址不影响；
            // 但官方一旦改签名/改名，这里会打 Error —— 所以把命中情况汇总成一条，方便一眼看出。
            if (_hooksMissing > 0)
            {
                Log(LogLevel.Error, "hook 自检: " + _hooksOk + " 个命中, " + _hooksMissing
                    + " 个缺失（见上方 Error）。缺失的那部分功能不会生效，通常是游戏更新改了方法签名。");
            }
            else
            {
                Log(LogLevel.Info, "hook 自检: " + _hooksOk + "/" + _hooksOk + " 全部命中。");
            }
        }

        private static int _hooksOk;
        private static int _hooksMissing;

        /// <summary>按名+签名找方法，找不到就退化成"只要同名"（protected 方法的兜底）。</summary>
        private static MethodInfo FindMethod(Type type, string name, params Type[] parameters)
        {
            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static
                                     | BindingFlags.Public | BindingFlags.NonPublic;
            var m = type.GetMethod(name, flags, null, parameters, null);
            return m ?? type.GetMethod(name, flags);
        }

        private static void Patch(Harmony harmony, MethodBase target,
                                  string prefixName, string postfixName, string label)
        {
            if (target == null)
            {
                _hooksMissing++;
                Log(LogLevel.Error, "找不到目标方法，跳过: " + label);
                return;
            }
            if (target.IsVirtual || target.IsAbstract)
            {
                _hooksMissing++;
                Log(LogLevel.Error, "目标是虚/抽象方法，拒绝 patch（会无限递归爆栈）: " + label);
                return;
            }

            const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
            HarmonyMethod prefix = null, postfix = null;
            if (prefixName != null)
                prefix = new HarmonyMethod(typeof(Patches).GetMethod(prefixName, flags));
            if (postfixName != null)
                postfix = new HarmonyMethod(typeof(Patches).GetMethod(postfixName, flags));

            harmony.Patch(target, prefix, postfix);
            _hooksOk++;
            Log(LogLevel.Info, "已 patch: " + label);
        }

        // ------------------------------------------------- 1) 颜色：静态数据 -> 枚举值

        /// <summary>
        /// 原方法是 Enum.Parse(typeof(COIN_COLOR_TYPE), color)，遇到未知字符串直接抛异常，
        /// 而且它把解析结果缓存进 _coinColorType 后会把 color 置空。
        /// 这里在它之前拦下所有已注册的自定义色：命中就自己写缓存并返回。
        /// color 为空说明已经解析过，交给原方法返回缓存值。
        ///
        /// 另外顺手兜住「既不是官方颜色名、也不是任何自定义色」的情况 ——
        /// 官方将来改颜色名、或者静态数据里写错、或者旧标识（StrikeCoin）没跟着改，
        /// 原版都会抛异常让整个技能加载失败，这里改成按 GOLD 处理并打 Error。
        /// </summary>
        private static bool Prefix_SkillCoinData_CoinColorColor(SkillCoinData __instance,
                                                               ref COIN_COLOR_TYPE __result)
        {
            try
            {
                if (__instance == null) return true;
                string color = __instance.color;
                if (string.IsNullOrEmpty(color)) return true;

                CoinDef def;
                if (CoinRegistry.TryGetById(color, out def))
                {
                    __instance._coinColorType = def.Type;
                    __instance.color = null;
                    __result = def.Type;
                    return false;
                }

                // 数字写法（"1" = GREY）原版 Enum.Parse 认得，别把它当未知兜底掉
                if (ModConfig.FallbackUnknownColor.Value
                    && !CoinColorAllocator.IsOfficialName(color)
                    && !CoinColorAllocator.IsNumericLiteral(color))
                {
                    __instance._coinColorType = COIN_COLOR_TYPE.GOLD;
                    __instance.color = null;
                    __result = COIN_COLOR_TYPE.GOLD;
                    LogThrottled("未知的 coin color 字符串 \"" + color + "\"，已按 GOLD 处理"
                        + "（原版 Enum.Parse 会抛异常导致技能加载失败）。"
                        + "当前已注册的自定义色: " + ModConfig.CoinColorIds.Value
                        + "，请检查静态数据拼写。");
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                LogThrottled("get_CoinColorColor prefix 异常", e);
                return true;
            }
        }

        /// <summary>
        /// TryGetCoinColor 用 Type.GetType("CoinColor_" + 枚举名) 反射，
        /// 越界枚举值会去找不存在的 CoinColor_<数字>，LogError 后返回 null，
        /// 上游 CoinModel.GetCoinSprite 直接拿它解引用 -> NRE。
        /// 这里给一个 CoinColor_GOLD 实例兜底（于是破坏规则沿用 GOLD：comGrade >= myGrade）。
        /// 注意返回实例的 get_type() 是 GOLD，所以硬币图不能靠它 —— 图像由 patch 3/4 单独给。
        ///
        /// 条件是「官方解析失败（__result == null）」且「这个值属于我们注册的自定义色」两个都满足，
        /// 将来官方新增了颜色但没配套 CoinColor_X 类时，不会被我们误兜。
        ///
        /// ⚠ 光 patch 这里不够：拿了这个实例的人如果走 CoinColor.TryGetSprite，
        /// 它会用 get_type()（虚方法，我们的实例永远是 GOLD）去查图 —— 拿到的是金色图。
        /// 所以还必须 patch TryGetSprite（见 Prefix_CoinColor_TryGetSprite）。
        ///
        /// TODO: 四种占位色目前也沿用 GOLD 的破坏规则。等各自特性定下来，
        /// 这里要按 def 返回不同的 CoinColor 实例。
        /// </summary>
        private static void Postfix_CoinModel_TryGetCoinColor(COIN_COLOR_TYPE colorType,
                                                              ref CoinModel.CoinColor __result)
        {
            try
            {
                if (__result != null) return;
                CoinDef def;
                if (!CoinRegistry.TryGetByValue((int)colorType, out def)) return;
                __result = GetFallbackColor(def);
            }
            catch (Exception e)
            {
                LogThrottled("TryGetCoinColor postfix 异常", e);
            }
        }

        /// <summary>
        /// 第二个取图出口 —— 之前漏了它，所以投币动画 / 技能信息面板里的自定义色显示成金色。
        ///
        /// CoinColor.TryGetSprite(0x1824961A0) 的逻辑是：
        ///   1. 查实例自己的 _sprites 缓存；
        ///   2. 未命中 -> UISpriteDataManager.GetCoinSprite(this.get_type(), state)，并写回缓存。
        /// get_type() 是虚方法（Slot 4，IL2CPP 下不能 patch），我们的兜底实例是 CoinColor_GOLD，
        /// 所以它永远拿 GOLD 去查 -> 金色图，还会把金色图缓存住。
        /// 调用方有 14 处（StartCoinToss / Start_Cointoss / 技能信息面板 / CoinModel.GetCoinSprite…），
        /// 全都绕过了 patch 3/4。
        ///
        /// 这个方法是**非虚**的（dump.cs 无 Slot 标记），可以安全 patch。
        /// 判断方式：只认我们自己造出来的那几个兜底实例（按原生指针反查属于哪种色），
        /// 别的 CoinColor 一律放行。
        /// </summary>
        private static bool Prefix_CoinColor_TryGetSprite(CoinModel.CoinColor __instance,
                                                          COIN_UI_STATE coinUIState,
                                                          ref Sprite __result)
        {
            try
            {
                if (__instance == null) return true;
                IntPtr key = SafePointer(__instance);
                if (key == IntPtr.Zero) return true;

                CoinDef def;
                if (!FallbackOwners.TryGetValue(key, out def)) return true;

                var sprite = CoinArt.Get(def, coinUIState);
                if (sprite == null) return true;
                __result = sprite;
                return false;
            }
            catch (Exception e)
            {
                LogThrottled("CoinColor.TryGetSprite prefix 异常", e);
                return true;
            }
        }

        private static CoinModel.CoinColor GetFallbackColor(CoinDef def)
        {
            CoinModel.CoinColor color;
            if (FallbackColors.TryGetValue(def.Value, out color) && color != null && !color.WasCollected)
                return color;

            // 实例类型仍是 CoinColor_GOLD —— get_type() 是虚方法（Slot 4）改不了，
            // 所以破坏规则依然沿用 GOLD。图像改由 patch 6（TryGetSprite）单独接管。
            color = new CoinColor_GOLD();
            FallbackColors[def.Value] = color;

            IntPtr key = SafePointer(color);
            if (key != IntPtr.Zero) FallbackOwners[key] = def;
            return color;
        }

        // ------------------------------------------------------------------ 2) 图像

        private static bool Prefix_UISpriteDataManager_GetCoinSprite(COIN_COLOR_TYPE coinUIType,
                                                                     COIN_UI_STATE coinUIState,
                                                                     ref Sprite __result)
        {
            try
            {
                CoinDef def;
                if (!CoinRegistry.TryGetByValue((int)coinUIType, out def)) return true;
                var sprite = CoinArt.Get(def, coinUIState);
                if (sprite == null) return true;
                __result = sprite;
                return false;
            }
            catch (Exception e)
            {
                LogThrottled("UISpriteDataManager.GetCoinSprite prefix 异常", e);
                return true;
            }
        }

        /// <summary>
        /// 走 CoinColor.TryGetSprite 的话，拿到的兜底实例 type 是 GOLD，会查出金色图。
        /// 所以这条路径直接按 CoinModel 自己的颜色给图。
        /// </summary>
        private static bool Prefix_CoinModel_GetCoinSprite(CoinModel __instance,
                                                           COIN_UI_STATE coinUIState,
                                                           bool autoCheckState,
                                                           ref Sprite __result)
        {
            try
            {
                if (__instance == null) return true;
                CoinDef def;
                if (!CoinRegistry.TryGetByValue((int)__instance.GetCoinColor(), out def)) return true;
                var sprite = CoinArt.Get(def, coinUIState);
                if (sprite == null) return true;
                __result = sprite;
                return false;
            }
            catch (Exception e)
            {
                LogThrottled("CoinModel.GetCoinSprite prefix 异常", e);
                return true;
            }
        }

        // ------------------------------------------------------------------ 3) 效果

        /// <summary>
        /// Parrying 是拼点主循环里唯一同时拿到「双方 action + 回合序号 + 胜负日志 + ref ParryingStatus」
        /// 的地方，所以在这里收尾。
        ///
        /// 这里跑两套互不相干的效果：
        ///   1) SelfRemoveCoinIds（自删，CYAN）—— 不看胜负，双方各判各的，见 TryRemoveOwnEffectCoin；
        ///   2) ActiveEffectIds（摧毁，ORANGE）—— 只有赢家触发，见 ApplyEffect。
        ///
        /// 循环终止条件只看 actorParryingLife / opponentParryingLife / count>=99，
        /// 不看硬币数 —— 把败方 life 写 0，否则赢家会对着没硬币的对手空转，
        /// GetDestroyTargetCoin 会走进 LogError 分支。
        /// </summary>
        private static void Postfix_Parrying(BattleActionModel actorAction,
                                             BattleActionModel oppoAction,
                                             BattleLog_Parrying parryingLog,
                                             int parryingCount,
                                             ref ParryingStatus parryingStatus)
        {
            try
            {
                if (parryingLog == null || actorAction == null || oppoAction == null) return;

                PARRYING_RESULT result = parryingLog.ACharacterResult;

                if (ModConfig.Verbose.Value)
                    DumpCoins(actorAction, oppoAction, parryingCount, result);

                // 1) 自删：不论输赢（平局也算"拼点了一次"），双方各自检查自己的技能。
                //    必须放在判胜负之前，否则 DRAW 时会被 return 掉。
                TryRemoveOwnEffectCoin(actorAction, true, parryingCount, ref parryingStatus);
                TryRemoveOwnEffectCoin(oppoAction, false, parryingCount, ref parryingStatus);

                // 2) 摧毁：只有赢家触发。
                BattleActionModel winner, loser;
                bool loserIsOpponent;

                if (result == PARRYING_RESULT.WIN)
                {
                    winner = actorAction; loser = oppoAction; loserIsOpponent = true;
                }
                else if (result == PARRYING_RESULT.LOSE)
                {
                    winner = oppoAction; loser = actorAction; loserIsOpponent = false;
                }
                else
                {
                    return; // DRAW / NONE
                }

                CoinDef def;
                var coin = FindEffectCoin(winner, out def);
                if (coin == null || def == null) return;
                if (def.Effect != CoinEffect.DestroyAllCoins) return; // 自删色由上面那条分支负责

                IntPtr key = SafePointer(coin);
                if (key == IntPtr.Zero) return;
                if (!Triggered.Add(key)) return; // 这枚币已经赢过一次了
                if (Triggered.Count > 512) Triggered.Clear();

                ApplyEffect(def, loser, loserIsOpponent, ref parryingStatus, parryingCount);
            }
            catch (Exception e)
            {
                LogThrottled("Parrying postfix 异常", e);
            }
        }

        /// <summary>
        /// 自删效果（CYAN）：本技能每参与一次拼点，就把**自己**第一枚该色硬币从技能里删掉。
        /// 一次拼点只删一枚；删完就没有了，下一回合再拼就轮到下一枚。
        ///
        /// 删除用官方原语 SkillModel.RemoveCoinModelFromListByIndex(idx) @ 0x18126F9F0（public、非虚）：
        ///     _coinList.RemoveAt(idx)  →  返回被删的 CoinModel  →  _coinListCacheDirty = 1
        /// 和游戏自己 RemoveAllTempCoins 删临时币是同一套范式。
        ///
        /// ⚠ 下标必须是 _coinList（原始列表）的下标。get_CoinList() 返回的其实是过滤缓存
        /// _validCoinListCache（会跳过 CoinStatusManager.IsValidCoin 判 false 的币），
        /// 两边下标并不一一对应 —— 所以这里直接在 _coinList 上遍历。
        /// </summary>
        private static void TryRemoveOwnEffectCoin(BattleActionModel action, bool isActor,
                                                   int round, ref ParryingStatus parryingStatus)
        {
            if (action == null) return;
            var skill = action.Skill;
            if (skill == null) return;

            var coins = skill._coinList;
            if (coins == null) return;

            for (int i = 0; i < coins.Count; i++)
            {
                var coin = coins[i];
                if (coin == null) continue;

                CoinDef def;
                if (!CoinRegistry.TryGetByValue((int)coin.GetCoinColor(), out def)) continue;
                if (def.Effect != CoinEffect.RemoveSelfFirstCoin) continue;

                RemoveOwnCoin(skill, coin, i, def, isActor, round, ref parryingStatus);
                return; // 一次拼点只删一枚
            }
        }

        /// <summary>
        /// 执行删除，并同步修正该方的 parryingLife。
        ///
        /// ★ life 必须跟着改，否则决斗循环会多跑一回合：
        ///   Duel 里 actorParryingLife 初值 = SkillModel.GetCoinNum() = 硬币数，
        ///   循环只看 life 归没归 0，不看还有没有币。删一枚币 = 该方可战之币 -1，life 也得 -1，
        ///   不然下一回合 GetDestroyTargetCoin 会取不到币（它返回 null 不崩，但那一回合是空转）。
        ///
        /// ★ 但只在被删那枚当时还 IsUsableInDuel 时才 -1：
        ///   如果它正好是本回合刚被 CheckCoinDestroy 打掉的那枚，life 已经为它减过一次了，
        ///   再减就变成"一枚币扣两条命"，这方会平白少打一回合。
        ///   所以删之前先把 wasUsable 读出来。
        /// </summary>
        private static void RemoveOwnCoin(SkillModel skill, CoinModel coin, int index, CoinDef def,
                                          bool isActor, int round, ref ParryingStatus parryingStatus)
        {
            bool wasUsable = coin.IsUsableInDuel;
            string side = isActor ? "A" : "B";

            Log(LogLevel.Info, def.Id + " 第 " + round + " 回合拼点结束（" + side + " 方）-> 删除本技能第 "
                + index + " 枚硬币，删前仍可上场=" + wasUsable);

            if (!ModConfig.RemoveSelfFirstCoin.Value) return;

            if (ModConfig.SelfRemoveMode.Value != 0)
            {
                // 兜底模式：不真删，走和 ORANGE 摧毁对手一样的标记法（硬币留在列表里，显示破碎态）。
                coin.SetDestroyedState(true);
                coin._isUsableInDuel = false;
                if (wasUsable) DecrementParryingLife(isActor, ref parryingStatus);
                return;
            }

            var removed = skill.RemoveCoinModelFromListByIndex(index);
            if (removed == null)
            {
                Log(LogLevel.Warning, "RemoveCoinModelFromListByIndex(" + index
                    + ") 返回 null（下标越界或列表已变），本次不删除。");
                return;
            }

            if (wasUsable) DecrementParryingLife(isActor, ref parryingStatus);

            if (ModConfig.Verbose.Value)
            {
                var left = skill._coinList;
                Log(LogLevel.Info, "  删除完成，" + side + " 方剩余硬币 "
                    + (left == null ? -1 : left.Count) + " 枚，life=" + LifeOf(isActor, parryingStatus));
            }
        }

        /// <summary>该方 parryingLife 减一（不低于 0）。Orgin 那两个字段不动 —— 它们只进日志/UI。</summary>
        private static void DecrementParryingLife(bool isActor, ref ParryingStatus parryingStatus)
        {
            if (isActor)
            {
                if (parryingStatus.actorParryingLife > 0) parryingStatus.actorParryingLife--;
            }
            else
            {
                if (parryingStatus.opponentParryingLife > 0) parryingStatus.opponentParryingLife--;
            }
        }

        private static int LifeOf(bool isActor, ParryingStatus parryingStatus)
        {
            return isActor ? parryingStatus.actorParryingLife : parryingStatus.opponentParryingLife;
        }

        /// <summary>
        /// 按颜色的 CoinEffect 分发 —— 只管「赢家才触发」的那套效果。
        /// 不看胜负的效果（RemoveSelfFirstCoin）不在这里，见 TryRemoveOwnEffectCoin。
        ///
        /// 占位色走 default 分支 —— 只提示一次，不做任何事。
        /// 以后给某个颜色填"赢了才触发"的真实特性：在 CoinEffect 加枚举值，在这里加一个 case。
        /// </summary>
        private static void ApplyEffect(CoinDef def, BattleActionModel loser, bool loserIsOpponent,
                                       ref ParryingStatus parryingStatus, int round)
        {
            switch (def.Effect)
            {
                case CoinEffect.DestroyAllCoins:
                    Log(LogLevel.Info, def.Id + " 首次拼点胜利（第 " + round + " 回合）-> 摧毁对手全部硬币");
                    if (!ModConfig.DestroyAllCoins.Value) return;

                    int destroyed = DestroyAllCoins(loser);
                    if (loserIsOpponent) parryingStatus.opponentParryingLife = 0;
                    else parryingStatus.actorParryingLife = 0;

                    Log(LogLevel.Info, "已摧毁 " + destroyed + " 枚硬币，并将败方 parryingLife 置 0 结束决斗");
                    break;

                case CoinEffect.RemoveSelfFirstCoin:
                    // 自删色不看胜负，由 TryRemoveOwnEffectCoin 在每次拼点后统一处理。
                    // 这里必须有个空 case —— 否则会掉进 default 被当成"占位色"打误导日志。
                    break;

                default:
                    // 占位色：注册、出图都正常，只是还没有功能特性。每种色只提示一次，避免刷屏。
                    if (PlaceholderLogged.Add(def.Id))
                    {
                        Log(LogLevel.Info, def.Id + " 拼点胜利（第 " + round + " 回合）—— 该色暂无效果（占位）。"
                            + "想让它套用现成效果，就把 " + def.Id + " 加进配置 Effect.ActiveEffectIds（赢了摧毁对手全部）"
                            + "或 Effect.SelfRemoveCoinIds（每拼点一次自删一枚）；"
                            + "要给它专属特性，在 CoinEffect 加枚举值后到 ApplyEffect 补一个 case。");
                    }
                    break;
            }
        }

        /// <summary>
        /// 找"本回合上场的那枚硬币"是不是 DestroyAllCoins 那套效果的自定义色，并把定义带出来。
        /// 场上（未摧毁且仍可用于拼点）的第一枚就是本回合上场的那枚 —— 硬币按顺序消耗。
        /// TriggerMode=1 时放宽成"技能里任意一枚存活的该类硬币"。
        ///
        /// 只认 DestroyAllCoins：占位色（None）不参与触发；
        /// RemoveSelfFirstCoin 不看胜负，走 TryRemoveOwnEffectCoin，不能在这儿被"赢家"条件挡住。
        /// </summary>
        private static CoinModel FindEffectCoin(BattleActionModel action, out CoinDef found)
        {
            found = null;
            var skill = action.Skill;
            if (skill == null) return null;
            var coins = skill.CoinList;
            if (coins == null) return null;

            bool loose = ModConfig.TriggerMode.Value != 0;
            for (int i = 0; i < coins.Count; i++)
            {
                var c = coins[i];
                if (c == null) continue;
                if (c.IsDestroyed) continue;
                if (!c.IsUsableInDuel) continue;

                CoinDef def;
                bool isEffect = CoinRegistry.TryGetByValue((int)c.GetCoinColor(), out def)
                                && def.Effect == CoinEffect.DestroyAllCoins;
                if (!loose)
                {
                    // 严格模式：只认本回合上场的那枚
                    found = isEffect ? def : null;
                    return isEffect ? c : null;
                }
                if (isEffect)
                {
                    found = def;
                    return c;
                }
            }
            return null;
        }

        private static int DestroyAllCoins(BattleActionModel action)
        {
            var skill = action.Skill;
            if (skill == null) return 0;
            var coins = skill.GetAliveCoins();
            if (coins == null) return 0;

            int n = 0;
            for (int i = 0; i < coins.Count; i++)
            {
                var c = coins[i];
                if (c == null) continue;
                c.SetDestroyedState(true);
                // 关键：SetDestroyedState 只管 _isDestroyed。不把 _isUsableInDuel 也置 false，
                // 下一回合它照样会被选上场。
                c._isUsableInDuel = false;
                n++;
            }
            return n;
        }

        // ------------------------------------------------------------------ 工具

        private static void DumpCoins(BattleActionModel a, BattleActionModel b,
                                      int round, PARRYING_RESULT result)
        {
            var sb = new StringBuilder();
            sb.Append(Tag).Append("round ").Append(round).Append("  ACharacterResult=").Append(result).Append('\n');
            AppendSide(sb, "A", a);
            AppendSide(sb, "B", b);
            Log(LogLevel.Info, sb.ToString());
        }

        private static void AppendSide(StringBuilder sb, string label, BattleActionModel action)
        {
            sb.Append("  ").Append(label).Append(": ");
            var skill = action.Skill;
            if (skill == null) { sb.Append("(no skill)"); return; }
            var coins = skill.CoinList;
            if (coins == null) { sb.Append("(no coins)"); return; }
            for (int i = 0; i < coins.Count; i++)
            {
                var c = coins[i];
                if (c == null) continue;
                sb.Append('[').Append(i).Append(']')
                  .Append(CoinRegistry.NameOf((int)c.GetCoinColor()))
                  .Append(c.IsDestroyed ? " x" : " -")
                  .Append(c.IsUsableInDuel ? "u" : "-")
                  .Append(' ');
            }
        }

        private static IntPtr SafePointer(Il2CppObjectBase obj)
        {
            try
            {
                if (obj == null || obj.WasCollected) return IntPtr.Zero;
                return obj.Pointer;
            }
            catch
            {
                return IntPtr.Zero;
            }
        }

        private static void Log(LogLevel level, string message)
        {
            var log = StrikeCoinPlugin.LogInstance;
            if (log == null) return;
            log.Log(level, Tag + message);
        }

        private static void LogThrottled(string what, Exception e)
        {
            if (_errorBudget <= 0) return;
            _errorBudget--;
            Log(LogLevel.Error, what + ": " + e);
            if (_errorBudget == 0) Log(LogLevel.Error, "同类错误过多，后续不再打印。");
        }

        private static void LogThrottled(string message)
        {
            if (_errorBudget <= 0) return;
            _errorBudget--;
            Log(LogLevel.Error, message);
            if (_errorBudget == 0) Log(LogLevel.Error, "同类错误过多，后续不再打印。");
        }
    }
}
