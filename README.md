# Remote Buddy (Windows)

> 🍎 **macOS 原生版**：如果您在寻找 macOS 系统上的版本，请前往访问 **[remote-buddy (macOS 原生版)](https://github.com/zfdang/remote-buddy)** —— Remote Buddy 原创 macOS 项目，提供了完整的 ATVV 协议状态机、时序防吞字、虚拟声卡路由架构及优秀的产品设计。
>
> 本项目定位为 **remote-buddy 的同源 Windows 原生移植版本**。

**Remote Buddy（遥控搭子）Windows 版** 把蓝牙电视遥控器变成 Windows 电脑上的全局语音输入按钮。拿起遥控器，按住说话、松开结束，即可在当前任意输入框中唤起输入法（微信输入法、豆包输入法等）或系统听写工具进行流畅输入。

本项目基于 **原生 Windows 技术栈（C# / .NET 10 LTS）** 构建，坚持**纯软件方案**，直接利用电脑内置蓝牙连接遥控器，无需额外购买或插拔任何 USB 硬件。

---

## 🌟 核心特性

- **零额外硬件**：直接通过 Windows 自带低功耗蓝牙（BLE）连接小米遥控器、Google Chromecast 遥控器等设备；
- **原生极速流转**：基于完整 ATVV 协议（含 `AUDIO_SYNC` 状态同步与动态 `SessionID` 管理）实时接收语音，纯 C# 内存级解码 IMA ADPCM（16kHz 16-bit PCM），经由低延迟 WASAPI 写入虚拟麦克风；
- **时序安全防护**：具备 Command Pre-roll（首音预存）与 Drain（尾音排空）保护，彻底解决输入法“吞首字”与“剪尾句”问题；
- **智能快捷键注入**：支持“按住说话（Hold-to-Talk）”与“点按切换（Toggle）”，无缝对接微信输入法、豆包输入法以及 Windows 11 自带语音听写（`Win + H`）；
- **全按键映射探索**：支持 Raw Input 全局按键监听，探索按键附加映射与 Unicode 常用语快速上屏；
- **闭环式新手向导**：五步式向导，包含驱动环境检测、音量波形测试、目标输入法麦克风引导与**实际转写验收**；
- **严格隐私保护**：应用纯本地运行，不保存录音历史，不统计按键日志，无远程遥测。

---

## 🏗️ 架构与工作流

```text
遥控器麦克风 → BLE GATT 语音流 → Remote Buddy (C# 解码) → WASAPI → 虚拟麦克风 (VB-CABLE) → 输入法
遥控器语音键 → Raw Input / ATVV → Remote Buddy 状态机    → SendInput → 触发目标语音快捷键   → 当前输入框
```

---

## 📱 支持的遥控器范围

> 注：下表中的“已验证”均指已在 macOS 上完成验证；Windows 平台以第一阶段（Phase 1）真机验收为准。

| 遥控器型号 | 语音协议 | 普通按键支持 | 状态与验证边界 |
| :--- | :--- | :--- | :--- |
| **小米蓝牙遥控器 2** (RC001) | ATVV v1.0 (16kHz) | 支持（完整方向、确认、主页、返回、音量） | macOS 已验证；Windows 待真机验收 |
| **小米蓝牙遥控器 2 Pro** (RC003) | ATVV v1.0 (16kHz) | 支持 | macOS 已验证；Windows 待真机验收 |
| **Google Chromecast 遥控器** (G9N9N) | ATVV v1.0 (16kHz) | 仅部分支持（普通按键待 PoC 验证） | macOS 语音已验证；Windows 待真机验收 |
| **Google TV Streamer 遥控器** (GY3LE) | ATVV 兼容 | 计划支持 | 待后续硬件适配与真机验证 |

---

## 📚 设计与开发文档

Windows 真机已验证 G9N9N 常用普通键，以及 RC003 的返回和音量加减。设备报告、采集路径和验收范围见下方技术文档。

相关技术细节与规范已在 `docs/` 目录下完成详细归档：

- [G9N9N 按键与 Windows 报告](docs/g9n9n-buttons.md)
- [小米 RC003 按键与 Windows 报告](docs/xiaomi-rc003-buttons.md)
- [Windows 按键采集、切换与隔离](docs/windows-remote-input.md)
- 🕹️ [Google Chromecast (G9N9N) 按键处理方案技术规范](docs/chromecast-g9n9n-button-handling.md)：详细阐述 G9N9N/GY3LE 遥控器在 Windows 上的隔离痛点、多种技术路线对比（Frida 注入 vs ETW 实时流 vs 消息栈）以及 Remote Buddy 采用的原生零注入多轨协同架构；
- 🏛️ [软件架构设计 (Architecture)](docs/architecture.md)：技术栈选型（.NET 10 LTS + WPF 自包含发布）、分层设计与线程并发模型；
- 📡 [遥控器与 ATVV 协议规范 (Device Protocols)](docs/device-protocols.md)：支持设备列表、完整 ATVV 控制报文（`0x08`, `0x04`, `0x0A`, `0x0D` 等）、动态 `SessionID` 处理、`AUDIO_SYNC` 解码同步与 HID Usage 码表；
- 🎙️ [虚拟音频与驱动集成方案 (Virtual Audio & Driver)](docs/virtual-audio-and-driver.md)：Windows 驱动签名限制应对策略、VB-CABLE 独立安装合规决策、重启状态处理与 WASAPI 低延迟推流引擎；
- ⌨️ [按键监听与快捷键注入规范 (Input & Hotkeys)](docs/input-and-hotkeys.md)：Raw Input 监听限制（区分监听、附加映射与屏蔽）、`SendInput` 64 位结构对齐（40 字节）、UIPI 权限与首尾音时序防护；
- 🗺️ [开发路线图与里程碑 (Development Roadmap)](docs/development-roadmap.md)：设立严格的第一阶段真机验收门槛（Phase 1 Acceptance Gate），明确从 CLI 验证到实际转写成功的路线。

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
