using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;
using System.Collections.Generic;

namespace ModbusMaster
{
    public sealed class AreaConfiguration
    {
        public int Start { get; set; }
        public int Count { get; set; }
        public ushort[] Values { get; set; }
        public ParseRule DefaultRule { get; set; }
        public AddressRule[] ItemRules { get; set; }
        public AddressLabel[] ItemLabels { get; set; }
        public AreaConfiguration() { DefaultRule = new ParseRule(); ItemRules = new AddressRule[0]; ItemLabels = new AddressLabel[0]; }
    }
    public sealed class SimulatorConfiguration
    {
        public int Version { get; set; }
        public int Mode { get; set; }
        public string Ip { get; set; }
        public int Port { get; set; }
        public int Unit { get; set; }
        public string Com { get; set; }
        public int Baud { get; set; }
        public string Parity { get; set; }
        public string StopBits { get; set; }
        public int DataBits { get; set; }
        public AreaConfiguration[] Areas { get; set; }
        public CardKey[] CardOrder { get; set; }
        public SimulatorConfiguration()
        { Version = 4; DataBits = 8; Ip = "0.0.0.0"; Port = 502; Unit = 1; Com = ""; Baud = 9600; Parity = "None"; StopBits = "1"; CardOrder = new CardKey[0]; }
        public static SimulatorConfiguration Capture(RegisterBank bank)
        { return new SimulatorConfiguration { Areas = bank.Snapshot() }; }
        public void Validate()
        {
            System.Net.IPAddress address;
            if ((Version != 2 && Version != 3 && Version != 4) || Mode < 0 || Mode > 6 || (DataBits != 7 && DataBits != 8) || (Mode != 3 && DataBits != 8) || Unit < 1 || Unit > 247 || Port < 1 || Port > 65535 ||
                !System.Net.IPAddress.TryParse(Ip, out address) || Com == null || Com.Length > 80 ||
                Array.IndexOf(new int[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 }, Baud) < 0 ||
                (Parity != "None" && Parity != "Even" && Parity != "Odd") || (StopBits != "1" && StopBits != "2"))
                throw new ArgumentException("配置版本或通信参数无效。");
            if (Areas == null || Areas.Length != 4) throw new ArgumentException("配置必须包含四个独立数据区。");
            for (int i = 0; i < 4; i++)
            {
                AreaConfiguration area = Areas[i];
                if (area == null || area.Start < 0 || area.Start >= RegisterBank.Capacity || area.Count < 0 ||
                    area.Count > RegisterBank.Capacity - area.Start || area.Values == null || area.Values.Length != area.Count)
                    throw new ArgumentException("数据区 " + i + " 的地址、数量或数据长度无效。");
                if (i < 2) foreach (ushort value in area.Values)
                    if (value > 1) throw new ArgumentException("位对象的数据只能是 0 或 1。");
                if (area.DefaultRule == null) area.DefaultRule = new ParseRule();
                area.DefaultRule.Validate();
                if (area.DefaultRule.Unit.Length > 32) throw new ArgumentException("单位最多32个字符。");
                if (area.ItemRules == null) area.ItemRules = new AddressRule[0];
                if (area.ItemRules.Length > area.Count) throw new ArgumentException("单项规则数量超过数据区数量。");
                HashSet<int> addresses = new HashSet<int>();
                foreach (AddressRule item in area.ItemRules)
                {
                    if (item == null || item.Rule == null || item.Address < area.Start || item.Address >= area.Start + area.Count || !addresses.Add(item.Address))
                        throw new ArgumentException("单项解析规则的地址无效或重复。");
                    item.Rule.Validate();
                    if (item.Rule.Unit.Length > 32) throw new ArgumentException("单位最多32个字符。");
                }
                if (area.ItemLabels == null) area.ItemLabels = new AddressLabel[0];
                if (area.ItemLabels.Length > area.Count) throw new ArgumentException("标签数量超过数据区数量。");
                addresses.Clear();
                foreach (AddressLabel label in area.ItemLabels)
                {
                    if (label == null || label.Address < area.Start || label.Address >= area.Start + area.Count || !addresses.Add(label.Address))
                        throw new ArgumentException("卡片标签的地址无效或重复。");
                    label.Validate();
                }
            }
            // 顺序允许包含被多字解析覆盖的地址，恢复16位解析后仍可沿用；仅校验原始区间。
            if (CardOrder == null) CardOrder = new CardKey[0];
            HashSet<int> orderKeys = new HashSet<int>();
            foreach (CardKey key in CardOrder)
            {
                if (key == null || key.Area < 0 || key.Area > 3) throw new ArgumentException("自定义卡片顺序的数据区无效。");
                AreaConfiguration area = Areas[key.Area];
                if (key.Address < area.Start || key.Address >= area.Start + area.Count || !orderKeys.Add(key.Area * RegisterBank.Capacity + key.Address))
                    throw new ArgumentException("自定义卡片顺序的地址无效或重复。");
            }
        }
        public RegisterBank CreateBank()
        {
            Validate();
            RegisterBank result = new RegisterBank();
            for (int i = 0; i < 4; i++)
            {
                result.Configure(i, Areas[i].Start, Areas[i].Count);
                for (int j = 0; j < Areas[i].Count; j++) result.Set(i, Areas[i].Start + j, Areas[i].Values[j]);
            }
            return result;
        }
        public void Save(string path)
        {
            Validate();
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 33554432 };
            string payload = serializer.Serialize(this);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, payload, new UTF8Encoding(false));
                // 同目录临时文件，完整写出后才替换；保存失败不破坏旧配置。
                if (File.Exists(path)) File.Replace(temporary, path, null);
                else File.Move(temporary, path);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        public static SimulatorConfiguration Load(string path)
        {
            if (new FileInfo(path).Length > 33554432) throw new ArgumentException("配置文件超过 32 MB。");
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = 33554432 };
            SimulatorConfiguration result = serializer.Deserialize<SimulatorConfiguration>(File.ReadAllText(path, Encoding.UTF8));
            if (result == null) throw new ArgumentException("配置文件为空。");
            result.Validate();
            return result;
        }
    }
    public static class ValueParser
    {
        public static bool TryParse(string text, bool signed, out ushort value)
        {
            value = 0;
            if (String.IsNullOrWhiteSpace(text)) return false;
            text = text.Trim();
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                return ushort.TryParse(text.Substring(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out value);
            if (!signed) return ushort.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value);
            short number;
            if (!short.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out number)) return false;
            value = unchecked((ushort)number);
            return true;
        }
    }
}
