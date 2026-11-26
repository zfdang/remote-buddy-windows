# 同类 Windows 蓝牙遥控器方案技术实现对比

本文对目前同类思路的三个 Windows 实现在核心链路上的异同进行系统梳理与对照，作为后续架构演进、兼容性排障与设计取舍参考：

1. **Remote Buddy (`voice-input-buddy-windows`)**：C# / .NET 10 LTS + WPF（本项目）
2. **`ZSTDJan/windows-remote-mic-app` (WRM)**：Python + Qt/PySide6（偏重深度适配豆包 / 微信输入法的分支）
3. **`HZDavy/MoteDeck`**：Python + Qt/PySide6（偏向通用按键宏与可配置快捷键的同源分支）

---

## 一、整体技术栈与设计哲学差异

| 维度 | **Remote Buddy（本项目）** | **`ZSTDJan/windows-remote-mic-app`** | **`HZDavy/MoteDeck`** |
| :--- | :--- | :--- | :--- |
| **开发语言与运行形态** | C# / .NET 10 LTS，原生 WPF，单文件自包含发布 | Python + Qt/PySide6 + 多子进程（BLE 扫描/诊断/提权采集/Frida 宿主） | Python + Qt/PySide6 + 多子进程 |
| **核心工程取向** | **纯公开系统接口 + 轻量微软签名驱动**：不注入第三方应用和 Windows 驱动进程，维护面小、抗更新性强 | **极致突破系统与输入法限制（高侵入）**：为了无视 Windows 吞键、ETW 裁剪和豆包过滤标记，广泛使用 Frida / Gadget Hook 驱动与输入法 | **去耦合输入法私有协议 + 通用硬件化注入**：剥离了对豆包/微信输入法的私有 Hook，保留 `WUDFHost` Frida 采集与多注入后端 |
| **核心取舍逻辑** | 优先兼容主流普通用户环境、避免杀软/证书与系统更新脆弱性 | 只要能通过 SHA-256 与机器码门控校验就敢 Hook，功能耦合最深 | 把“虚拟键盘”与“通用快捷键录制”抽象出来，减少输入法升级维护负担 |

---

## 二、核心环节横向对照总表

| 核心处理环节 | **Remote Buddy（本项目）** | **`ZSTDJan/windows-remote-mic-app` (WRM)** | **`HZDavy/MoteDeck`** |
| :--- | :--- | :--- | :--- |
| **1. 小米 `RC003` 普通键与绑定** | **Raw Input（`VID_2717` / `PID_32B8`）+ BthPort ETW 仅补 `0x0064` 缺失键**（返回 `0xF1`、音量加减 `0x80`/`0x81`）；连接归属走 ETW GATT UUID 探针 | **Raw Input（`ContainerID` 过滤）+ Frida Gadget Hook `WUDFHost.exe`** 拦截 `UMDF_COPY_IOCTL` (`0x80018483`) 读取原始 9 字节报告并按需清零；通过调用栈 `NtQueryKey` / `ContainerID` 或独占进程确认实体 | 与 WRM 同源：**Raw Input + Frida 注入 `WUDFHost.exe`** 截取与抑制底层 HID 报告 |
| **2. Chromecast `G9N9N` 普通键与绑定** | **BthPort ETW Event 402 为主，直接 HID / Raw Input 为辅**。正常流程通过带 UUID 标记的出站查询关联设备和连接句柄。看不到出站标记时，根据探测窗口内的入站应答和有效按键通知推断归属。没有应答时，可根据唯一候选设备的完整按下、松开通知尝试绑定。 | 主路径 **完全放弃 BthPort ETW**，改由 **Frida Attach / Gadget 注入 `WUDFHost.exe`** Hook `HogpGattAdapter::OnReportValueChanged`；<br>从 `BTHLEDevice` 注册表取 `HostPid` + MAC，在回调入口直接比对 `this + address_offset` 的 6 字节 MAC（内置 Win10/Win11 数十个 DLL SHA-256 偏移表 + 在线下载微软 PDB 兜底）；<br>旧版 `ButtonReceiver`（纯 ETW）要求双向探针严格匹配且无兜底，15 秒直接超时报 `source_timeout` | 同样切换至 **Frida Attach `WUDFHost.exe` Hook `OnReportValueChanged`** + MAC 归属（内置单个已知 SHA-256，其余依赖联网下载微软 PDB 解析符号）；<br>旧版 ETW 代码同样无出站丢失兜底 |
| **3. 语音快捷键过 `LLKHF_INJECTED`（含豆包）** | **微软签名 `usbip-win2` (0.9.8.1) + `HIDMaestro` 公开 USB/IP 接口** 创建真实虚拟 USB 键盘；仅用于豆包等语音工具快捷键，保留左右修饰键，无 `LLKHF_INJECTED`；不修改受信任根证书 | **不建虚拟键盘**：继续 `keybd_event` 并附加 64 位秘密票据（`dwExtraInfo = 0x524D4943...`），由 **`DoubaoPhysicalizer` 用 Frida 注入豆包 `ImeService.exe`** 在回调内将 `flags & ~0x12` 与 `dwExtraInfo` 清零（支持 `0.9.0.0`–`1.0.0.23` 多版本 RVA/签名表） | **按按键可选三种注入后端**：默认 `SendInput`、**`WinUHid`**（基于 MIT 开源 `WinUHidDriver` / Microsoft VHF，需导自签名证书并建 `Root\WinUHid` 设备）、**`Interception`**（内核过滤驱动，需安装并重启） |
| **4. 输入法控制策略** | 纯靠豆包公开的「全局唤起语音（持续模式/长按模式）」快捷键，**不 Hook 输入法、不调用私有 COM / 管道**，对输入法版本升级不敏感 | 深度绑定：<br>1. 调未公开 TSF COM 接口 `IAICProxy` 切换前台输入法到豆包 / 微信输入法；<br>2. 连豆包私有命名管道 `\\.\pipe\ObricIme\oime-server`（`rpc.dll`）发 `0x3E9` 停止语音；<br>3. 可在豆包 `CallNextHookEx` 处吞掉末尾快捷键防止漏字；另有 `WeTypeVoiceControl` | **彻底剥离输入法私有耦合**：删除豆包 RPC 与微信输入法专控，只重放用户录制的通用快捷键（使用 `WinUHid` 时自动禁用长按重复，避免状态报告反复触发误判松开） |
| **5. 语音音频采集与传送至输入法** | WinRT GATT（ATVV）→ 纯 C# IMA ADPCM 解码为 16 kHz / 16-bit / Mono PCM → **`NAudio` 原生 WASAPI Render** → **`VB-CABLE` (`CABLE Input` → `CABLE Output`)** | WinRT GATT（ATVV）→ IMA ADPCM 解码 → **Python `sounddevice` (PortAudio)**（排除 WDM-KS，按 WASAPI > DirectSound > MME 选择）→ **`VB-CABLE` (`CABLE Input` → `CABLE Output`)** | 同 WRM（Python `sounddevice`/PortAudio → `VB-CABLE`；额外兼容 `CABLE In 16ch` 端点名，并支持直接呈现 `DJI Mic 2` 等系统真实录音输入状态） |
| **6. VB-CABLE 安装与就绪校验** | 应用内双源校验下载（优先固定版本 Pack 46 镜像，回退官网），调官方安装器交互授权；通过 WASAPI 真实打开 16kHz 流检测是就绪、未装还是待重启 | 构建期随包固化 `VBCABLE_Driver_Pack45.zip`（SHA-256 校验），点击按钮以 `runas` 拉起 `VBCABLE_Setup_x64.exe`；用 PortAudio + 独立子进程做探测与环回检查 | 同 WRM（打包固定 `Pack45` ZIP + `runas` 启动官方 `x64` 安装程序 + PortAudio 端点枚举验证） |
| **7. 音频通话特殊保护机制** | 无锁 `CircularBuffer`、启动 Pre-roll 缓存、结束先发关麦再排空尾帧最后松键 | 1. **`LoopbackProbe`**：向 `CABLE Input` 播放测试信号并从 `CABLE Output` 录回做互相关（阈值 `0.60`）验证通道真实导通；<br>2. **`MuteGuard`**：按住语音期间每 `50ms` 轮询检查自身播放会话，防微信输入法把虚拟声卡静音 | 同样保留了 **`MuteGuard`（50ms 防静音轮询）** 与独立音频后端线程控制 |

---

## 三、关键技术差异的深层原因分析

### 1. 蓝牙普通键与设备归属：ETW 路线 vs 驱动 Hook 路线
- **ETW 路线（Remote Buddy 采用）** 无需向系统驱动进程注入代码，也无需维护系统私有 DLL 的内存偏移表。曾在一台 Windows 10 `10.0.19045` 电脑上观察到完整入站通知，却未匹配到出站探测。兜底在自动化测试中覆盖了有入站应答和仅有完整按下、松开通知的情形。实际效果取决于系统交付的报文及候选设备是否唯一，具体实机验证范围见 [Windows 遥控器输入实现](windows-remote-input.md#诊断和验证范围)。
- **Frida Hook `WUDFHost.exe`（WRM / MoteDeck 采用）** 彻底脱离了 BthPort ETW，直接从 UMDF HID 驱动入口按 MAC 或 `ContainerID` 认领报文，并能在驱动向上提交前清除按键字节以防止原生系统键透传；但代价是**强侵入系统进程、容易触发杀软拦截、依赖多版本 DLL 偏移表或联网拉取微软 PDB**（详情见 [Frida Gadget HID 采集方案调研](frida-gadget-hid-tap-design.md)）。

### 2. 豆包快捷键与 `LLKHF_INJECTED`：Hook 输入法 vs 虚拟键盘驱动
- **WRM** 为了零驱动安装，选择了 **“软件发键 + Frida 钩进豆包 `ImeService.exe` 洗白标记 + 私有管道/COM 控制”**。优点是不装虚拟键盘；代价是高度脆弱——豆包每次发版都可能更换 `ImeService.exe` 哈希和汇编结构（代码已不得不维护 5 个版本的签名表）。
- **MoteDeck** 转向通用化，直接把虚拟键盘后端做成了 **`WinUHid`（自签名 UMDF + VHF）与 `Interception`（需重启的内核过滤驱动）**。缺点是用户需要接受导入自签名证书或重启安装内核驱动。
- **Remote Buddy** 选取了中间最稳妥的点：借助**已自带微软签名的 `usbip-win2`（0.9.8.1）+ `HIDMaestro` USB/IP 接口**模拟真实 USB HID 键盘，既不向豆包进程打补丁，也不篡改系统根证书库或要求用户重启装过滤驱动，详见 [豆包语音快捷键与 USB/IP 虚拟键盘](doubao-voice-hotkeys.md)。

### 3. 音频到输入法链路：共同的终点（`VB-CABLE`），不同的护航细节
- 因为 Windows DSE 内核驱动签名限制，**三个项目最终全部依托 VB-Audio 官方签名的 `VB-CABLE` 建立用户态 `CABLE Input -> CABLE Output` 音频桥梁**，技术收敛度极高。
- WRM 和 MoteDeck 为规避 Python `sounddevice`/PortAudio 在部分机器上的句柄清理卡死、以及微信输入法自动静音其他播放会话的现象，衍生出了**独立环回检测子进程（`LoopbackProbe`）**和 **`50ms MuteGuard`**；而 Remote Buddy 直接以 C# 调用原生 `WASAPI` Render，音频栈更短更可控。

---

## 四、可借鉴的演进参考（按优先级）

1. **高性价比、可直接借鉴（无侵入风险）**：
   - **播放防静音守卫（MuteGuard 思路）**：若后续用户在使用特定输入法时出现“遥控器收到了语音但 VB-CABLE 没声音”，可在语音会话激活期间检查自身 `AudioSessionControl.SimpleAudioVolume.Mute`，被系统或输入法静音时自动恢复。
   - **声学环回自检（LoopbackProbe 思路）**：向导在已有「驱动就绪检测」基础上，如有需要可增加“播放测试提示音并从 `CABLE Output` 采样匹配相关性”的可选诊断，排查用户误选麦克风或声卡静音问题。
2. **仅作极端系统兼容储备（当前不引入）**：
   - 仅当未来某代 Windows 的 BthPort ETW 全面屏蔽普通按键载荷时，再评估是否以可选高级模式引入 `WUDFHost.exe` Frida 采集（详见 [Frida Gadget HID 采集方案调研](frida-gadget-hid-tap-design.md)）。

---

## 相关文档

- [Windows 蓝牙遥控器信号处理分层架构总结](windows-ble-input-layers.md)
- [Frida / Frida Gadget 驱动层 HID 采集方案调研（暂不采用）](frida-gadget-hid-tap-design.md)
- [Windows 遥控器输入实现](windows-remote-input.md)
- [豆包语音快捷键与 USB/IP 虚拟键盘](doubao-voice-hotkeys.md)
- [虚拟音频与驱动集成方案](virtual-audio-and-driver.md)
