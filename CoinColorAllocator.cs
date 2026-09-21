using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using BepInEx.Logging;

namespace StrikeCoin
{
    /// <summary>
    /// 给 StrikeCoin 挑一个"官方没占用"的 COIN_COLOR_TYPE 数值。
    ///
    /// 为什么不能再写死 5：官方只要新增硬币颜色（下一个很可能正好叫 ORANGE），
    /// 我们的值就和官方撞车 —— 官方橙币会套上我们的图，还会触发"摧毁对手全部硬币"。
    /// 所以启动时扫一遍官方已占用的值，取 max+1；官方下次再加，我们自动让开。
    ///
    /// 这个数值不会被写进静态数据（静态数据里只存字符串标识），
    /// 纯运行时产物，所以每次启动重算都是安全的，不存在存档/数据迁移问题。
    /// </summary>
    internal static class CoinColorAllocator
    {
        private const int ScanLimit = 4096;

        /// <summary>扫描彻底失败时沿用的历史值（原版 0..4 已占用，5 是第一个空位）。</summary>
        private const int LegacyFallback = 5;

        private static readonly Dictionary<string, int> ByName =
            new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<int> ByValue = new HashSet<int>();

        private static int _max = -1;
        private static bool _viaReflection;

        /// <summary>扫一遍官方枚举，建立「名字 / 数值」占用表。必须在 Init 之前调用一次。</summary>
        internal static void Scan()
        {
            ByName.Clear();
            ByValue.Clear();
            _max = -1;
            _viaReflection = false;

            try
            {
                Type type = typeof(COIN_COLOR_TYPE);
                foreach (object raw in Enum.GetValues(type))
                {
                    int value = Convert.ToInt32(raw, CultureInfo.InvariantCulture);
                    Register(Enum.GetName(type, raw), value);
                }
                _viaReflection = ByName.Count > 0;
            }
            catch (Exception e)
            {
                Log(LogLevel.Warning, "反射 COIN_COLOR_TYPE 失败，改用数值扫描: " + e.Message);
            }

            if (_viaReflection) return;

            // 兜底扫描：未定义的值 ToString() 会得到纯数字字符串，有名字的才是官方占用。
            for (int i = 0; i < ScanLimit; i++)
            {
                string name = ((COIN_COLOR_TYPE)i).ToString();
                if (string.IsNullOrEmpty(name)) continue;
                if (IsNumeric(name)) continue;
                Register(name, i);
            }
        }

        private static void Register(string name, int value)
        {
            ByValue.Add(value);
            if (value > _max) _max = value;
            if (!string.IsNullOrEmpty(name) && !ByName.ContainsKey(name)) ByName.Add(name, value);
        }

        /// <summary>扫描是否成功（失败时 Allocate 只能退回历史值）。</summary>
        internal static bool Scanned
        {
            get { return _max >= 0; }
        }

        /// <summary>
        /// 取一个官方没占用的值（官方最大值 + 1，且再校验一遍确实没被占），
        /// 并且**立刻占住** —— 注册多种颜色时连着调 N 次会拿到 N 个不同的值。
        /// </summary>
        internal static int Allocate()
        {
            if (_max < 0)
            {
                // 扫描失败时从历史值开始往后找空位，保证多次调用仍互不相同。
                int legacy = LegacyFallback;
                while (ByValue.Contains(legacy)) legacy++;
                Reserve(legacy);
                return legacy;
            }
            int value = _max + 1;
            while (value < ScanLimit && ByValue.Contains(value)) value++;
            Reserve(value);
            return value;
        }

        /// <summary>把某个值标记为已占用，后续 Allocate 会跳过它。</summary>
        internal static void Reserve(int value)
        {
            ByValue.Add(value);
        }

        internal static bool IsOfficialValue(int value)
        {
            return ByValue.Contains(value);
        }

        internal static bool IsOfficialName(string name)
        {
            return !string.IsNullOrEmpty(name) && ByName.ContainsKey(name);
        }

        /// <summary>
        /// 纯数字字符串（如 "0" / "3"）。Enum.Parse 认这种写法，
        /// 所以不能把它当"未知颜色"兜底掉，否则 "1"（GREY）会被误判。
        /// </summary>
        internal static bool IsNumericLiteral(string s)
        {
            return !string.IsNullOrEmpty(s) && IsNumeric(s);
        }

        /// <summary>官方同名时它自己的数值，用于告警文案。</summary>
        internal static int ValueOf(string name)
        {
            int value;
            return ByName.TryGetValue(name, out value) ? value : -1;
        }

        /// <summary>启动日志用：把官方成员清单打成 "GOLD=0 GREY=1 ..."。</summary>
        internal static string Describe()
        {
            var sb = new StringBuilder();
            var sorted = new List<KeyValuePair<string, int>>(ByName);
            sorted.Sort((a, b) => a.Value.CompareTo(b.Value));
            foreach (var kv in sorted)
            {
                sb.Append(kv.Key).Append('=').Append(kv.Value).Append(' ');
            }
            return sb.Length == 0 ? "(扫描失败)" : sb.ToString(0, sb.Length - 1);
        }

        private static bool IsNumeric(string s)
        {
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                bool digit = (c >= '0' && c <= '9') || (i == 0 && (c == '-' || c == '+'));
                if (!digit) return false;
            }
            return s.Length > 0;
        }

        private static void Log(LogLevel level, string message)
        {
            var log = StrikeCoinPlugin.LogInstance;
            if (log != null) log.Log(level, "[StrikeCoin] " + message);
        }
    }
}
