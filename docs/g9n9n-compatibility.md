# G9N9N 多版本兼容性与诊断日志指南

本文说明 Remote Buddy Windows 版在 **Chromecast with Google TV Voice Remote（G9N9N，VID `18D1` / PID `9450`）** 及全系列支持遥控器上的批次兼容方案、各固件/硬件变体的按键报文编码，以及遇到未兼容变种时如何收集诊断日志。

> **关于真机验收范围的说明**：软件层面的构建与单元测试通过**不等于**对应批次已完成真机验收。下文明确区分「已在 Windows 11 真机逐键验证」与「协议已实现并通过单元测试、待对应批次真机验证」的范围。

---

## 一、Remote Buddy Windows 版支持的所有遥控器类型

| 遥控器系列 / 批次 | 硬件标识 | 语音链路 | 普通按键采集路径 | 当前 Windows 验证状态 |
| --- | --- | --- | --- | --- |
| **小米语音遥控器 (`RC003`)** | VID `2717` / PID `32B8`（名称常含 `MI RC`） | BLE GATT ATVV（`AB5E0001`） | Raw Input + BthPort ETW 补取返回与音量加减（含 `0x0064` 八字节报告） | **已在 Windows 11 真机验证**（方向、确认、主页、菜单、返回、音量加减、电源与语音） |
| **小米遥控器 (`RC001` 等同系)** | VID `2717` | BLE GATT ATVV（`AB5E0001`） | Raw Input + Xiaomi HID/ETW 解析 | 协议共享实现，**Windows 真机验收待补** |
| **G9N9N — 8 字节索引批次 (`0x0110`)** | `18D1:9450`（ATT 句柄 `0x0029`） | BLE GATT ATVV（`0x08 -> MIC_OPEN -> 0x04`） | BthPort ETW 解析 8 字节索引 `[code, 0..0]`（命中 `0x0029` 后自动锁定） | **已在 Windows 11 真机验证** |
| **G9N9N — 规范 Report 1 两字节/三字节批次 (`0x0110 / FW 26.2`)** | `18D1:9450`，`HID 0x0110` / 固件 `26.2` | 同上 ATVV 语音链路 | BthPort ETW 解析 2 字节索引 `[code, slot2]` 与含 ID 3 字节 `[0x01, code, slot2]` | 协议实现并通过单元测试，**对应批次 Windows 真机验收待补** |
| **G9N9N — 1 字节短包批次 (`A0 / FW 26.7`, `0x0110 / FW 26.0`)** | `18D1:9450`，如句柄 `0x0046` | 同上 ATVV 语音链路 | BthPort ETW 直接解析 1 字节索引 `[code]` 与 `[0x00]` 松开（以及双键 2 字节 `[c1, c2]`） | 协议实现并通过单元测试，**对应批次 Windows 真机验收待补** |
| **G9N9N — `HID 0x011B` Report 2 批次** | `18D1:9450`，`HID 0x011B` | 同上 ATVV 语音链路 | BthPort ETW 解析 2 字节/3 字节小端 Consumer Usage（及含 ID `0x02` 3 字节） | 协议实现并通过单元测试，**对应批次 Windows 真机验收待补** |
| **Google `GY3LE`** | 同系候选名称识别 | BLE GATT ATVV | 按 G9N9N 同系列规则解析 | **Windows 真机验收待补** |
| **T1-Remote** | VID `2B54` / PID `1600` | BLE GATT ATVV（16 kHz / 8 kHz 等） | Raw Input / 直接 HID / ETW | **Windows 真机验收待补** |

---

## 二、为什么 Windows ETW 能兼容 macOS 丢弃的 G9N9N 批次？

市面上的 G9N9N 兼容款尽管同为 `18D1:9450`、DIS 型号同报 `A0`，甚至 HID Version 同为 `0x0110`，仍存在三种主要 HID 报告实现差异：

1. **`A0 / 固件 26.7` 短报告批次（及现象一致的 `0x0110 / 26.0 CR PVT/MP`）**：HID 描述符声明 Report 1 含两个 8 位槽位（不含 ID 应为 2 字节），但单按和松开只在 ATT 线上发 **1 字节**（如上 `03`、松开 `00`，句柄 `0x0046`）；仅双键同时按下才凑齐 2 字节。
2. **`HID 0x011B` Report 2 批次**：使用 Report ID `2` 的 16-bit 小端 Consumer Usage 编码，描述符声明不含 ID 3 字节，但固件实际仅发送 **2 字节**（如上 `42 00`、松开 `00 00`）。
3. **`0x0110 / 固件 26.2`（或 Windows 8 字节批次）**：发送与描述符匹配的完整 2 字节索引 `XX 00`（带 Report ID 为 `01 XX 00`）或 8 字节 `[XX, 0..0]`。

在 macOS 上，`BTLEServer`（或系统 HID 驱动）会对所有报告严格校验“实际字节数是否等于描述符声明长度”，长度不符会在送达应用层的 `IOHID` 之前被静默丢弃。

而在 Windows 上，Remote Buddy 通过 **BthPort ETW（Event 402）** 直接拦截进入系统 `hidbthle.sys` 解析器之前的 **原始 HCI ACL / L2CAP / ATT（`0x1B` Notification / `0x1D` Indication）帧**。因此只要 Windows 配对后固件发出了 ATT 通知，无论其是否符合描述符长度，Remote Buddy 都能在驱动层拿到完整载荷并还原按下与松开边沿（支持单击、双击、长按）。

> 注意：ATVV 语音按键与 16 kHz IMA-ADPCM 音频流走独立的 BLE GATT 服务（`AB5E0001`），与上述 HID 报告格式无关，在所有已知批次上均采用相同协议交互。

---

## 三、G9N9N 支持的 ATT 载荷格式与 14 键对照表

在蓝牙 ATT 链路上（ETW 抓取的 Notification/Indication Payload，**不含系统外加的 Report ID**，除非固件自身将 Report ID 编码在特性值中），Remote Buddy 目前支持以下 4 类载荷编码：

| 载荷类别 | 长度 | 格式特征 | 松开报文 | 适用批次 |
| --- | --- | --- | --- | --- |
| **8 字节索引 (`Indexed8Byte`)** | 8 字节 | `[idx, 0, 0, 0, 0, 0, 0, 0]`，`idx = 1..17` | `00 00 00 00 00 00 00 00` | Windows 实测 `0x0029` 样机 |
| **1 字节索引 (`Indexed1Byte`)** | 1 字节 | `[idx]`，`idx = 1..17` | `00` | `A0 / 26.7`（实测抓包）、`0x0110 / 26.0` |
| **2 字节索引 (`Indexed2Byte`)** | 2 字节 | `[idx1, idx2]`，`idx1 = 1..17, idx2 = 0..17` | `00 00` (`Release2Byte`) | `0x0110 / 26.2`、`26.7` 双键组合 |
| **2 字节 / 3 字节 Consumer Usage (`ConsumerUsage2Byte` / `ConsumerUsage3Byte`)** | 2 或 3 字节 | 小端 16-bit Consumer Usage `[lo, hi]` 或 `[lo, hi, 00]` | `00 00` 或 `00 00 00` | `HID 0x011B`（短 2 字节或合规 3 字节） |
| **含 Report ID 3 字节 (`ReportId3Byte`)** | 3 字节 | 首字节 `0x01`（双索引）或 `0x02`（2 字节 Usage） | `01 00 00` / `02 00 00` | 显式带 Report ID 头的特征值 |

**数学无冲突保证**：Report 1 所有索引值为 `0x01..0x11`（1~17），而 `G9N9NProtocol.ConsumerButtons` 中所有 16-bit Consumer Usage 的低字节均 `>= 0x21`（33），两者数值空间**完全不相交**。

### 全部 14 颗普通按键码表

| 按键 | `Indexed1Byte`<br>*(26.7 等 1 字节)* | `Indexed2Byte`<br>*(26.2 等 2 字节)* | `Indexed8Byte`<br>*(0x0029 8 字节)* | `ConsumerUsage2Byte`<br>*(0x011B 2 字节小端)* |
| --- | :---: | :---: | :---: | :---: |
| 电源 (`Power`) | `01` | `01 00` | `01 00 ... 00` | `9E 01` (`0x019E`) |
| *(预留索引 2：Play/Pause)* | `02` | `02 00` | `02 00 ... 00` | `CD 00` (`0x00CD`) |
| 上 (`Up`) | `03` | `03 00` | `03 00 ... 00` | `42 00` (`0x0042`) |
| 下 (`Down`) | `04` | `04 00` | `04 00 ... 00` | `43 00` (`0x0043`) |
| 左 (`Left`) | `05` | `05 00` | `05 00 ... 00` | `44 00` (`0x0044`) |
| 右 (`Right`) | `06` | `06 00` | `06 00 ... 00` | `45 00` (`0x0045`) |
| 确认 (`Ok`) | `07` | `07 00` | `07 00 ... 00` | `41 00` (`0x0041`) |
| 静音 (`Mute`) | `08` | `08 00` | `08 00 ... 00` | `E2 00` (`0x00E2`) |
| *(预留索引 9：Menu/App)* | `09` | `09 00` | `09 00 ... 00` | `21 02` (`0x0221`) |
| 主页 (`Home`) | `0A` | `0A 00` | `0A 00 ... 00` | `23 02` (`0x0223`) |
| 返回 (`Back`) | `0B` | `0B 00` | `0B 00 ... 00` | `24 02` (`0x0224`) |
| 音量＋ (`VolumeUp`) | `0C` | `0C 00` | `0C 00 ... 00` | `E9 00` (`0x00E9`) |
| 音量－ (`VolumeDown`) | `0D` | `0D 00` | `0D 00 ... 00` | `EA 00` (`0x00EA`) |
| YouTube | `0E` | `0E 00` | `0E 00 ... 00` | `77 00` (`0x0077`) |
| Netflix | `0F` | `0F 00` | `0F 00 ... 00` | `78 00` (`0x0078`) |
| *(预留索引 16：Custom)* | `10` | `10 00` | `10 00 ... 00` | `79 00` (`0x0079`) |
| 输入源 (`Input`) | `11` | `11 00` | `11 00 ... 00` | `89 01` (`0x0189`) 或 `89 00` (`0x0089`) |
| **松开** | `00` | `00 00` | `00 00 ... 00` | `00 00` |

> **关键修复细节**：`2 字节索引` 解析强制要求首字节 `report[0] >= 1`（即使在 ATT 层直接按两字节解析时也不会将 `01 00` 电源键误认为 Report ID 1 松开，也不会将 ATVV 控制报文 `00 02` 即 `AUDIO_STOP` 误判为 `0x02 Play/Pause`）。

---

## 四、如何保证原有 `0x0029` 已验证型号零回归？

放开其他 GATT 句柄以兼容多批次时，最大的风险是把 ATVV 控制包、电池电量通知（如 `0x0A` 误判成 Home 键 `Win+Tab`）或鼠标包误当作按键。系统通过四层防护消除回归风险：

1. **动态 GATT 布局发现与 HID 句柄范围限制（[WinRtBluetoothManager](../src/RemoteBuddy.Platform.Windows/Bluetooth/WinRtBluetoothManager.cs)）**
   - 连接 G9N9N 后，向系统缓存读取一次完整的 GATT 服务列表，定位 HID Service（`0x1812`）的起止句柄范围 `[HidHandleStart, HidHandleEnd]`。
   - 若成功找到 `0x1812`，只允许该区间内的 ATT 通知进入按键解码；Battery（`0x180F`）、Device Information（`0x180A`）等服务句柄天然落在区间外，无法冒充按键。
   - 若未能获取 `0x1812` 区间，则只放行已实测确认的两个 HID 句柄 `0x0029` 与 `0x0046`。
2. **显式排除 ATVV 与非 HID 特征句柄**
   - `ATVV TX / Audio / Control` 的声明句柄与值句柄（`h` 及 `h + 1`，覆盖诸如音频 `0x003C` 与控制 `0x003F`），以及整个 ATVV 服务区间（`AB5E0001`，在缺少 `0x1812` 时还会补充扫描其余非 HID 特征句柄），均会被加入 `ExcludedAttributes` 并同步序列化传给提权 ETW 子进程；同时在布局发现期间保留活跃 ATVV 服务对象不释放，避免影响语音事件订阅。
   - 任何来自排除句柄的报文在 ETW 入口即被静默丢弃（例如 `START_SEARCH 0x08` 永远不会撞码静音键 `0x08`）。
3. **`0x0029` 首次有效按键自动锁定（零回归锁死机制）**
   - 当已连接设备在经典句柄 `0x0029` 上成功解析到任意一次有效按键按下（`isDown == true`）后，该次连接立即锁定 `LockedG9N9NHandle = 0x0029`，同连接内后续来自其他句柄的报文一律忽略；蓝牙断开或重连后自动清除锁定。
4. **多长度脱敏包检测**
   - 当 Windows 注册表未关闭 BthPort ACL 脱敏时，Windows 会把 1 / 2 / 3 / 8 字节 HID 通知截断为仅剩 3 字节 ATT 头（L2CAP 长度分别为 `4 / 5 / 6 / 11`）。ETW 均能捕获截断并通知向导提示提权或配置注册表。

---

## 五、新变种诊断日志与问题排查 / 反馈方式

如果用户持有的新批次 G9N9N（或兼容遥控器）仍出现“语音能用但部分/全部普通按键无反应”的情况，可直接通过诊断日志定位其真实底层报文格式。

### 1. 日志文件位置

日志默认保存在当前用户的应用数据目录：

```text
%APPDATA%\RemoteBuddy\logs\app.log
```

（单文件上限 5 MB，自动轮转保留 `app.1.log` ~ `app.3.log`。）

### 2. 日志中的关键条目说明

当连接 G9N9N 并按下按键后，`app.log` 会记录以下结构化诊断信息（不包含任何语音波形或口述文本）：

1. **硬件与 PnP 版本信息 (`BluetoothManager`)**
   ```text
   G9N9N hardware details for 0x...: model='A0', firmware='26.7', hardwareRev='...', pnp='18D1 / 9450', hidVersion='0x0110'
   ```
   用于确认遥控器的 DIS 型号、固件版本、硬件修订以及 `PnP ID` 中的 `HID ProductVersion`（例如 `0x0110` 或 `0x011B`）。

2. **GATT 服务布局与筛选区间 (`BluetoothManager`)**
   ```text
   G9N9N GATT layout for 0x...: hidRange=0x0025..0x0050, excluded=[0x0052, 0x0054, 0x0056], services=[0x1800@0x0001..0x000F, 0x1812@0x0025..0x0050, ...]
   ```
   记录本机看到的完整 GATT 服务句柄划分、`0x1812` HID 区间以及排除清单。

3. **首次识别到的报告格式 (`ChromecastEtwReceiver`)**
   ```text
   G9N9N ETW report format observed: addr=0x..., attr=0x0046, format=ConsumerUsage2Byte, len=2, sample=4200, button=Up, down=True
   ```
   确认该设备实际使用的特性句柄、解码格式及原始样本十六进制。

4. **未识别或区间外的按键通知 (`ChromecastEtwReceiver`)**
   ```text
   G9N9N ETW unrecognized notification [UnknownPayload]: addr=0x..., attr=0x0048, opcode=0x1B, len=4, hex=02420000 (hidRange=0x0025..0x0050)
   ```
   * `[UnknownPayload]`：在 HID 句柄范围内收到了暂未识别的载荷长度或键值，`hex=` 即为遥控器发出的原始十六进制字节。超过 **16 字节** 的载荷会出于音频隐私保护记录为 `hex=<omitted>`，绝不会将音频波形写入日志。
   * `[OutsideHidRange]`：非排除句柄但在已计算 HID 区间之外收到了通知（例如某些改写了服务排布的兼容固件）。
   * `[UnlistedAttribute]`：系统未能返回 HID 范围时，回退名单（`0x0029`/`0x0046`）之外收到的通知。
   * **防刷屏机制**：相同 `(reason, attr, len, hex)` 每次连接只记录 1 次，且每条蓝牙连接最多记录 **32 条** 未识别条目，超过后记录封顶提示并停止追加。

### 3. 遇到不兼容型号时如何反馈？

请按以下步骤提供日志信息以便快速适配：

1. 先配对并连接问题 G9N9N 遥控器，等待 **5 秒**（让 `G9N9N GATT layout` 与 `G9N9N hardware details` 写入日志）。
2. 打开向导按键测试页（或主界面），对着无反应的按键依次各短按一下（建议按：**上、下、左、右、OK、返回、主页、静音、音量＋、音量－、YouTube、Netflix、电源、输入源**）。
3. 打开 `%APPDATA%\RemoteBuddy\logs\app.log`（如为隐私考虑可掩码蓝牙 MAC 地址后 6 位），提取包含以下前缀的日志行并在 GitHub Issue 提交：
   - `G9N9N hardware details`
   - `G9N9N GATT layout`
   - `ETW bound`
   - `G9N9N ETW report format observed`
   - `G9N9N ETW unrecognized notification`
   - `Windows omitted a verified button code`

拿到这些行中的 `attr=0x....`、`len=` 和 `hex=` 对照按键顺序后，即可直接为该新变种补充解码支持。
