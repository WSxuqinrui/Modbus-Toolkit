using System;
using System.Collections.Generic;

namespace ModbusMaster
{
    public sealed class AddressRule
    {
        public int Address { get; set; }
        public ParseRule Rule { get; set; }
    }
    /// <summary>独立于数值解析的卡片名称和显示单位；显式空单位覆盖解析规则单位。</summary>
    public sealed class AddressLabel
    {
        public int Address { get; set; }
        public string Name { get; set; }
        public string Unit { get; set; }
        public AddressLabel() { Name = ""; Unit = ""; }
        public void Validate()
        {
            if (Address < 0 || Address >= RegisterBank.Capacity || Name == null || Name.Length > 64 || Unit == null || Unit.Length > 32)
                throw new ArgumentException("卡片标签地址无效，名称最多64个字符，单位最多32个字符，且不可为null。");
        }
    }
    // 解析规则只影响工程量显示/编辑，不改变原始 Modbus 地址、功能码或线上的字节顺序。
    public sealed class AreaPresentation
    {
        public ParseRule DefaultRule = new ParseRule();
        public readonly SortedDictionary<int, ParseRule> Rules = new SortedDictionary<int, ParseRule>();
        public readonly SortedDictionary<int, AddressLabel> Labels = new SortedDictionary<int, AddressLabel>();
        public ParseRule GetRule(int address) { ParseRule rule; return Rules.TryGetValue(address, out rule) ? rule : DefaultRule; }
        public string GetName(int address) { AddressLabel label; return Labels.TryGetValue(address, out label) ? label.Name : ""; }
        public string GetUnit(int address) { AddressLabel label; return Labels.TryGetValue(address, out label) ? label.Unit : GetRule(address).Unit; }
        public void SetLabel(int address, string name, string unit)
        {
            AddressLabel label = new AddressLabel { Address = address, Name = name, Unit = unit };
            label.Validate(); Labels[address] = label;
        }
        public AddressLabel[] ExportLabels()
        {
            List<AddressLabel> result = new List<AddressLabel>();
            foreach (KeyValuePair<int, AddressLabel> item in Labels)
                result.Add(new AddressLabel { Address = item.Key, Name = item.Value.Name, Unit = item.Value.Unit });
            return result.ToArray();
        }
        public static ParseRule Copy(ParseRule rule)
        {
            return new ParseRule { DataType = rule.DataType, ByteOrder = rule.ByteOrder, Operation = rule.Operation,
                Operand = rule.Operand, Offset = rule.Offset, Decimals = rule.Decimals, Unit = rule.Unit };
        }
        public AddressRule[] Export()
        {
            List<AddressRule> result = new List<AddressRule>();
            foreach (KeyValuePair<int, ParseRule> item in Rules) result.Add(new AddressRule { Address = item.Key, Rule = Copy(item.Value) });
            return result.ToArray();
        }
        public void Import(AreaConfiguration config)
        {
            if (config == null) throw new ArgumentNullException("config");
            DefaultRule = Copy(config.DefaultRule ?? new ParseRule()); Rules.Clear(); Labels.Clear();
            if (config.ItemRules != null) foreach (AddressRule item in config.ItemRules) Rules.Add(item.Address, Copy(item.Rule));
            if (config.ItemLabels != null) foreach (AddressLabel label in config.ItemLabels)
            {
                if (label == null) throw new ArgumentException("标签不可为null。", "config");
                if (Labels.ContainsKey(label.Address)) throw new ArgumentException("标签地址重复。", "config");
                SetLabel(label.Address, label.Name, label.Unit);
            }
        }
        public void Trim(int start, int count)
        {
            List<int> removed = new List<int>(); foreach (int address in Rules.Keys) if (address < start || address >= start + count) removed.Add(address);
            foreach (int address in removed) Rules.Remove(address);
            removed.Clear(); foreach (int address in Labels.Keys) if (address < start || address >= start + count) removed.Add(address);
            foreach (int address in removed) Labels.Remove(address);
        }
        public static double Read(RegisterBank bank, int area, int address, ParseRule rule)
        { return ValueCodec.Decode(bank.ReadWords(area, address, ValueCodec.WordCount(rule)), rule); }
        public static void Write(RegisterBank bank, int area, int address, double displayValue, ParseRule rule)
        { bank.WriteWords(area, address, ValueCodec.Encode(displayValue, rule)); }
    }
}
