# Frida / Frida Gadget 驱动层 HID 采集方案调研与备选设计（暂不采用）

> **状态**：潜在备选架构记录（**当前版本不采用**）。  
> 当前生产版本继续坚持**无进程注入**的公开系统接口路线（RawInput + Direct HID + BthPort ETW Event 402）。只有当未来 Windows 驱动/系统机制导致 ETW 彻底无法获取按键载荷时，再评估是否将本文所述方案作为可选高级采集后端引入。

---

## 1. 背景与参考来源

在调研 `HZDavy/MoteDeck` 与 `ZSTDJan/windows-remote-mic-app` 两个同源开源项目时，观察到它们在 Windows 端为了应对“Windows 吞掉部分 HID Usage（如返回键、部分功能键）”以及“BthPort ETW 脱敏抹空字节 / 丢失探测包”的问题，演化出了一套基于 **Frida / Frida Gadget 注入 Windows UMDF 驱动宿主进程 `WUDFHost.exe`** 的直接采集方案：

- 在 Chromecast（`G9N9N`）路径中，其注释明确说明已不再使用 BthPort ETW（*"The legacy BTHPORT trace is never a readiness gate or an input source here."*），转而在 `WUDFHost.exe` 内 Hook `microsoft.bluetooth.profiles.hidovergatt.dll` 的私有报告回调。
- 在小米（`RC003`）路径中，则配合 RawInput 使用 Frida Gadget Hook `DeviceIoControl` / `NtDeviceIoControlFile` 读取底层 UMDF HID 报告并按需屏蔽原生按键上抛。

---

## 2. 两条参考实现路径与设备绑定原理

### 2.1 Google Chromecast (`G9N9N`)：Hook `HogpGattAdapter::OnReportValueChanged` + PnP MAC 绑定

主要对应参考实现中的 `chromecast_hid_worker.py` 与 `chromecast_hid_tap_windows.py`：

1. **通过 Windows PnP 注册表确定宿主进程与蓝牙 MAC**
   - 枚举 `HKLM\SYSTEM\CurrentControlSet\Enum\BTHLEDevice` 下服务 GUID `{00001812-0000-1000-8000-00805f9b34fb}` 且包含目标硬件标识（如 `dev_vid&0118d1_pid&9450_rev&0110` / `rev&011b`）的节点。
   - 核对实例下的 `ContainerID` 与用户所选物理遥控器匹配，仅当唯一命中时读取：
     - `Device Parameters\WUDFDiagnosticInfo\HostPid`：托管该 HID over GATT 实例的 **`WUDFHost.exe` PID**；
     - 节点键名末尾的 12 位 hex：**遥控器蓝牙 MAC 地址**。
2. **解析私有驱动符号与对象布局**
   - 目标模块为 `%SystemRoot%\System32\drivers\umdf\microsoft.bluetooth.profiles.hidovergatt.dll`，目标符号为 `Microsoft::Bluetooth::Profiles::HidOverGatt::HogpGattAdapter::OnReportValueChanged`。
   - 先按精确文件 SHA-256 查内置映射表（记录不同 Windows Build 下该函数的 `RVA` 以及 `this` 指针中蓝牙地址的字段偏移，例如 `24` 或 `8` 字节）；
   - 未命中已知哈希时，从 PE Debug 目录读取 RSDS/PDB 签名，尝试从 `msdl.microsoft.com` 下载公开 PDB，通过 `dbghelp` 解析该回调 RVA，并将验证后的符号缓存至本地。
3. **注入 `WUDFHost.exe` 与运行时按 MAC 归属**
   - 优先通过 Frida Attach 目标 `HostPid`；若遇 `ProcessNotRespondingError`，`windows-remote-mic-app` 还实现了回退到注入 **Frida Gadget DLL** 并建立本地控制连接的方式。
   - Hook 脚本在 `OnReportValueChanged(this, ...)` 入口直接读取 `this + address_offset` 处的 6 字节蓝牙 MAC 地址（`addressMatches`），与目标遥控器 MAC 比对。
   - 验证通过后，通过 `args[2]`（WinRT `IBuffer` 接口）直接读出 8 字节索引报告（或 2 字节 Consumer Usage 并归一化），经消息通道发回主进程解码，**全程不依赖 BthPort ETW 与出站/入站 GATT 探针**。

---

### 2.2 小米 (`RC003`)：Frida Gadget Hook `DeviceIoControl` / `NtDeviceIoControlFile` + `ContainerID` 绑定

主要对应参考实现中的 `frida_hid_tap_runtime.py` 与 `raw_input_windows.py`：

1. **采集职责划分**
   - 标准键盘键（方向键、确认、主页等）直接走带设备路径过滤的 Win32 **Raw Input**。
   - Windows 键盘驱动吞掉的键（如返回键 Usage `0x00F1` 等），通过注入到 `WUDFHost.exe` 的 **Frida Gadget**（例如内嵌配置 + 脚本的 DLL，并通过本地回环端口与主进程通信）截获底层报告。
2. **拦截点与报文中转**
   - Hook `WUDFHost.exe` 对 `DeviceIoControl` 的调用以及底层 `ntdll!NtDeviceIoControlFile`，关注特定 UMDF HID 复制控制码 `UMDF_COPY_IOCTL = 0x80018483`（8 字节输入元数据 + 9 字节输出键盘报告）。
   - 截获原始 9 字节报告传回主进程后，脚本可就地将缓冲区中的 HID usage 清零，从而阻止 Windows 将同一份按键再翻译成多余的系统原生按键事件。
3. **基于 `ContainerID` 的严格设备绑定（多设备防串扰）**
   - 在 `DeviceIoControl` 入口处，利用 `RtlLookupFunctionEntry` 校验调用方返回地址落在 `WUDFHost.exe` 代码段内，并反汇编分析非易失寄存器加载模式以定位实时 UMDF 设备栈对象；
   - 通过验证虚表 `QueryInterface` 匹配特定接口 IID（`7a2dfa5b66f7d34a959ed8fe0f0e83e1`）获取其注册表句柄，再调用 `NtQueryKey` 与 `RegQueryValueExW("ContainerID")` 算得设备摘要，精确比对用户当前选中的遥控器（`selectedSourceKey`）来绑定 `handle`；
   - 若无法从调用栈解析实例句柄，则枚举 `HKLM\SYSTEM\CurrentControlSet\Enum` 检查该 `WUDFHost.exe` 进程是否**独占**只挂载了一只 RC003 设备；不满足唯一性时 fail-closed 拒绝绑定，避免误拦其他键盘设备。

---

## 3. 技术优势与工程代价对照

| 维度 | 当前路线（RawInput + Direct HID + BthPort ETW） | 备选路线（Frida / Frida Gadget Hook `WUDFHost.exe`） |
| :--- | :--- | :--- |
| **系统侵入性** | **零注入**，仅使用公开 Win32 / WinRT / ETW API | **高侵入**，向系统级 UMDF 驱动宿主进程注入原生代码与 Hook |
| **系统版本脆弱性** | 不依赖微软私有 DLL 的 RVA 或结构体内存偏移 | 强依赖未公开 C++ 类布局、偏移量与私有 IOCTL；Windows 更新后需更新哈希表或联网下载 PDB |
| **安全软件兼容性** | 仅提权 ETW 会话时需 UAC，很少触发杀软误报 | 注入系统进程 + 下载/加载 Gadget DLL 极易被 Defender、EDR 或第三方安全软件拦截 |
| **解决极端问题的能力** | 受限于 BthPort ETW（若系统裁剪载荷或不发特定包则无解） | 可绕开 BthPort ETW 脱敏与丢包限制，也能读取被 Windows 吞掉的 Usage 并抑制系统原生键值 |
| **生命周期与打包复杂度** | 仅随主程序附带轻量可信源码逻辑，停止即关闭 Trace | 需分发/管理原生 Frida Gadget 二进制与私有临时目录 ACL，需处理卸载超时、宿主崩溃与 x64 限制 |

---

## 4. 为什么现阶段暂不采用

1. **不符合当前轻量化与可维护性边界**：引入 Frida 运行时及多版本 `hidovergatt.dll` 偏移表/在线 PDB 解析会大幅增加打包体积、跨 Windows 小版本维护负担和排障复杂度。
2. **安全策略与杀软拦截风险高**：向 `WUDFHost.exe` 注入 DLL 属于高敏感行为，在很多普通用户主机与办公电脑上容易直接失败，整体可用率反而可能不如纯 ETW。
3. **当前场景已有轻量解法**：
   - 在已排查的 Windows 11 机器上，BthPort ETW Event 402 可稳定拿到完整字节；
   - 在 Windows 10 `10.0.19045` 日志场景中，Event 402 仍交付了完整的 8 字节 `attr=0x0029` 入站通知，只是缺少出站请求包，适合先通过 PR #47 的探测关联兜底逻辑在 ETW 层解决，避免引入注入依赖。

---

## 5. 后续何时重新评估启用该方案

仅当出现以下**现有公开路径无法在用户侧软件绕过的硬性阻塞**时，再把本文方案作为“受控、可选的高级驱动采集后端”重新提上日程：

1. Windows 主流更新后 BthPort ETW Event 402 普遍只吐包头或被系统裁剪脱敏，导致无法获得键码；
2. 实机验证确认目标 Windows 版本在 ETW 既不输出出站探测包、也不输出可关联的入站 ATT 应答包，使得无注入 ETW 彻底无法完成设备归属确认；
3. 需要针对 RC003 或新机型从驱动底层同时实现**“读取被吞键值 + 抑制系统原始快捷键透传”**且公开 RawInput / 键盘钩子无法满足要求。

---

## 相关文档

- [Windows 蓝牙遥控器信号处理分层架构总结](windows-ble-input-layers.md)
- [Windows 遥控器输入实现](windows-remote-input.md)
- [G9N9N 按键与 Windows 报告](g9n9n-buttons.md)
- [小米 RC003 按键说明](xiaomi-rc003-buttons.md)
