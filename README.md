# Remote Buddy (Windows)

> 🍎 **macOS 原生版**：如果您在寻找 macOS 系统上的版本，请前往访问 **[remote-buddy (macOS 原生版)](https://github.com/zfdang/remote-buddy)** —— Remote Buddy 原创 macOS 项目，提供了完整的 ATVV 协议状态机、时序防吞字、虚拟声卡路由架构及优秀的产品设计。
>
> 本项目定位为 **remote-buddy 的同源 Windows 原生移植版本**。

**Remote Buddy（遥控搭子）Windows 版** 把蓝牙电视遥控器变成 Windows 电脑上的全局语音输入按钮。用遥控器语音键唤起微信输入法、豆包输入法或系统听写，在当前输入框里说话输入。

本项目基于 **原生 Windows 技术栈（C# / .NET 10 LTS）** 构建，坚持**纯软件方案**，直接利用电脑内置蓝牙连接遥控器，无需额外购买或插拔任何 USB 硬件。

---

## 🌟 核心特性

- **零额外硬件**：直接通过 Windows 自带低功耗蓝牙（BLE）连接小米遥控器、Google Chromecast 遥控器等设备；
- **原生极速流转**：基于完整 ATVV 协议（含 `AUDIO_SYNC` 状态同步与动态 `SessionID` 管理）实时接收语音，纯 C# 内存级解码 IMA ADPCM（16kHz 16-bit PCM），经由低延迟 WASAPI 写入虚拟麦克风；
- **时序安全防护**：具备 Command Pre-roll（首音预存）与 Drain（尾音排空）保护，彻底解决输入法“吞首字”与“剪尾句”问题；
- **智能快捷键注入**：支持“按住说话（Hold-to-Talk）”与“点按切换（Toggle）”，无缝对接微信输入法、豆包输入法以及 Windows 11 自带语音听写（`Win + H`）；
- **多遥控器同时使用**。已连接的遥控器各用各的普通键映射，语音由先取得占用权的一只使用。映射页面选择设备只影响编辑对象。
- **自定义普通键**。支持单击、长按和双击，可发送快捷键、媒体键或常用文本。
- **闭环式新手向导**：五步式向导，包含驱动环境检测、音量波形测试、目标输入法麦克风引导与**实际转写验收**；
- **严格隐私保护**：应用纯本地运行，不保存录音或转写历史，无远程遥测。

---

## 🏗️ 架构与工作流

```text
普通键 → Raw Input／直接 HID 或 BthPort ETW → 按物理设备路由 → 单击、长按、双击映射
语音键 → BLE GATT 的 ATVV 控制通知 → 取得语音占用 → 输入法快捷键
麦克风 → ATVV 音频通知 → ADPCM 解码 → WASAPI → VB-CABLE 虚拟麦克风 → 输入法
```

## 遥控器支持与 Windows 验证范围

| 型号 | 普通键在 Windows 上的采集方式 | 语音与实机状态 |
| --- | --- | --- |
| **小米蓝牙遥控器 2 Pro（RC003）** | 方向键、确认、主页等走 Raw Input 键盘事件。ETW 补取 Windows 未交付给 Raw Input 的返回、音量加和音量减 | ATVV。Windows 11 已验证语音和按键，包含上述三个补取键 |
| **Google Chromecast 遥控器（G9N9N）** | 实测主要依赖 BthPort ETW，从设备的 HID over GATT 通知解码普通键 | ATVV。Windows 11 已验证语音和多枚普通键，ETW 普通键覆盖取决于系统是否交付完整报告 |
| **小米蓝牙遥控器 2（RC001）** | 有小米设备处理路径，实机报告与覆盖范围待独立验证 | ATVV。Windows 真机验收待补 |
| **Google TV Streamer 遥控器（GY3LE）** | 代码包含型号和采集路径，实机报告与覆盖范围待独立验证 | ATVV 兼容。Windows 真机验收待补 |

Windows 11 已验证一只 RC003 与一只 G9N9N 共存，两只普通键各用各的映射，语音一次由一只占用。型号用于选择解码规则，运行状态和映射按实体隔离。自动化测试覆盖三个输入来源，其中两个型号相同。测试记录与排查顺序见 [Windows 遥控器输入实现](docs/windows-remote-input.md)。

## 文档

- [Windows 遥控器输入实现](docs/windows-remote-input.md)对照两种设备的普通键、语音信号、设备身份绑定和并发规则。
- [小米 RC003 按键与 Windows 报告](docs/xiaomi-rc003-buttons.md)列出 Raw Input 键码、返回和音量的 ETW 报告及默认映射。
- [G9N9N 按键与 Windows 报告](docs/g9n9n-buttons.md)列出 HID 索引、Consumer Usage、语音智能模式及默认映射。
- [软件架构](docs/architecture.md)、[ATVV 协议](docs/device-protocols.md)、[虚拟音频与驱动](docs/virtual-audio-and-driver.md)和[快捷键与输入限制](docs/input-and-hotkeys.md)说明共享实现。

---

## 🤝 参考项目与致谢 (Attribution & References)

本项目在 Windows 平台实现蓝牙电视遥控器低延迟语音输入与按键捕获的过程中，参考并汲取了以下开源项目的宝贵经验与设计灵感：

1. **[remote-buddy (macOS 原生版)](https://github.com/zfdang/remote-buddy)**：Remote Buddy 原创 macOS 项目，提供了完整的 ATVV 协议状态机、时序防吞字、虚拟声卡路由架构及优秀的产品设计；
2. **[MoteDeck](https://github.com/HZDavy/MoteDeck)**：启发了在 Windows 下对蓝牙遥控器系统消息交互与原生按键直通映射的探索；
3. **[windows-remote-mic-app](https://github.com/BenjmSek/windows-remote-mic-app)**：为 Windows 平台下 BthPort ETW Event 402 内核事件捕获、驱动脱敏机制及 HID GATT 协议分析提供了宝贵的数据支撑。

---

## 🛠️ 技术栈总览

| 模块 | 技术选型 |
| :--- | :--- |
| **运行时** | **.NET 10 LTS** (C# 13) |
| **UI 框架** | **WPF** (配合 [lepoco/wpfui](https://github.com/lepoco/wpfui) 现代化控件库)，采用自包含单文件发布 |
| **蓝牙通信** | Windows 10/11 原生 WinRT BLE API (`Windows.Devices.Bluetooth`) |
| **音频处理** | [NAudio](https://github.com/naudio/NAudio) (WASAPI Render 模式) + 纯 C# IMA ADPCM 解码器（含 `AUDIO_SYNC` 校准） |
| **虚拟麦克风** | [VB-CABLE](https://vb-audio.com/Cable/) (引导用户从官网独立安装，已获微软 WHQL 认证) |
| **输入注入** | Win32 API (`SendInput` 40字节完整结构体 + `RegisterRawInputDevices`) |
| **打包分发** | Inno Setup 轻量安装包 |

---

## 📄 开源许可

本项目遵循 [GPL-3.0-only](LICENSE) 协议发布。
