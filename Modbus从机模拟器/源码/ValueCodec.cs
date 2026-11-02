using System;
namespace ModbusSimulator
{
    /// <summary>单项解析规则。显示值 = Apply(原始值, Operation, Operand) + Offset。
    /// Decimals 和 Unit 只控制界面格式，不参与数值舍入或传输。</summary>
    [Serializable]
    public sealed class ParseRule
    {
        public string DataType { get; set; }
        public string ByteOrder { get; set; }
        public string Operation { get; set; }
        public double Operand { get; set; }
        public double Offset { get; set; }
        public int Decimals { get; set; }
        public string Unit { get; set; }
        public ParseRule() { DataType = "UInt16"; ByteOrder = "ABCD"; Operation = "*"; Operand = 1; Decimals = 3; Unit = ""; }
        public void Validate()
        {
            if (DataType != "UInt16" && DataType != "Int16" && DataType != "UInt32" &&
                DataType != "Int32" && DataType != "Float32" && DataType != "Float64")
                throw new ArgumentException("不支持的数据类型：" + DataType);
            if (ByteOrder != "ABCD" && ByteOrder != "BADC" && ByteOrder != "CDAB" && ByteOrder != "DCBA")
                throw new ArgumentException("字节顺序必须是 ABCD、BADC、CDAB 或 DCBA。");
            if (Operation != "+" && Operation != "-" && Operation != "*" && Operation != "/")
                throw new ArgumentException("运算符必须是 +、-、* 或 /。");
            if (!ValueCodec.IsFinite(Operand) || !ValueCodec.IsFinite(Offset))
                throw new ArgumentException("运算参数和偏移量必须是有限数值。");
            if (Operand == 0 && (Operation == "/" || Operation == "*"))
                throw new ArgumentException(Operation == "/" ? "除数不能为 0。" : "乘数不能为 0，否则无法逆换算写回。");
            if (Decimals < 0 || Decimals > 12) throw new ArgumentException("小数位数必须在 0～12 之间。");
            if (Unit == null) throw new ArgumentException("单位不能为 null，可使用空字符串。");
        }
    }
    public static class ValueCodec
    {
        internal static bool IsFinite(double value) { return !double.IsNaN(value) && !double.IsInfinity(value); }

        /// <summary>类型占用的连续 16 位寄存器数。所有入口均校验完整规则。</summary>
        public static int WordCount(ParseRule rule)
        {
            if (rule == null) throw new ArgumentNullException("rule");
            rule.Validate();
            return rule.DataType == "Float64" ? 4 : (rule.DataType == "UInt16" || rule.DataType == "Int16" ? 1 : 2);
        }

        // 在规范大端字节索引与设备字节索引间建立映射；四种映射均自反。
        // Float64: ABCDEFGH / BADCFEHG / GHEFCDAB / HGFEDCBA。
        // 单个寄存器没有字交换；BADC/DCBA 交换该寄存器内的两个字节。
        private static int MappedIndex(int index, int length, string order)
        {
            bool reverseWords = order == "CDAB" || order == "DCBA";
            bool swapBytes = order == "BADC" || order == "DCBA";
            int word = index / 2, byteInWord = index % 2;
            if (reverseWords) word = length / 2 - 1 - word;
            if (swapBytes) byteInWord = 1 - byteInWord;
            return word * 2 + byteInWord;
        }

        /// <summary>从起始位置解析所需寄存器，多余尾部数据忽略，缺少尾寄存器报错。
        /// 设备已有 NaN/Infinity 保留给界面明确显示；Encode 永远禁止写入它们。</summary>
        public static double Decode(ushort[] words, ParseRule rule)
        {
            int count = WordCount(rule);
            if (words == null) throw new ArgumentNullException("words");
            if (words.Length < count) throw new ArgumentException("尾部寄存器不足：" + rule.DataType + " 需要连续 " + count + " 个寄存器，实际只有 " + words.Length + " 个。");
            byte[] device = new byte[count * 2];
            for (int i = 0; i < count; i++) { device[i * 2] = (byte)(words[i] >> 8); device[i * 2 + 1] = (byte)words[i]; }
            ulong bits = 0;
            for (int i = 0; i < device.Length; i++) bits = (bits << 8) | device[MappedIndex(i, device.Length, rule.ByteOrder)];
            double raw;
            switch (rule.DataType)
            {
                case "Int16": raw = unchecked((short)bits); break;
                case "UInt16": raw = (ushort)bits; break;
                case "Int32": raw = unchecked((int)bits); break;
                case "UInt32": raw = (uint)bits; break;
                case "Float32": raw = BitConverter.ToSingle(BitConverter.GetBytes((uint)bits), 0); break;
                default: raw = BitConverter.Int64BitsToDouble(unchecked((long)bits)); break;
            }
            return ApplyRaw(raw, rule);
        }

        // 解码、边界识别和可逆性校验共用同一正向运算，避免浮点求值顺序不同。
        private static double ApplyRaw(double raw, ParseRule rule)
        {
            double transformed;
            switch (rule.Operation)
            {
                case "+": transformed = raw + rule.Operand; break;
                case "-": transformed = raw - rule.Operand; break;
                case "*": transformed = raw * rule.Operand; break;
                default: transformed = raw / rule.Operand; break;
            }
            return transformed + rule.Offset;
        }

        private static void RequireDistinctInteger(double raw, double min, double max, ParseRule rule)
        {
            double forward = ApplyRaw(raw, rule);
            if (!IsFinite(forward) || (raw > min && forward == ApplyRaw(raw - 1, rule)) ||
                (raw < max && forward == ApplyRaw(raw + 1, rule)))
                throw new ArgumentException("当前运算规则在此整数附近发生精度塌缩，无法唯一逆换算写回。");
        }

        /// <summary>显示值逆换算为原始值，并生成完整寄存器数组。
        /// 调用方应先检查区域边界，再一次性原子写入全部寄存器。</summary>
        public static ushort[] Encode(double displayValue, ParseRule rule)
        {
            int count = WordCount(rule);
            if (!IsFinite(displayValue)) throw new ArgumentException("不允许写入 NaN 或 Infinity。");
            double adjusted = displayValue - rule.Offset;
            double raw;
            switch (rule.Operation)
            {
                case "+": raw = adjusted - rule.Operand; break;
                case "-": raw = adjusted + rule.Operand; break;
                case "*": raw = adjusted / rule.Operand; break;
                default: raw = adjusted * rule.Operand; break;
            }
            if (!IsFinite(raw)) throw new ArgumentException("逆换算结果溢出，不允许写入非有限数值。");
            ulong bits;
            if (rule.DataType == "Float64") bits = unchecked((ulong)BitConverter.DoubleToInt64Bits(raw));
            else if (rule.DataType == "Float32")
            {
                float value = (float)raw;
                if (float.IsNaN(value) || float.IsInfinity(value)) throw new ArgumentOutOfRangeException("displayValue", "逆换算结果超出 Float32 有限数值范围。");
                bits = BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
            }
            else
            {
                double min = 0, max = ushort.MaxValue;
                switch (rule.DataType)
                {
                    case "Int16": min = short.MinValue; max = short.MaxValue; break;
                    case "Int32": min = int.MinValue; max = int.MaxValue; break;
                    case "UInt32": max = uint.MaxValue; break;
                }
                if (raw < min || raw > max)
                {
                    double boundary = raw < min ? min : max;
                    // 合法边界经正向运算后，逆运算可能残留略负/略大的舍入误差。
                    // 仅输入与边界正向值完全相等时恢复精确边界，不采用区间宽容。
                    // 下方同时检查相邻整数可区分性，精度塌缩的映射不能据此写回。
                    if (displayValue != ApplyRaw(boundary, rule))
                        throw new ArgumentOutOfRangeException("displayValue", "逆换算结果超出 " + rule.DataType + " 范围 [" + min + ", " + max + "]。");
                    raw = boundary;
                }
                if (raw != Math.Truncate(raw))
                {
                    // 小数缩放的逆运算会产生有限的二进制舍入误差，例如 0.3/0.1。
                    // 仅实际执行过换算时允许原始量纲运算尺度的最多 4 ULP，
                    // 且绝对误差不超过 1e-9。尺度包含发生消减前的工程值/偏移，
                    // 防止合法零值逆换算后残留极小误差时，按残差自身 ULP 误拒绝。
                    // 范围已在此前检查，绝不通过近似吸附整数接受越界值。
                    bool transformed = rule.Offset != 0 ||
                        ((rule.Operation == "+" || rule.Operation == "-") ? rule.Operand != 0 : rule.Operand != 1);
                    double nearest = Math.Round(raw);
                    double magnitude = Math.Max(Math.Abs(raw), Math.Abs(nearest));
                    double displayScale = displayValue, offsetScale = rule.Offset;
                    if (rule.Operation == "*") { displayScale /= rule.Operand; offsetScale /= rule.Operand; }
                    else if (rule.Operation == "/") { displayScale *= rule.Operand; offsetScale *= rule.Operand; }
                    else magnitude = Math.Max(magnitude, Math.Max(Math.Abs(adjusted), Math.Abs(rule.Operand)));
                    // 将各级操作数换到原始值量纲，再估计二进制舍入误差的传播。
                    magnitude = Math.Max(magnitude, Math.Max(Math.Abs(displayScale), Math.Abs(offsetScale)));
                    double tolerance = 0;
                    if (IsFinite(magnitude))
                    {
                        double ulp = BitConverter.Int64BitsToDouble(BitConverter.DoubleToInt64Bits(magnitude) + 1) - magnitude;
                        tolerance = Math.Min(ulp * 4, 1e-9);
                    }
                    if (!transformed || Math.Abs(raw - nearest) > tolerance)
                        throw new ArgumentException("逆换算结果不是整数，无法写入 " + rule.DataType + "：" + raw.ToString("R", System.Globalization.CultureInfo.InvariantCulture));
                    raw = nearest;
                }
                RequireDistinctInteger(raw, min, max, rule);
                bits = unchecked((ulong)(long)raw);
            }
            byte[] device = new byte[count * 2];
            for (int i = 0; i < device.Length; i++)
                device[MappedIndex(i, device.Length, rule.ByteOrder)] = (byte)(bits >> ((device.Length - 1 - i) * 8));
            ushort[] words = new ushort[count];
            for (int i = 0; i < count; i++) words[i] = (ushort)((device[i * 2] << 8) | device[i * 2 + 1]);
            return words;
        }
    }
}
