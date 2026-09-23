using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using ModularSkillScripts;

namespace StrikeCoin
{
    [BepInPlugin("com.limbusmods.strikecoin", "StrikeCoin", "1.2.1")]
    [BepInDependency("Lethe")]
    [BepInDependency("GlitchGames.ModularSkillScripts")]
    public class StrikeCoinPlugin : BasePlugin
    {
        internal static ManualLogSource LogInstance;

        public override void Load()
        {
            LogInstance = Log;
            ModConfig.Init(Config);

            // 美术内嵌在 DLL 里，这里只是建索引 + 把资源清单打进日志。
            CoinArt.Init();

            // 先扫官方枚举、给每种自定义色分配值（顺带做官方新增硬币的兼容性自检），再装 hook。
            Patches.Init();

            CustomCoinColorRegister.BuildLookUpTable();

            var harmony = new Harmony("com.limbusmods.strikecoin");
            Patches.Apply(harmony);

            LogInstance.LogInfo("[StrikeCoin] loaded v1.2.1. 自定义色: " + CoinRegistry.Describe()
                + " | CoinColorBaseValue=" + ModConfig.CoinColorBaseValue.Value
                + " ActiveEffectIds=" + ModConfig.ActiveEffectIds.Value
                + " TriggerMode=" + ModConfig.TriggerMode.Value
                + " FallbackUnknownColor=" + ModConfig.FallbackUnknownColor.Value);

            ModularSkillScripts.MainClass.acquirerDict["getcoincolorid"] = new AcquirerGetCoinColorId();
        }
    }
}
