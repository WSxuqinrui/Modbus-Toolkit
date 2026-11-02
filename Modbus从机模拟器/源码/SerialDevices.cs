using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ModbusSimulator
{
    /// <summary>串口原始名称与驱动报告的描述；显示文字不会丢失 COM 端口号。</summary>
    public sealed class SerialDevice
    {
        public string PortName { get; set; }
        public string Description { get; set; }

        public string DisplayName
        {
            get
            {
                string port = SerialDevices.RawPort(PortName);
                string description = SerialDevices.CleanDescription(port, Description);
                return description.Length == 0 ? port : port + ": " + description;
            }
        }

        public override string ToString() { return DisplayName; }
    }

    /// <summary>
    /// 只读枚举：读取系统串口清单、SetupAPI 设备属性及设备注册表。
    /// 不构造或打开 SerialPort，不发送串口数据，不改变任何系统设置。
    /// </summary>
    public static class SerialDevices
    {
        private static readonly Regex PortPattern = new Regex(@"\ACOM([1-9][0-9]*)(?:\s*[:\u00b7]\s*\S[^\r\n]*)?\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        private static readonly IntPtr InvalidHandle = new IntPtr(-1);

        /// <summary>接受 COM3、COM3: 描述、COM3 · 描述；非法输入明确抛出 ArgumentException。</summary>
        public static string RawPort(string displayedOrRaw)
        {
            string value = (displayedOrRaw ?? String.Empty).Trim();
            Match match = PortPattern.Match(value);
            int number;
            if (!match.Success || !Int32.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out number))
                throw new ArgumentException("串口名称无效，应为 COM3 或 COM3: 设备说明。", "displayedOrRaw");
            return "COM" + number.ToString(CultureInfo.InvariantCulture);
        }

        internal static string CleanDescription(string port, string description)
        {
            string value = (description ?? String.Empty).Trim();
            string suffix = "(" + port + ")";
            if (value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                value = value.Substring(0, value.Length - suffix.Length).Trim();
            if (String.Equals(value, port, StringComparison.OrdinalIgnoreCase)) return String.Empty;
            return value;
        }

        /// <summary>纯函数：复制输入，清除无效项、合并重复端口并按端口数字升序排列。</summary>
        public static SerialDevice[] NormalizeDescriptors(IEnumerable<SerialDevice> descriptors)
        {
            Dictionary<string, SerialDevice> unique = new Dictionary<string, SerialDevice>(StringComparer.OrdinalIgnoreCase);
            if (descriptors != null)
            {
                foreach (SerialDevice descriptor in descriptors)
                {
                    if (descriptor == null) continue;
                    string port;
                    try { port = RawPort(descriptor.PortName); }
                    catch (ArgumentException) { continue; }
                    string description = CleanDescription(port, descriptor.Description);
                    SerialDevice previous;
                    if (!unique.TryGetValue(port, out previous))
                        unique.Add(port, new SerialDevice { PortName = port, Description = description });
                    else if (String.IsNullOrEmpty(previous.Description) && description.Length != 0)
                        previous.Description = description;
                }
            }
            List<SerialDevice> sorted = new List<SerialDevice>(unique.Values);
            sorted.Sort(delegate(SerialDevice left, SerialDevice right)
            {
                int a = Int32.Parse(left.PortName.Substring(3), CultureInfo.InvariantCulture);
                int b = Int32.Parse(right.PortName.Substring(3), CultureInfo.InvariantCulture);
                return a.CompareTo(b);
            });
            return sorted.ToArray();
        }

        /// <summary>优先保留驱动友好名，信息无权限或不可用时退回实际 COM 名。</summary>
        public static SerialDevice[] Enumerate()
        {
            List<SerialDevice> found = new List<SerialDevice>();
            // SetupAPI 只返回当前存在的设备；先加入描述，后合并系统端口清单。
            if (Environment.OSVersion.Platform == PlatformID.Win32NT)
            {
                try { AddSetupDevices(found); }
                catch (SecurityException) { }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                try
                {
                    using (RegistryKey key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM", false))
                    {
                        if (key != null)
                            foreach (string name in key.GetValueNames())
                                found.Add(new SerialDevice { PortName = key.GetValue(name) as string });
                    }
                }
                catch (SecurityException) { }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            try
            {
                foreach (string port in SerialPort.GetPortNames())
                    found.Add(new SerialDevice { PortName = port });
            }
            catch (SecurityException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
            return NormalizeDescriptors(found);
        }

        private static void AddSetupDevices(List<SerialDevice> found)
        {
            const uint presentAllClasses = 0x02 | 0x04;
            IntPtr set = SetupDiGetClassDevs(IntPtr.Zero, null, IntPtr.Zero, presentAllClasses);
            if (set == InvalidHandle) return;
            try
            {
                for (uint index = 0; ; index++)
                {
                    DeviceInfo info = new DeviceInfo();
                    info.Size = (uint)Marshal.SizeOf(typeof(DeviceInfo));
                    if (!SetupDiEnumDeviceInfo(set, index, ref info)) break;
                    // KEY_READ=0x20019；不请求写权限，也不创建注册表项。
                    IntPtr handle = SetupDiOpenDevRegKey(set, ref info, 1, 0, 1, 0x20019);
                    if (handle == InvalidHandle || handle == IntPtr.Zero) continue;
                    try
                    {
                        using (SafeRegistryHandle safe = new SafeRegistryHandle(handle, true))
                        using (RegistryKey key = RegistryKey.FromHandle(safe))
                        {
                            string port = key.GetValue("PortName") as string;
                            if (String.IsNullOrEmpty(port)) continue;
                            // SPDRP_FRIENDLYNAME=12；没有友好名时才使用真实设备描述。
                            string description = ReadProperty(set, ref info, 12);
                            if (String.IsNullOrEmpty(description)) description = ReadProperty(set, ref info, 0);
                            found.Add(new SerialDevice { PortName = port, Description = description });
                        }
                    }
                    catch (SecurityException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
        }

        private static string ReadProperty(IntPtr set, ref DeviceInfo info, uint property)
        {
            uint type, required;
            // Windows 设备属性有长度上限；先查询大小，避免截断或固定缓冲区溢出。
            SetupDiGetDeviceRegistryProperty(set, ref info, property, out type, null, 0, out required);
            if (required == 0 || required > 65536) return null;
            byte[] buffer = new byte[required];
            if (!SetupDiGetDeviceRegistryProperty(set, ref info, property, out type, buffer, (uint)buffer.Length, out required)) return null;
            return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DeviceInfo
        {
            public uint Size;
            public Guid ClassGuid;
            public uint DevInst;
            public UIntPtr Reserved;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetClassDevsW")]
        private static extern IntPtr SetupDiGetClassDevs(IntPtr classGuid, string enumerator, IntPtr parent, uint flags);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref DeviceInfo info);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern IntPtr SetupDiOpenDevRegKey(IntPtr set, ref DeviceInfo info, uint scope, uint profile, uint keyType, uint access);
        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiGetDeviceRegistryProperty(IntPtr set, ref DeviceInfo info, uint property, out uint type, byte[] buffer, uint bufferSize, out uint requiredSize);
        [DllImport("setupapi.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    }
}
