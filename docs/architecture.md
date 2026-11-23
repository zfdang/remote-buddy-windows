# Remote Buddy (Windows) - 软件架构设计

Remote Buddy for Windows 是面向 Windows 10/11 的纯软件版硬件语音桥接工具。其目标是将闲置的蓝牙电视遥控器（如小米蓝牙遥控器、Google Chromecast 遥控器等）直接通过电脑自带蓝牙连接，将其打造成系统全局的“按住说话”语音输入按键，并将遥控器麦克风拾取的声音低延迟送入微信输入法、豆包输入法、Windows 11 自带语音听写等目标工具中。

本项目坚持**纯软件方案**，无需任何额外 USB 开发板或硬件接收器。

---

## 一、系统工作流与运行拓扑

```text
普通键 → Raw Input／直接 HID 或 BthPort ETW
       → 匹配物理遥控器 → 该设备的手势与映射 → SendInput

语音键 → WinRT GATT 的 ATVV 控制通知
       → 共用语音占用仲裁 → 输入法快捷键（SendInput）

麦克风 → ATVV 音频通知 → IMA ADPCM 解码 → WASAPI
       → VB-CABLE 播放端（CABLE Input）
       → VB-CABLE 录音端（CABLE Output）→ 目标输入法
```

小米 RC003 的多数普通键走 Raw Input，返回和音量加减由 ETW 补取。G9N9N 实测主要依靠 ETW。连接和按键状态按物理设备隔离，多个设备共用一条语音输出。详细对照见 [Windows 遥控器输入实现](windows-remote-input.md)。

---

## 二、技术栈选型与决策调整

本项目选用 **原生 Windows 技术栈（C# / .NET 10 LTS）** 进行开发：

| 层次 / 模块 | 选型 | 选用理由与决策说明 |
| :--- | :--- | :--- |
| **运行时环境** | **.NET 10 LTS** | 考虑到 .NET 8 LTS 将于 2026 年 11 月 10 日结束官方支持（EOL），新项目直接选用 .NET 10 LTS 作为长期稳定基线。采用**自包含单文件发布（Self-Contained Single-File）**，不依赖目标机器预装运行时。 |
| **UI 框架** | **WPF (配合 WPF-UI)** | 经评估，**明确选择成熟稳定的 WPF** 并撤回通用的 Native AOT 承诺。因为 WPF 官方仍默认禁用程序集裁剪（Trimming），无法直接可靠编译为 Native AOT 二进制。采用常规自包含单文件发布，既能享受 Windows 11 Fluent Design（Mica 亚克力、深色模式），又规避了 AOT 反射异常风险。 |
| **蓝牙通信 (BLE)** | **WinRT API (`Windows.Devices.Bluetooth`)** | 微软官方公开第一方蓝牙栈，支持 GATT 服务发现、特征值写入与数据通知（Notification），与 Windows 蓝牙系统设置深度集成。 |
| **音频推流** | **NAudio (基于 WASAPI Render)** | 成熟的低延迟音频写入库，以共享模式（Shared Event-Driven）将解码后的 PCM 音频推入虚拟声卡端点。 |
| **音频解码** | **纯 C# 实现 IMA ADPCM 解码器** | 纯内存计算，结合 `Span<byte>` 与查表法，实现 `AUDIO_SYNC` 状态同步与动态会话恢复。 |
| **虚拟麦克风** | **VB-CABLE (用户独立安装)** | 规避 Windows 内核驱动签名限制（DSE）。初期版本严格遵守开源许可约束，**不随安装包私自重新打包分发闭源二进制**，向导中引导用户从官方网站独立下载安装。 |
| **按键监控与注入** | **Raw Input、直接 HID、BthPort ETW、`SendInput`** | 采集来源可确认的普通键，通过 `SendInput` 执行映射和输入法快捷键，受 UIPI 权限边界约束。 |
| **打包与安装** | **Inno Setup** | 制作独立的轻量安装包，负责快捷方式生成、开机启动项注册与卸载清理。 |

---

## 三、核心模块与分层设计

| 模块 | 职责 |
| --- | --- |
| [RemoteBuddy.Core](../src/RemoteBuddy.Core/) | ATVV 报文、IMA ADPCM 解码、按键手势、设备和映射模型 |
| [RemoteBuddy.Platform.Windows](../src/RemoteBuddy.Platform.Windows/) | WinRT BLE、按设备连接管理、语音占用、Raw Input／HID／ETW、WASAPI 和快捷键注入 |
| [RemoteBuddy.App](../src/RemoteBuddy.App/) | WPF 客户端、设备连接与向导、按地址分发按键和加载各设备映射、输入法联动 |
| [RemoteBuddy.Tests](../src/RemoteBuddy.Tests/) | 报文解析、设备隔离、语音会话及映射回归测试 |

### 1. Bluetooth & Protocol Runtime
* **职责**。按蓝牙地址维护独立 WinRT BLE、GATT 订阅与 ATVV 状态。扫描由连接管理器统一发起。
* **状态机**：`Disconnected` -> `Connecting` -> `ServicesDiscovered` -> `CapabilitiesConfirmed` -> `Ready`。
* **特性**：
  * 支持自动重连（遥控器休眠唤醒后自动恢复）；
  * 具备超时看门狗，防止蓝牙通道静默断开。

### 2. Audio Pipeline & Ring Buffer
* **职责**：接收遥控器上报的语音分包，解码为标准音频，送入输出缓冲区。
* **低延迟保证**：采用线程安全的无锁环形缓冲区（`CircularBuffer`），容量控制在 100~200ms。
* **格式规范**：统一解码为 `16,000 Hz, 16-bit Signed Integer, Mono (单声道)`。
* **状态同步**：响应 ATVV `0x0A (AUDIO_SYNC)` 控制包，动态校准 Predictor 与 StepIndex。

### 3. Voice Session Coordinator (会话编排器)
* **职责**：严格编排“按下说话”、“松开结束”的生命周期与端到端时序。
* **关键时序约束（防止首音截断与尾音剪切）**：
  * **启动时**：在快捷键发出、输入法麦克风尚未完全就绪前，在应用层暂存首批音频包（Command Pre-roll）；
  * **停止时**：监听到按键释放时，**先向遥控器发送关麦并接收有界尾帧**，排空音频管线推流，**最后才注入结束输入法快捷键**。

---

## 四、线程模型与并发隔离

Raw Input 由主窗口或当前向导窗口的 `WM_INPUT` 消息入口处理。ETW 和直接 HID 接收器在后台读取，采集到的按键事件交给 WPF 调度器执行按设备分发和手势识别。各设备的 WinRT GATT 回调分别处理 ATVV 控制与音频。共享语音仲裁在启动音频输出前取得占用，避免多个设备同时写入虚拟麦克风。

---

## 五、本地诊断日志体系 (Diagnostics & Troubleshooting)

为了便于排查遥控器连接、ATVV 协议协商、按键映射与驱动状态等问题，程序内置了线程安全的轻量级本地诊断日志（`AppLogger`）：

* **日志存储位置**：`%APPDATA%\RemoteBuddy\logs\app.log`
* **滚动与归档策略**：单文件上限 5MB，最多保留 3 个归档副本（`app.1.log` ~ `app.3.log`），占用磁盘空间严格受限（< 20MB）。
* **诊断记录范围**：
  * 进程生命周期与异常（未捕获异常、启动参数、平台信息）；
  * BLE 扫描发现、连接协商与 GATT 特征订阅状态；
  * ATVV 协议握手、会话状态流转（`Opening` -> `Streaming` -> `Draining` -> `Idle`）；
  * Raw Input 设备注册、Container ID 匹配、ETW 会话及设备绑定、遥控器按键与映射动作分发；
  * 虚拟声卡驱动审计（VB-CABLE Ready / NotInstalled / RebootRequired）。
* **严格隐私保护规范**：
  * **绝不**记录任何麦克风语音流、音频波形与原始音频文件；
  * **绝不**记录用户语音转写的文本内容或剪贴板隐私；
  * **绝不**收集用户统计数据或进行远程遥测上报，纯本地安全排查。
* **用户交互支持**：设置界面提供一键直达“打开日志文件夹”，方便用户快速提取日志文件进行问题反馈。
