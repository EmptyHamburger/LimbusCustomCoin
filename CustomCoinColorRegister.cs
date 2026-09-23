using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Lethe;

namespace StrikeCoin;

public static class CustomCoinColorRegister
{
    public static Dictionary<string, int> lookUpDictByName = new(StringComparer.OrdinalIgnoreCase);
    public static Dictionary<int, string> lookUpDictById = new();
    
    public static void BuildLookUpTable()
    {
        lookUpDictByName.Clear();
        CoinArt.ScanModSprites();

        foreach(object rEnum in Enum.GetValues(typeof(COIN_COLOR_TYPE)))
        {
            string enumName = Enum.GetName(typeof(COIN_COLOR_TYPE), rEnum);
            if (!string.IsNullOrEmpty(enumName)) lookUpDictByName[enumName] = Convert.ToInt32(rEnum);
        }

        foreach(CoinDef def in CoinRegistry.Defs)
        if (!lookUpDictByName.ContainsKey(def.Id)) lookUpDictByName[def.Id] = def.Value;

        foreach (string modPath in Directory.GetDirectories(LetheMain.modsPath.FullPath))
        {
            string modName = Path.GetFileName(modPath);
            if (modName.StartsWith("FULLDISABLED_")) continue;

            var path = Path.Combine(modPath, "custom_coin_colors");
            if (!Directory.Exists(path)) continue;

            List<string> fileList = new();
            fileList.AddRange(Directory.GetFiles(path, "*.txt", SearchOption.AllDirectories));

            foreach(string filePath in fileList)
            {
                string fileName = Path.GetFileNameWithoutExtension(filePath);
                Match match = Regex.Match(fileName, @"([a-zA-Z]+)_?(\d*)");

                if (!match.Groups[1].Success) continue;
                string colorName = match.Groups[1].Value.ToUpper();

                if (CoinColorAllocator.IsOfficialName(colorName) || lookUpDictByName.ContainsKey(colorName)) continue;
                
                if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out int colorId))
                {
                    CoinRegistry.RegisterCustomCoinColor(colorName, colorId);
                    lookUpDictByName[colorName] = colorId;
                }
                else
                {
                    CoinRegistry.RegisterCustomCoinColor(colorName, null);
                    if (CoinRegistry.TryGetById(colorName, out CoinDef def))
                    lookUpDictByName[colorName] = def.Value;
                }
            }
            lookUpDictById = lookUpDictByName.GroupBy(pair => pair.Value).ToDictionary(group => group.Key, group => group.First().Key);

            foreach(KeyValuePair<string, int> pair in lookUpDictByName)
            {
                StrikeCoinPlugin.LogInstance.LogInfo($"Registered lookUp | {pair.Key}: {pair.Value}");
            }
        }
    }
}