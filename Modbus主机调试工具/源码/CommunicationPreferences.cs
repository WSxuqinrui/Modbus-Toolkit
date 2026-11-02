using System;

namespace ModbusMaster
{
    /// <summary>只保存通信编辑值，不包含连接/监听/轮询状态，也不提交寄存器草稿。</summary>
    public sealed class CommunicationPreferences
    {
        public int Mode, Port = 502, Unit = 1, Baud = 9600, DataBits = 8;
        public string Ip, Com = "", Parity = "None", StopBits = "1";
        public int TimeoutMs = 1500, PollIntervalMs = 500;
        public void Normalize()
        {
            if (Mode < 0 || Mode > 6) Mode = 0;
            if (Port < 1 || Port > 65535) Port = 502; if (Unit < 1 || Unit > 247) Unit = 1;
            if (Array.IndexOf(new int[] { 1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200 }, Baud) < 0) Baud = 9600;
            if (Parity != "None" && Parity != "Even" && Parity != "Odd") Parity = "None";
            if (StopBits != "1" && StopBits != "2") StopBits = "1";
            if (DataBits != 7 && DataBits != 8 || Mode != 3) DataBits = 8;
            if (Com == null || Com.Length > 128) Com = ""; if (Ip != null && Ip.Length > 256) Ip = null;
            if (TimeoutMs < 100 || TimeoutMs > 60000) TimeoutMs = 1500;
            if (PollIntervalMs < 50 || PollIntervalMs > 60000) PollIntervalMs = 500;
        }
    }
    public sealed partial class MainForm
    {
        private CommunicationPreferences CaptureCommunicationPreferences()
        {
            string portName = com.Text;
            try { if (!String.IsNullOrWhiteSpace(portName)) portName = SerialDevices.RawPort(portName); }
            catch (ArgumentException) { } // 未完成的串口输入也保留，手动连接时仍会校验。
            int baudValue, bitsValue;
            if (!Int32.TryParse(baud.Text, out baudValue)) baudValue = 9600;
            if (!Int32.TryParse(dataBits.Text, out bitsValue)) bitsValue = 8;
            CommunicationPreferences result = new CommunicationPreferences {
                Mode = TransportMode, Ip = ip.Text, Port = (int)port.Value, Unit = (int)unit.Value, Com = portName,
                Baud = baudValue, Parity = parity.Text, StopBits = stopBits.Text, DataBits = bitsValue
            };
            result.TimeoutMs = (int)timeoutMs.Value; result.PollIntervalMs = (int)pollInterval.Value;
            result.Normalize(); return result;
        }
        private void RestoreCommunicationPreferences(CommunicationPreferences saved)
        {
            if (saved == null) return;
            saved.Normalize();
            // 模式事件会刷新串口列表并强制非ASCII为8位，先切模式再设最终编辑值。
            SelectTransport(saved.Mode); if (saved.Ip != null) ip.Text = saved.Ip;
            port.Value = saved.Port; unit.Value = saved.Unit; SelectPort(saved.Com);
            baud.SelectedItem = saved.Baud.ToString(); parity.SelectedItem = saved.Parity; stopBits.SelectedItem = saved.StopBits;
            dataBits.SelectedItem = saved.DataBits.ToString(); UpdateMode();
            timeoutMs.Value = saved.TimeoutMs; pollInterval.Value = saved.PollIntervalMs;
        }
    }
}
