using ModularSkillScripts;

namespace StrikeCoin;

public class AcquirerGetCoinColorId : IModularAcquirer
{
    public int ExecuteAcquirer(ModularSA modular, string section, string circledSection, string[] circles)
    {
        if (modular.modsa_coinModel == null) return -1;
        return CustomCoinColorRegister.lookUpDictById.TryGetValue((int)modular.modsa_coinModel._classInfo._coinColorType, out _) ? (int)modular.modsa_coinModel._classInfo._coinColorType : -1;
    }
}