using System;
using System.Collections.Generic;

namespace ModbusSimulator
{
    /// <summary>卡片身份使用数据区和绝对协议地址，与当前显示顺序无关。</summary>
    public sealed class CardKey
    {
        public int Area { get; set; }
        public int Address { get; set; }
    }
    public static class CardLayout
    {
        // 数据区编号保留协议含义，只调整卡片默认显示顺序。
        private static readonly int[] DefaultAreas = { 2, 3, 0, 1 };

        private static CardKey Copy(CardKey key)
        { return new CardKey { Area = key.Area, Address = key.Address }; }

        private static bool IsValid(CardKey key)
        { return key != null && key.Area >= 0 && key.Area < 4 && key.Address >= 0 && key.Address < RegisterBank.Capacity; }

        // 16位地址空间和两位数据区编码不重叠，避免字符串分配和模糊的地址比较。
        private static int Identity(CardKey key)
        { return key.Area * RegisterBank.Capacity + key.Address; }

        private static void Validate(RegisterBank bank, Func<int, int, ParseRule> ruleAt)
        {
            if (bank == null) throw new ArgumentNullException("bank");
            if (ruleAt == null) throw new ArgumentNullException("ruleAt");
        }

        /// <summary>仅扫描可见头地址的规则；隐藏地址规则和原始寄存器值均不修改。
        /// 自定义顺序只应用到仍然可见的头，未指定的卡片按默认顺序追加。</summary>
        public static List<CardKey> Build(RegisterBank bank, Func<int, int, ParseRule> ruleAt, IEnumerable<CardKey> customOrder)
        {
            Validate(bank, ruleAt);
            List<CardKey> defaults = new List<CardKey>();
            foreach (int area in DefaultAreas)
            {
                int start = bank.Start(area), end = start + bank.Count(area);
                for (int address = start; address < end; )
                {
                    defaults.Add(new CardKey { Area = area, Address = address });
                    // 步进允许越过配置末尾，循环在下一次判断即结束；不补造尾部地址。
                    address += area < 2 ? 1 : ValueCodec.WordCount(ruleAt(area, address));
                }
            }
            if (customOrder == null) return defaults;

            HashSet<int> visible = new HashSet<int>();
            foreach (CardKey key in defaults) visible.Add(Identity(key));
            HashSet<int> selected = new HashSet<int>();
            List<CardKey> result = new List<CardKey>(defaults.Count);
            foreach (CardKey key in customOrder)
            {
                if (!IsValid(key)) continue;
                int identity = Identity(key);
                if (visible.Contains(identity) && selected.Add(identity)) result.Add(Copy(key));
            }
            foreach (CardKey key in defaults)
                if (selected.Add(Identity(key))) result.Add(key);
            return result;
        }

        /// <summary>将任意合法原始地址定位至覆盖它的可见头地址。
        /// 尾部不足时，只将实际存在的尾寄存器映射到头。</summary>
        public static CardKey OwnerOf(RegisterBank bank, Func<int, int, ParseRule> ruleAt, int area, int address)
        {
            Validate(bank, ruleAt);
            if (area < 0 || area > 3) throw new ArgumentOutOfRangeException("area");
            int start = bank.Start(area), end = start + bank.Count(area);
            if (address < start || address >= end) throw new ArgumentOutOfRangeException("address", "地址不在当前配置的数据区内。");
            if (area < 2) return new CardKey { Area = area, Address = address };
            for (int head = start; head < end; )
            {
                int next = head + ValueCodec.WordCount(ruleAt(area, head));
                if (address < next) return new CardKey { Area = area, Address = head };
                head = next;
            }
            throw new ArgumentException("无法定位地址所属卡片。", "address");
        }

        /// <summary>返回把源卡片移到目标之前的新列表；输入列表和键均不修改。
        /// 全列表先校验，确保即使自拖也不会默许重复或非法键。</summary>
        public static List<CardKey> MoveBefore(IList<CardKey> current, CardKey source, CardKey target)
        {
            if (current == null) throw new ArgumentNullException("current");
            if (!IsValid(source)) throw new ArgumentException("源卡片键无效。", "source");
            if (!IsValid(target)) throw new ArgumentException("目标卡片键无效。", "target");
            int sourceId = Identity(source), targetId = Identity(target);
            bool sourceFound = false, targetFound = false;
            HashSet<int> seen = new HashSet<int>();
            for (int i = 0; i < current.Count; i++)
            {
                CardKey key = current[i];
                if (!IsValid(key) || !seen.Add(Identity(key))) throw new ArgumentException("当前卡片列表包含无效或重复的键。", "current");
                int identity = Identity(key);
                if (identity == sourceId) sourceFound = true;
                if (identity == targetId) targetFound = true;
            }
            if (!sourceFound) throw new ArgumentException("源卡片不在当前列表中。", "source");
            if (!targetFound) throw new ArgumentException("目标卡片不在当前列表中。", "target");

            List<CardKey> result = new List<CardKey>(current.Count);
            for (int i = 0; i < current.Count; i++)
            {
                CardKey key = current[i];
                int identity = Identity(key);
                if (sourceId != targetId)
                {
                    if (identity == sourceId) continue;
                    if (identity == targetId) result.Add(Copy(source));
                }
                result.Add(Copy(key));
            }
            return result;
        }
    }
}
