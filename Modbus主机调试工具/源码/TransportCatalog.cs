using System;
namespace ModbusMaster
{
    // UI顺序与持久化编号分离，重新排列下拉框不会改变旧配置文件的实际通信协议。
    public static class TransportCatalog
    {
        private static readonly int[] ids = { 1, 3, 2, 0, 4, 5, 6 };
        public static readonly string[] Names = { "串口 · RTU", "串口 · ASCII", "串口 · MBAP 扩展", "网口 · TCP/IP", "网口 · UDP/IP", "网口 · RTU over TCP", "网口 · RTU over UDP" };
        public static int IdAt(int index) { if (index < 0 || index >= ids.Length) throw new ArgumentOutOfRangeException("index"); return ids[index]; }
        public static int IndexOf(int id) { int index = Array.IndexOf(ids, id); if (index < 0) throw new ArgumentOutOfRangeException("id"); return index; }
    }
}
