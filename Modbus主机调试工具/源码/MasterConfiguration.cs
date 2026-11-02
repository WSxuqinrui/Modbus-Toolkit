using System;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace ModbusMaster
{
    /// <summary>主机持久配置；Display中的原始数值只是快照，加载不会建立连接或开始轮询。</summary>
    public sealed class MasterConfiguration
    {
        private const int MaximumBytes = 32 * 1024 * 1024;
        public int Version { get; set; }
        public MasterConnectionOptions Connection { get; set; }
        public SimulatorConfiguration Display { get; set; }
        public int PollIntervalMs { get; set; }

        public MasterConfiguration()
        {
            Version = 1;
            Connection = new MasterConnectionOptions();
            Display = SimulatorConfiguration.Capture(new RegisterBank());
            PollIntervalMs = 500;
        }

        public void Validate()
        {
            if (Version != 1) throw new ArgumentException("不支持的主机配置版本。");
            if (Connection == null || Display == null) throw new ArgumentException("配置缺少通信参数或显示数据区。");
            if (PollIntervalMs < 50 || PollIntervalMs > 60000) throw new ArgumentException("轮询周期必须在50～60000毫秒之间。");
            Connection.Validate();
            // 复用四区地址、原始值、解析规则、名称单位和卡片顺序的完整校验。
            Display.Validate();
        }

        public void Save(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("配置路径不能为空。", "path");
            Validate();
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = MaximumBytes };
            byte[] payload = new UTF8Encoding(false, true).GetBytes(serializer.Serialize(this));
            if (payload.Length > MaximumBytes) throw new ArgumentException("配置文件超过32 MB。");
            string destination = Path.GetFullPath(path);
            string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (FileStream stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { stream.Write(payload, 0, payload.Length); stream.Flush(true); }
                // 临时文件与目标同目录，完整写入后原子替换，任何验证/写盘失败均保留旧文件。
                if (File.Exists(destination)) File.Replace(temporary, destination, null);
                else File.Move(temporary, destination);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        public static MasterConfiguration Load(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) throw new ArgumentException("配置路径不能为空。", "path");
            byte[] payload;
            using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (stream.Length > MaximumBytes) throw new ArgumentException("配置文件超过32 MB。");
                payload = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < payload.Length)
                {
                    int read = stream.Read(payload, offset, payload.Length - offset);
                    if (read == 0) throw new EndOfStreamException("配置文件未完整读取。");
                    offset += read;
                }
            }
            string json = new UTF8Encoding(false, true).GetString(payload);
            // 兼容其他文本编辑器保存的UTF-8 BOM。
            if (json.Length > 0 && json[0] == '\uFEFF') json = json.Substring(1);
            JavaScriptSerializer serializer = new JavaScriptSerializer { MaxJsonLength = MaximumBytes };
            MasterConfiguration result = serializer.Deserialize<MasterConfiguration>(json);
            if (result == null) throw new ArgumentException("配置文件为空。");
            result.Validate();
            return result;
        }
    }
}
