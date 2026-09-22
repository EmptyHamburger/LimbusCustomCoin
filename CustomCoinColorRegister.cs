using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Lethe;

namespace StrikeCoin;

public static class CustomCoinColorRegister
{
    public static Dictionary<string, int> lookUpDict = new(StringComparer.OrdinalIgnoreCase);
    
    public static void BuildLookUpTable()
    {
        lookUpDict.Clear();
        CoinArt.ScanModSprites();

        foreach(object rEnum in Enum.GetValues(typeof(COIN_COLOR_TYPE)))
        {
            string enumName = Enum.GetName(typeof(COIN_COLOR_TYPE), rEnum);
            if (!string.IsNullOrEmpty(enumName)) lookUpDict[enumName] = Convert.ToInt32(rEnum);
        }

        foreach(CoinDef def in CoinRegistry.Defs)
        if (!lookUpDict.ContainsKey(def.Id)) lookUpDict[def.Id] = def.Value;

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

                if (CoinColorAllocator.IsOfficialName(colorName) || lookUpDict.ContainsKey(colorName)) continue;
                
                if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value, out int colorId))
                {
                    CoinRegistry.RegisterCustomCoinColor(colorName, colorId);
                    lookUpDict[colorName] = colorId;
                }
                else
                {
                    CoinRegistry.RegisterCustomCoinColor(colorName, null);
                    if (CoinRegistry.TryGetById(colorName, out CoinDef def))
                    lookUpDict[colorName] = def.Value;
                }
            }

            foreach(KeyValuePair<string, int> pair in lookUpDict)
            {
                StrikeCoinPlugin.LogInstance.LogInfo($"Registered lookUp | {pair.Key}: {pair.Value}");
            }
        }
    }
}