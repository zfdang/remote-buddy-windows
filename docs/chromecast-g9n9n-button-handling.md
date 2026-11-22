# Google Chromecast (G9N9N / GY3LE) 遥控器按键处理方案技术规范

本文档详细记录在 Windows 平台下针对 **Google Chromecast with Google TV 语音遥控器（G9N9N）** 及 **Google TV Streamer 遥控器（GY3LE）** 实体按键捕获的技术分析、不同方案优缺点对比，以及 Remote Buddy（Windows）采用的**多轨零注入融合架构**。

---

## 一、 背景与系统级痛点

Chromecast 遥控器（型号 G9N9N）是一款广泛使用的低功耗蓝牙（BLE）语音遥控器，但在 Windows 系统上，其底层协议呈现出“双重隔离”的特征：

```text
                  ┌────────────────────────────────────────────────────────┐
                  │          Google Chromecast 遥控器 (G9N9N)               │
                  └────────────────────────────────────────────────────────┘
                                     │
                 ┌───────────────────┴───────────────────┐
                 ▼                                       ▼
    【通道 A：ATVV GATT 语音服务】           【通道 B：HOGP 标准 HID 服务】
    UUID: AB5E0001-5A21-4F05-...            UUID: 0x1812 (Human Interface Device)
                 │                                       │
                 ▼                                       ▼
  Windows WinRT API 完全开放允许访问          Windows 内核与 hidovergatt.dll 独占
  • 语音流通知 (AudioStart / AudioData)     • 普通应用读取返回 AccessDenied (E_ACCESSDENIED)
  • 物理语音键触发 (StartSearch 0x08)       • CreateFile 打开 HID 设备句柄被拒绝
  • 麦克风保活与重开 (MicrophoneOpen)       • RawInput 无法直接枚举与接收未映射 Consumer 报文
                 │                                       │
                 ▼                                       ▼
     【语音键始终可原生稳定工作】                 【普通方向键/OK/返回/音量被系统拦截】
```

1. **语音通道畅通**：遥控器语音键及音频流运行在 Android TV Voice (ATVV GATT) 专属服务上，Windows WinRT 原生开放，因此语音按键与 16kHz ADPCM 音频流可在普通权限下原生工作；
2. **普通按键被系统拦截隔离**：方向键（上/下/左/右）、确认键（OK）、返回、主页、音量加减、静音、YouTube、Netflix 等按键均属于标准 HID-over-GATT (HOGP) 服务。Windows 内核驱动（`bthport.sys`）与 UMDF 蓝牙驱动（`microsoft.bluetooth.profiles.hidovergatt.dll`，运行于 `WUDFHost.exe`）对 0x1812 服务实施严格独占，禁止任何用户态应用程序直接访问或枚举其特征，导致常规的 WinRT 或 RawInput 监听完全失效。

---

## 二、 几种技术路线与方案对比

社区与业界针对该问题主要有以下几种技术路线：

| 方案维度 | 方案一：Frida / DLL 注入 WUDFHost | 方案二：BthPort ETW 实时内核跟踪 | 方案三：系统消息栈 (WM_APPCOMMAND) | 方案四：RemoteBuddy 多轨零注入融合方案 |
| :--- | :--- | :--- | :--- | :--- |
| **工作原理** | 向 `WUDFHost.exe` 注入代码，Hook `hidovergatt.dll` 的 `OnReportValueChanged` | 监听 Windows 蓝牙驱动 ETW 会话，直接在内核层捕获 Event 402 ACL 数据包 | 监听窗口焦点消息、`WM_APPCOMMAND` 与原生键盘事件 | **ETW 双引擎（TDH + UserData）+ 截断自愈 + 窗口多级协同** |
| **代码注入** | **是**（极高风险，需向驱动宿主注入） | **否**（纯净原生 Win32 API） | **否** | **否（纯净原生零注入）** |
| **杀毒软件兼容** | 极易报毒（Defender/火绒常拦截为木马注入） | 绿色安全，不触碰任何安全防御线 | 绿色安全 | **完全合规，绝不触碰杀软告警** |
| **按键覆盖范围** | 全部 15 个按键 | 全部 15 个按键（需配置生效） | 仅限部分已转换的系统键 | **全部 15 个按键精准区分** |
| **系统版本兼容** | 脆弱（Windows 补丁更新会改变函数 RVA） | 稳定（Win 10 19041 ~ Win 11 全系标准事件） | 稳定 | **全 Windows 10/11 版本跨平台自愈稳定** |
| **免重启友好度** | 免重启生效 | 需重启蓝牙适配器或电脑加载敏感日志 | 免重启 | **向导自愈兼容（未重启也能通过按键测试）** |

### 1. 方案一：Frida / DLL 注入 WUDFHost.exe
- **实现原理**：通过提权辅助进程寻找加载了 `microsoft.bluetooth.profiles.hidovergatt.dll` 的 `WUDFHost.exe` 驱动宿主进程，使用 Frida Gadget 或原生 CreateRemoteThread 注入 DLL，挂钩 `HogpGattAdapter::OnReportValueChanged`，从输入参数中读取 8 字节 HID 原始报表并通过 IPC 命名管道传回应用。
- **致命缺点**：
  - 属于典型的进程注入行为，几乎 100% 会被主流安全防护软件拦截报毒；
  - 依赖特定 Windows 构建版本的符号表偏移（RVA），微软推送常规系统累积更新即可造成目标进程崩溃，进而引起整机蓝牙设备集体掉线；
  - 违背 Remote Buddy 追求的绿色纯净、不损害系统稳定性的开源原则。

### 2. 方案二：BthPort ETW (Event Tracing for Windows)
- **实现原理**：微软 Windows 蓝牙核心驱动 `bthport.sys` 暴露了原生 ETW 提供者 `{8a1f9517-3a8c-4a9e-a018-4f17a200f277}`。开启实时跟踪会话（`EVENT_TRACE_REAL_TIME_MODE`）并订阅 `0x8000000000000000` 关键字后，驱动在收到每个蓝牙 ACL 数据包时均会分发 Event 402（`BthPort_Event_BipAclData`）。
- **技术要点与难点**：
  - **敏感数据脱敏机制**：默认状态下，Windows 出于隐私安全考虑，只记录前 7 字节报头，将 8 字节 HID 报表载荷作为敏感数据剔除；
  - **注册表配置与加载时机**：需在 `HKLM\SYSTEM\CurrentControlSet\Services\BthPort\Parameters` 中设置 `EtwLogSensitiveData=1`、`MaxEtwBytes=1024`、`EtwDropLargeEvents=0`。**但 `bthport.sys` 仅在驱动初始化时读取该参数**，初次配置后必须重启蓝牙适配器或操作系统；
  - **TDH 模式缺失**：部分 Windows 系统未在 WMI/TDH 架构中注册该驱动事件的字段元数据，仅调用 `TdhGetProperty` 会因找不到属性返回失败，必须具备直接解析 `EVENT_RECORD.UserData` 原始内存的能力。

### 3. 方案三：系统消息栈 (WM_APPCOMMAND / RawInput)
- **实现原理**：Windows 系统的输入栈在收到蓝牙 HID 报告后，会将部分标准按键转换为窗口消息（如将返回键转换为 `WM_APPCOMMAND` 的 `APPCOMMAND_BROWSER_BACK`，音量转换为 `APPCOMMAND_VOLUME_MUTE` / `VOLUME_UP`，方向键转换为 `VK_UP` 等）。
- **局限性**：无法识别遥控器特有的流媒体厂商按键（如 YouTube 0x0E、Netflix 0x0F）；且窗口失去焦点或焦点位于不可捕获控件上时容易漏键。

---

## 三、 Remote Buddy 采用的方案：多轨零注入融合体系

为了兼顾**绝对安全合规（零报毒、零注入）**、**全按键精准识别**与**新手向导流畅自愈**，Remote Buddy（Windows）设计并实现了多轨协同架构：

```text
 ┌────────────────────────────────────────────────────────────────────────┐
 │                      Remote Buddy 多轨协同输入架构                      │
 └────────────────────────────────────────────────────────────────────────┘
                                     │
      ┌──────────────────────────────┼──────────────────────────────┐
      ▼                              ▼                              ▼
【轨道 1：BthPort ETW 内核流】    【轨道 2：ATVV GATT 状态机】     【轨道 3：UI 消息协同栈】
  • StartTrace / OpenTrace       • WinRT 原生 BLE 监听           • OnPreviewKeyDown (WPF)
  • Event 402 双引擎解析:          • StartSearch (0x08) 触发       • WM_APPCOMMAND 消息截获
    - TDH BIP_Data 优先            • AudioStart / AudioStop 协同    • 焦点穿透与防吞键处理
    - UserData Raw 内存兜底        • 语音键 100% 毫秒级直通         • 满足向导验证即时放行
  • 截断报文自愈探测 (0x0029)
      │                              │                              │
      └──────────────────────────────┼──────────────────────────────┘
                                     ▼
                      ┌─────────────────────────────┐
                      │    按键事件统一分发引擎       │
                      │  (DispatchButtonEvent)      │
                      └─────────────────────────────┘
                                     │
                 ┌───────────────────┴───────────────────┐
                 ▼                                       ▼
       【向导模式 (Onboarding)】                  【正常运行模式 (Runtime)】
       • 验证按键响应状态                        • 实体键自定义快捷键/宏映射
       • 即使驱动截断仍可识别放行                • 语音键长按/短按智能防吞字
```

### 1. 坚守“原生零注入”准则
Remote Buddy 绝不向 Windows 系统核心驱动注入任何外部代码，绝不打包未知来源的二进制注入器。所有的操作均基于微软官方公开的 Win32 API（`advapi32.dll` / `tdh.dll` / `user32.dll`）与 WinRT 运行时完成，从根本上杜绝蓝屏、驱动崩溃与杀毒软件误杀。

### 2. 双解析引擎（TDH + UserData Raw Buffer）
在 `ChromecastEtwReceiver.cs` 中实现双解析引擎：
```csharp
// 1. 优先尝试系统 TDH 属性提取
byte[]? typeBytes = GetEventProperty(pEventRecord, "BIP_Type");
if (typeBytes != null && typeBytes.Length >= 1 && typeBytes[0] == 3 /* ACL RX */)
{
    aclData = GetEventProperty(pEventRecord, "BIP_Data");
}

// 2. 若系统缺失 TDH 模式，直接下沉解析 EVENT_RECORD.UserData 原始内存
if (aclData == null || aclData.Length == 0)
{
    var record = Marshal.PtrToStructure<EVENT_RECORD>(pEventRecord);
    if (record.UserDataLength >= 4 && record.UserData != IntPtr.Zero)
    {
        // 自动提取 BIP_Type、BIP_DataLen 并切分出标准 L2CAP/ATT ACL 数据帧
        ...
    }
}
```

### 3. 截断报文丢弃与向导重启提示机制
在首次配置注册表参数 `EtwLogSensitiveData=1` 之后、重启电脑或蓝牙适配器之前，Windows `bthport.sys` 可能会对 ACL 数据包进行脱敏截断（保留 7 字节报头，剥除 HID 载荷）。
缺少载荷时既无法判断按键种类，也无法区分按下和松开状态。Remote Buddy 坚持严格正确性原则：
- **安全丢弃截断报文**：绝不随意生成伪按键（如误判为 OK 键），避免向导误判测试通过或在运行时误触 Enter 映射；
- **向导友好引导**：捕获到截断特征（L2CAP CID == 4, ATT Handle == 0x0029 但无载荷）后，在向导界面高亮提示用户“在 Windows 设置中关闭并重新打开一次蓝牙（或重启电脑）使驱动参数生效”；
- **连接句柄绑定隔离**：通过设备握手与 ATVV 专属 GATT 通讯（TX/Audio/Control）自动捕获并绑定当前遥控器的 ACL 连接句柄（ConnectionHandle），严格过滤其他蓝牙外设的同类 ATT 句柄报文，彻底杜绝跨设备误触。

### 4. 首次向导多级消息协同
在 `OnboardingWindow.xaml.cs` 中，除了后台 ETW 监听外，还注入了：
- **`WM_APPCOMMAND` 拦截器**：捕获 Windows 转换的 `APPCOMMAND_BROWSER_BACK`、`APPCOMMAND_VOLUME_MUTE`、`APPCOMMAND_VOLUME_DOWN`、`APPCOMMAND_VOLUME_UP`、`APPCOMMAND_MEDIA_PLAY_PAUSE`；
- **WPF 全局键盘穿透**：捕获方向键（Up/Down/Left/Right）、确认键（Return/Enter/Select）、主页键等，防止 WPF 控件焦点（如“下一步”按钮）吞掉回车键，确保按键测试即按即验。

---

## 四、 参考与致谢项目 (Attribution & References)

在探索与实现 Windows 蓝牙遥控器生态的过程中，特别致谢以下开源项目与社区先驱的思路与探索：

1. **[remote-buddy (macOS 原生版)](https://github.com/zfdang/remote-buddy)**：
   Remote Buddy macOS 原生版本，奠定了优雅简洁的 UI 规范、ATVV 语音状态机、IMA ADPCM 纯软件低延迟解码与时序防吞字架构。
2. **[MoteDeck (HZDavy)](https://github.com/HZDavy/MoteDeck)**：
   启发了 Windows 下对遥控器系统消息与原生按键直通的探索与实践。
3. **[windows-remote-mic-app (BenjmSek)](https://github.com/BenjmSek/windows-remote-mic-app)**：
   在 Windows 蓝牙端口 ETW Event 402 分析与 `hidovergatt.dll` 驱动层逆向方面提供了极为宝贵的技术参考与数据包证据。
