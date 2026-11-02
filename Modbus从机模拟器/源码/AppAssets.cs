using System;
using System.Drawing;
using System.IO;

namespace ModbusSimulator
{
    /// <summary>程序的内嵌图形资源，调用方负责释放返回的独立图标实例。</summary>
    public static class AppAssets
    {
        /// <summary>
        /// 加载编译时以 app.ico 为名称嵌入的图标。
        /// 克隆图标后再关闭资源流，保证窗体后续绘制不依赖已经释放的流。
        /// </summary>
        public static Icon LoadIcon()
        {
            using (Stream resource = typeof(AppAssets).Assembly.GetManifestResourceStream("app.ico"))
            {
                if (resource == null)
                    throw new InvalidOperationException("缺少内嵌图标资源 app.ico，请使用 /resource:assets/app.ico,app.ico 编译。");
                using (Icon icon = new Icon(resource))
                    return (Icon)icon.Clone();
            }
        }
    }
}
