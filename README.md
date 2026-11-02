# Modbus Toolkit

Windows Modbus 主机调试工具与从机模拟器，当前版本 **2.9**。

<img src="Modbus主机调试工具/assets/app.svg" width="64" alt="Master"> <img src="Modbus从机模拟器/assets/app.svg" width="64" alt="Slave">

- [最新发布与下载](https://github.com/WSxuqinrui/Modbus-Toolkit/releases/latest)
- [主机便携版](https://github.com/WSxuqinrui/Modbus-Toolkit/releases/download/v2.9/ModbusMaster-2.9.zip)
- [从机便携版](https://github.com/WSxuqinrui/Modbus-Toolkit/releases/download/v2.9/ModbusSlave-2.9.zip)

## 功能

- 串口RTU、串口ASCII、串口MBAP扩展、TCP/IP、UDP/IP、RTU over TCP、RTU over UDP。
- 线圈/离散输入/保持寄存器/输入寄存器四区，数量可为0；列表、平铺、页签、浮窗、连续卡片视图。
- UInt16/Int16/UInt32/Int32/Float32/Float64、字节顺序、四则公式；名称单位跨视图同步、卡片快速修改。
- 主机后台轮询，05/06/0F/10写入及确认后读回；输入对象只读。
- HEX/ASCII自定义发送、14个校验选项、换行和可设周期；目标失效停止、忙时跳过不堆积。
- 中英日韩俄语言，明亮/暗黑模式，软件内可打开本项目。
- 自动保存布局、语言主题、发送草稿/格式/周期、通信参数；重开保持断开、轮询及循环发送关闭。

## 运行

Windows 10/11，.NET Framework 4.8。下载EXE或解压便携包后运行，无第三方安装依赖。
两程序使用不同图标：M为主机、S为从机。首次默认四区各10项，站号1、端口502；串口默认9600/8N1。
按设备调整参数，再手动启动/连接。主机自定义原始发送进入独占原始模式并暂停常规轮询；断开重连恢复常规Modbus操作。
本机偏好位于 `%LOCALAPPDATA%/ModbusTools/master/layout.json` 和 `slave/layout.json`，两程序分别记忆；无需提交这些文件。

## 从源码构建

在Windows仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1 -BuildOnly
```

省略`-BuildOnly`执行完整软件回归。脚本使用系统.NET Framework4.8的csc.exe，自动嵌入软件图标，无NuGet依赖。
输出位于两项目根目录，测试结果位于各自`验证/`（Git忽略）。重编前关闭同版本的EXE。

目录：`Modbus主机调试工具/源码`、`Modbus从机模拟器/源码`、各自`assets`及当前使用说明/安全联调示例。

## 协议与测试范围

标准TCP/UDP使用MBAP+PDU，无RTU CRC；RTU扩展传输使用站号+PDU+CRC。串口MBAP是兼容扩展，需要对端支持。
地址从0开始，同一实例一个站号。原始发送面板只发送最终字节，不会根据连接模式再次包头或加CRC。
随附示例使用回环/本机参数，加载不会自动启动。先在软件间联调，再按实际设备参数使用。

已完成本机协议/配置/数值解析、TCP/UDP回环、写后立即读回、WinForms控件与布局、语言主题、状态恢复和图标检查。
物理COM/RS485及真实多显示器DPI仍需实际环境验收；程序化窗口/缩放检查不等同于现场设备验证。

CRC参数与固定向量参考：[CRC RevEng](https://reveng.sourceforge.io/crc-catalogue/)。
