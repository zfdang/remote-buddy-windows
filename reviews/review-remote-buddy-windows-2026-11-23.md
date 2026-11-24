# remote-buddy-windows 全仓独立 Review 报告

- **审查对象**：https://github.com/zfdang/remote-buddy-windows ，`main` @ `8d5e82f`（tag `v26-11-23.116`，merge PR #35）
- **审查时间**：2026-11-23
- **审查方式**：6 个独立 sub-agent 并行覆盖「架构与代码质量 / 安全隐私与许可证 / 蓝牙输入与设备协议 / 音频链路 / 测试 CI 与发布 / 与 macOS 版行为对齐」，主 agent 对高影响项逐一读源码交叉验证。全程只读，未修改任何文件，未提交 review、未合并 PR、未做任何公开操作。
- **仓库状态**：无 open PR；本地工作区已 fetch 并快进到最新 `origin/main`，与远端一致。文件规模约 112 个文件，源码 + 测试约 1.9 万行。
- **约束基准**：`/Users/zfdang/workspaces/remote-buddy/AGENTS.md`（无遗留用户/数据、GPL-3.0-only、二进制须附配套完整源码含驱动、零统计/转写历史/录音归档/遥测、公开 API 与无障碍接口、不访问他应用私有数据、build 成功≠真机验收）。该 Windows 仓库自身无 AGENTS.md，故沿用上述项目级约定。

---

## 结论摘要

仓库整体质量高于一般个人项目：分层清晰、无 `async void`、配置持久化有原子替换与损坏恢复、多遥控器代际隔离与跨源去重考虑细致、ETW 解析有边界校验、CI 在 Windows runner 上先测后发。当前 **没有发现会导致启动崩溃或数据丢失的阻断性缺陷**。

需要在合并/发布前处理的问题集中在四类：
1. **隐私与许可**：BthPort 系统级敏感日志开关无用户同意与回滚、Windows 发布未随附匹配源码（GPL）、README 绝对化隐私声明与日志中的设备标识不符。
2. **用户可见状态错误**：设置页与常规页开关不同步、设备页连接失败无反馈、VB-CABLE 就绪只看渲染端、WASAPI 中途失效静默。
3. **协议与输入边界**：陈旧保活回调可终止新会话、Raw Input 设备移除不清按键状态、G9N9N 双键索引只解第一键、ETW worker 管道未知消息类型导致帧失步。
4. **工程真实性**：三条测试以「不抛异常」代替行为断言、图标转换脚本硬编码到旧仓库路径。

以下按「建议合并前处理」与「可选改进」两组列出，不使用严重度标签。所有条目均给出 `文件:行号` 证据与具体取舍。

---

## 建议合并前处理

### A. 隐私、权限与许可证合规

- **BthPort 系统级敏感日志被自动写入且无同意/回滚**（交叉验证）
  - 证据：`src/RemoteBuddy.Platform.Windows/Input/BthPortRegistryHelper.cs:15-18,46-61` 写 `HKLM\SYSTEM\CurrentControlSet\Services\BthPort\Parameters` 的 `EtwLogSensitiveData=1`、`MaxEtwBytes=1024`、`EtwDropLargeEvents=0`；调用点 `ChromecastEtwReceiver.cs:157-159`、`ChromecastWorkerHost.cs:43`、`BluetoothAdapterHelper.cs:145`。全仓无 Restore/Disable 方法，`README.md:48-52` 只说需要管理员权限。
  - 影响：这是持久、系统范围的驱动配置变更，使 BthPort 对**所有**蓝牙连接（不限于本应用设备）输出敏感事件，本机任何有 ETW 读权限的进程都可消费；关闭功能或卸载后不回滚。
  - 建议：做成独立、用户可见的开关（向导/权限页明确说明），仅 G9N9N 普通键捕获需要时启用；写前保存原值，提供「恢复默认」；只由提权 worker 写入。
  - 取舍：增加 UI 与状态持久化；关闭后 G9N9N 的 ETW 普通键捕获失效，需文档说明。

- **公开二进制未随附匹配的完整源码（含捆绑驱动源码）**（与 AGENTS.md:11 冲突）
  - 证据：macOS 版 `docs/source-code.md:10-12` + `.github/workflows/release.yml:195,324` 会打包并上传 `Remote_Buddy_<tag>_sources.tar.gz`（含打补丁的 BlackHole 与 Sparkle 源码）；Windows 版 `.github/workflows/release.yml:62-68,94-102` 的 zip 只有自包含 exe + `LICENSE` + `THIRD_PARTY_NOTICES.md`，无源码归档。同时 `src/RemoteBuddy.Platform.Windows/RemoteBuddy.Platform.Windows.csproj:17-19,25-30` 构建期下载并捆绑 `HIDMaestro.Core.dll`，`README.md:131` 声明使用 `usbip-win2` 签名驱动。
  - 影响：`THIRD_PARTY_NOTICES.md:3-4` 只给链接，未随二进制提供捆绑驱动源码，不满足 GPL "matching complete source including bundled driver sources" 的约束。
  - 建议：按 macOS `scripts/build-release-sources.sh` 模式补 Windows 源码归档，覆盖 HIDMaestro/ usbip-win2 对应版本源码，随每个 Release 上传。
  - 取舍：需维护外部源码镜像与版本对应关系；若驱动源码不可再分发，则应重新评估是否捆绑该驱动。

- **生产发布未使用 GitHub Secrets 中的固定签名证书**
  - 证据：`.github/workflows/release.yml:45-68` 完整流程为 restore → build → test → publish → 复制许可证 → `Compress-Archive`，**无任何签名步骤**；`scripts/build.ps1:99-106` 同样无签名；`README.md:47` 只能引导用户绕过 SmartScreen。
  - 影响：ZIP 内 exe（及单文件内嵌的 HIDMaestro DLL）未签名，用户无法验证发布者身份与完整性，与 AGENTS.md:13 的证书要求不符。
  - 建议：publish 后从 Secrets 临时还原 PFX，用 `signtool sign /fd SHA256` 签名 exe；本地/PR 构建保持不签名。
  - 取舍：需维护 PFX 生命周期；自签仍会触发 SmartScreen（需 EV 证书积累信誉），应同步更新 FAQ 说明。若该约束仅适用于 macOS，应在 Windows 仓库明确记录「Windows 发布刻意不签名」。

- **README「零隐私采集」绝对声明与本地日志中的设备标识不符**（交叉验证）
  - 证据：`README.md:40` 宣称「纯本地运行、零隐私采集……日志仅保留于本地」；实际 `AppLogger.cs:166-184` 记录时间/PID/组件/异常，并在 `OnboardingViewModel.cs:572,842`、`MainViewModel.cs:510,713,775`、`ChromecastEtwReceiver.cs:965`、`HidDeviceReader.cs:277`、`WinRtBluetoothManager.cs:221,259,276,338` 持久化蓝牙 MAC/DeviceId；`App.xaml.cs:25` 还记录完整命令行参数（worker 模式下含 base64 ETW source 列表）。日志在 `%AppData%\RemoteBuddy\logs`，5MB×3 轮转。
  - 影响：MAC 与设备友好名是可关联个人的设备标识符，虽不上传、不记录音频/转写，但「零隐私采集」字面不严谨，与隐私预期冲突。
  - 建议：改为「不采集/不上传个人信息、语音与转写；诊断日志仅本地保存，可能包含蓝牙设备地址」；或对 MAC 掩码/哈希；`App.xaml.cs:25` 改为记录参数个数而非完整内容。
  - 取舍：掩码降低排障定位能力。

- **VB-CABLE donationware 自动下载/自托管镜像与「不打包不二次分发」声明不一致**
  - 证据：`VbCableInstaller.cs:19-20,35-39,50-56` 运行时从 `https://remote.zfdang.com/downloads/drivers/...` 镜像优先下载 ZIP、解压并以 `Verb=runas` 启动官方安装器；`README.md:132` 称「本仓库及安装包不打包或二次分发 VB-CABLE」，`THIRD_PARTY_NOTICES.md:234` 称用户独立安装，未提 donationware 的捐赠/商业授权要求。
  - 影响：项目方自托管镜像 + 一键提权安装，可能被解释为对 donationware 的再分发，绝对化表述与实现冲突。
  - 建议：统一为「用户主动点击后，应用从镜像/官方下载并在 UAC 确认下运行 VB-Audio 官方安装器，ZIP 不随发布包分发」；镜像改为仅回退或移除；补 donationware 条款摘要与官方许可链接。
  - 取舍：移除镜像降低国内下载成功率；保留镜像而措辞不变则需 VB-Audio 书面许可，最终需法务确认。

- **遗留旧项目配置迁移与「无遗留用户」假设冲突**（两个 agent 独立发现，已交叉验证）
  - 证据：`src/RemoteBuddy.Core/Config/ConfigService.cs:39-54` 主配置不存在时从 `%APPDATA%\VoiceInputBuddy\config.json` 复制；macOS 版 `AGENTS.md:5-7` 明确「no legacy users/data，remove obsolete logic instead of compatibility migrations」，macOS 持久化层无 legacy 引用。
  - 影响：这段死代码可能在装有同名目录的机器上把陈旧配置带进新应用，导致向导被跳过或自动连接旧设备。
  - 建议：整段删除（`.bak`/`.corrupt.*` 数据健壮性机制可保留，不属于产品约定冲突）。
  - 取舍：无；若确有迁移需求需补 schema 校验与显式用户提示。

- **旧项目/上游品牌残留**
  - 证据：`scripts/convert-icon.ps1:2` 硬编码 `d:\works\voice-input-buddy-windows\...`；`ConfigService.cs:42` 旧产品目录名 `VoiceInputBuddy`；macOS `README.md:143-145` 有商标与免责声明，Windows `README.md` 无对应声明。
  - 影响：与 AGENTS.md「no upstream branding」不符，且脚本会在开发者机器上写错目录（另见下条）。
  - 建议：清理路径与目录名，补商标免责声明。

- **新手向导按键测试与转写验收校验明显偏松**（与 macOS 对齐要求）
  - 证据：按键测试 macOS `OnboardingFlow.swift:466-470` 要求 `remoteConnected && testedRemoteButtonCount >= 3`；Windows `OnboardingViewModel.cs:729-735` 仅 `HasButtonTested`（单击任意一键即置位，见 `:602`）。转写验收 macOS `OnboardingFlow.swift:460-465` 要求语音会话 + 样本 + 转写出现 + 未检测到手工输入；Windows `OnboardingViewModel.cs:749-765` 仅要求「发生过会话 + 文本框非空 + 勾选」，而 `OnboardingWindow.xaml:212-213` 是可编辑 TextBox + 自述 CheckBox，无手工输入检测。
  - 影响：用户手动输入 + 自勾选即可绕过验收，与 owner 明确保留的 onboarding validation 不符；按键实测门槛也不对等。
  - 建议：按键门槛提到 ≥3 键；转写验收补「转写确实出现」与「手工输入判失败」判定。
  - 取舍： stricter 校验会增加部分用户的向导卡点，需要清晰的失败引导文案。

### B. 音频、设备与协议正确性

- **WASAPI 中途失效后恢复失败对上层完全静默**（已读源码验证）
  - 证据：`VirtualAudioAuditor.cs:406-445` `OnPlaybackStopped` 最多 2 次恢复，最终失败只写日志、无上层事件；`AudioOutputFailed` 仅在首次 `TryStartAudio()` 发出（`WinRtBluetoothManager.cs:2188`）。BLE 音频包仍持续刷新 `_lastAudioPacketTimeMs`（`:1475`），看门狗（`:1323`）不触发。
  - 影响：驱动重启/设备被占用/拔掉后，UI 显示「语音进行中」、IME 已开始录音，但实际无声，直到 5 分钟全局上限才停，README 的防吞字/低延迟承诺在此场景完全失效且无提示。
  - 建议：最终恢复失败后回传 `AudioOutputFailed`，由 BluetoothManager 主动结束会话并释放 IME 快捷键；保留 2 次自动恢复，仅第 2 次失败上报以免瞬时抖动误报。
  - 取舍：跨层回调需避免播放回调线程触发 UI（现有 `Task.Run` 已规避自连接死锁）。

- **VB-CABLE 就绪判定只看 CABLE Input（渲染端），忽略 CABLE Output（捕获端）**（已读源码验证）
  - 证据：`VirtualAudioAuditor.cs:145-200` 找到 `CABLE Input` 且 WASAPI `Init()` 成功即返回 `Ready`（`:196-200`），`CABLE Output` 仅在找到时调音量（`:176-182`），找不到不做判定；`GetCableInputDeviceInternal` 只枚举 `DataFlow.Render`（`:241-248`）。
  - 影响：CABLE Output 被禁用/未注册（半安装状态）时仍报 Ready，但 IME 默认输入设备无信号源，用户看到「已就绪」却无法听写。
  - 建议：同时枚举 `DataFlow.Capture` 检查 CABLE Output 是否 Active；缺失时至少返回「Ready 但 UI 提示 CABLE Output 未就绪」，避免直接否定驱动状态（部分用户可能用物理麦克风）。
  - 取舍：严格判定会误报使用其他捕获设备的用户。

- **陈旧 keepalive 回调可终止新开始的语音会话**（已读源码验证）
  - 证据：`WinRtBluetoothManager.cs:1239-1277`，定时器在写入前校验 `connectionGeneration`/`sessionGeneration`（`:1249-1250`），但 `await WriteCharacteristicWithTimeoutAsync` 最多阻塞 1.5 秒；失败后调用**无代际参数**的 `StopVoiceSessionAsync()`（`:1266,1275`）。
  - 影响：旧会话在这 1.5 秒内结束、新会话已开始时，陈旧回调直接停掉新会话，表现为「刚按语音就断」。
  - 建议：传 `sessionGeneration` 给带校验的重载，停止前复查；或 `StopKeepaliveTimer()` 等待在途回调退出。
  - 取舍：多一个重载或 in-flight 标志。

- **Raw Input 设备物理移除时未清理按键按下状态**（已读源码验证）
  - 证据：`MainViewModel.cs:1111-1117` `HandleDeviceRemoval` 只调用 `InvalidateDeviceCache(hDevice)`，后者仅清 `_devicePathCache`/`_containerIdCache`（`RawInputReceiver.cs:242`）；真正清 `_deviceReportButtons`/`_devicesHoldingF5` 的 `ResetDeviceState` 只在 BLE 连接状态变化时调用（`MainViewModel.cs:303,617,625`）。
  - 影响：拔插后 Windows 复用同一 `hDevice` 时，新设备首帧与旧设备残留按下集合比较，产生针对新设备从未按过的键的幽灵 key-up，并污染 F5 guard。
  - 建议：`GIDC_REMOVAL` 分支调用 `ResetDeviceState(hDevice)` 或「只清状态不派发释放」变体。
  - 取舍：会额外派发一次合成 key-up；映射层 150ms 跨源去重（`MainViewModel.cs:938-949`）通常无害。

- **G9N9N 双键 2 字节索引只解码第一个键，与文档声明不符**（已读源码验证）
  - 证据：`ChromecastEtwReceiver.cs:898-907` 只把 `report[0]` 查表得到 `button`，`report[1]` 仅参与范围校验后被丢弃；`docs/g9n9n-compatibility.md:48` 明确写支持 `[idx1, idx2]`（idx2=0..17）与 26.7 双键组合。`RecordButtonEdgesLocked`（`:583-608`）也只维护单个 Held。
  - 影响：真实双键同时按下时第二个键永不上报，文档承诺的功能不成立。
  - 建议：解析函数输出按键列表并按 Raw Input 和弦方式派发两个短按（`RawInputReceiver.cs:711-720`）；或把文档改为「仅解码首键」。
  - 取舍：解析函数签名与相关单测需调整；真机是否真的发出双键报文仍需验证（见「需真机验证」）。

- **AudioStop「物理松开」语义仅靠第二字节推断，可能误入免提**
  - 证据：`WinRtBluetoothManager.cs:2018-2019,2046-2047` `stopReason = data.Length > 1 ? data[1] : 0xFF`，随后 `ShouldReopenOnPhysicalStop(isRemoteStop: stopReason != 0x00, ...)`；缺省字节的 1 帧 `[0x00]` 被无条件视为物理松开。但 `docs/device-protocols.md:40` 明确记录 `[0x00]` 为 1 字节「遥控器停止确认」。
  - 影响：若某批次用 1 字节表示主机请求关闭后的确认，应用会重开麦克风进入免提，最长持续 5 分钟（`G9N9NSmartToggleSession.cs:18-19`）。
  - 建议：结合会话是否由 `StartSearch(0x08)` 启动、按压时长、本地是否刚发 MIC_CLOSE 判定，而非只看第二字节；真机抓包后固化规则。
  - 取舍：状态机需多维护一个「物理按压发起」标志。

- **ETW worker 管道消息类型无校验，未知类型导致帧失步**
  - 证据：`ChromecastWorkerClient.cs:200-230` 协议为「1 字节类型 + 固定长度载荷」，无长度前缀；读到未知 `msgType` 时不消费载荷，下一轮把载荷首字节当新类型解析。
  - 影响：升级/OAC 错配或同用户进程注入管道时，后续按键事件全部错位，可能解析出伪造的按键与地址。
  - 建议：校验 `msgType` 属于四个已知常量，未知则记录并终止 worker；或改带长度前缀的帧。
  - 取舍：极小。

- **`SendMedia` 部分注入时无 key-up 兜底**
  - 证据：`NativeInputSender.cs:330-346` 的 `[key-down, key-up]` 在 UIPI 拦截或前台切换时可能只成功 1 个；`SendShortcutClick` 已有 `ReleasePartialSendInputs`（`:321-324`），`SendMedia` 未用。
  - 影响：音量/媒体键可能保持逻辑按下造成卡键。
  - 建议：复用 `BuildUnclosedReleaseInputs`（`:152-189`）或纳入 `_heldSyntheticKeys` 统一释放。
  - 取舍：每次媒体键多一次小数组分配。

- **Opening 阶段取消会发送 SessionID=0 的 MIC_CLOSE**
  - 证据：`WinRtBluetoothManager.cs:1098-1115` 在 `Opening` 状态直接取 `_currentSessionId` 发 close（`:1112-1113`），而该值在 `StartVoiceSessionAsync:967` 被置 0；紧随其后的 AUDIO_START 又会被 `wasCancelledOrEnded` 分支再次 close（`:1837-1841`）。
  - 影响：发送 `[0x0D,0x00]` 属无效关闭，产生协议噪声；若固件把 0 解释为「当前会话」可能误关刚建立的流。
  - 建议：`_currentSessionId == 0` 时跳过 MIC_CLOSE，依赖 `_cancelOpenPending` + 拒绝路径。
  - 取舍：取消后遥控器可能在极短时间内继续发音频包，由 4s 看门狗（`:1323-1329`）兜底。

- **文档中 T1 的 VID/PID 与代码矛盾**
  - 证据：`docs/g9n9n-compatibility.md:20` 写 `VID 2B54 / PID 1600`；`T1RemoteProtocol.cs:13-14` 为 `0x620A / 0x0407`，`RawInputReceiver.cs:274,342` 与 `AudioDecoderAndProtocolTests.cs:481-483` 均以 `620A/0407` 为准。
  - 影响：按图索骥的真机验收会找错 HID 设备，排查走偏。
  - 建议：修正文档，并核对是否另有 `2B54:1600` 兼容变体需加入代码。
  - 取舍：仅文档改动。

### C. 用户可见状态与 UI 一致性

- **两个设置页的同名开关互不同步**（已读源码交叉验证）
  - 证据：`GeneralPage.xaml:120-121` 与 `SettingsPage.xaml:29-30` 分别绑定到 `GeneralViewModel` 和 `SettingsViewModel` 各自的副本；两个 VM 都只在构造时读一次配置（`SubViewModels.cs:1428-1429`、`1288-1289`）。`GeneralViewModel.RefreshState()`（`:1449-1489`）刷新了驱动/连接/集成三组属性却没有刷新这两个开关，而导航到 General 页恰恰只调它（`MainViewModel.cs:823-829`）。每个 VM 的 `On…Changed` 又各自写回配置（`:1311-1322`、`:1491-1502`）。
  - 影响：在「设置」页关掉开机启动，回「常规」页仍显示开启；若点击过期复选框会把刚保存的值覆盖回去。
  - 建议：两页共用同一 VM 实例，或 `RefreshState()` 重新从 `_config.Current` 拉取并触发通知。
  - 取舍：改动很小；若刻意保留两页独立，至少确保每次激活都同步并去掉重复写回。

- **设备页连接失败没有任何反馈**
  - 证据：`SubViewModels.cs:279-308` `ConnectDeviceAsync` 只有 `if (ok && IsDeviceReady(...))` 成功分支，失败时既不更新 `DeviceConnectionStatus` 也不设 `ValidationError`；向导里的实现明确写失败原因（`OnboardingViewModel.cs:575-593`）。
  - 影响：遥控器没电、被独占、语音服务协商失败时反复点「连接」什么都没有，只能猜。
  - 建议：失败分支写入可读状态与错误提示，复用向导文案。
  - 取舍：注意不要与 `OnConnectionStatusChanged` 后续推送的 false 事件重复覆盖文案。

- **映射页无法录制单独的 RAlt/RCtrl 等修饰键**
  - 证据：`MappingPage.xaml.cs:146-173` 在 `:158-159` 遇任意修饰键直接 `return`，而方法开头 `e.Handled = true`（`:149`）；`VoiceShortcutRecorder` 支持独立修饰键及松开提交（`:42-50`、`:88-110`），且豆包默认快捷键就是 RAlt（`OnboardingViewModel.cs:48`）。
  - 影响：按产品指引把豆包配成 RAlt 的用户，在按键映射里无法用同一个 RAlt 触发动作。
  - 建议：把 `VoiceShortcutRecorder` 提取到 App 层共享，映射页直接复用；不推荐在映射页复制一份录制逻辑（会继续漂移）。
  - 取舍：需小幅重构文件位置。

### D. 工程真实性 / 测试 / 脚本

- **三条测试以「不抛异常」代替行为断言**（已读源码验证）
  - 证据：`MainViewModelTests.cs:164-180` `MainViewModel_HandleDeviceRemoval_DoesNotThrow` 调用后无任何 `Assert`；`HidDeviceReaderTests.cs:93-99` 名为 `..._ClearsCachedEndpointId` 却只调 `EnsureEndpointUnmutedAndMaxVolume(null)`，注释写 "Should not throw"；`G9N9NSmartToggleTests.cs:106-134` 名为 `..._MaintainsPhaseConsistency`，500 次并行转换后只 `Assert.Empty(exceptions)`。
  - 影响：设备移除、generation 状态机正是易回归区域，这些绿色信号不对应任何被保护的行为。
  - 建议：移除设备后断言 `Assert.Empty(vm.MappingVm.Devices)` 与 `Assert.Null(config.Current.SelectedDeviceId)`；缓存清除用 mock audit 断言未命中；并发测试断言最终相位属于合法集合（如 `Idle`/`AwaitingReopen`）。
  - 取舍：相位断言要允许合法中间态，不能写死单一结果。

- **`convert-icon.ps1` 指向另一仓库的硬编码绝对路径**（两个 agent 独立发现，已读源码验证）
  - 证据：`scripts/convert-icon.ps1:1-2` 源为 `d:\works\remote-buddy\assets`，目标为 `d:\works\voice-input-buddy-windows\src\RemoteBuddy.App\Resources`（旧项目名）；`:4-7` 会把 4 张 PNG 覆盖写入该错误目录，`:36-67` 生成的 `app.ico` 也落到那里；脚本无 `$ErrorActionPreference = "Stop"`、无源文件存在性校验。
  - 影响：不进 CI/发布链路，但任何按字面运行的开发者会把资源写进另一个代码库，且失败会被掩盖。
  - 建议：改为基于 `$PSScriptRoot` 的相对路径或 `-SourceDir` 参数，加 `$ErrorActionPreference` 与 `Test-Path` 前置检查。
  - 取舍：若资产确实在外部，需显式传参而非硬编码。

- **配置并发写可能静默丢失**（已读源码验证）
  - 证据：`ConfigService.cs:24` 以可变对象直接暴露 `Current`，`Save()` 只在自己内部加锁（`:294-349`），调用方在锁外直接改它（`SubViewModels.cs:406,1314,1320,1582`）；增益保存由 `System.Threading.Timer` 在线程池回调执行（`:228-250`）；`SaveLocked()` 对异常只记日志后吞掉（`:338-341`），而 `CreateSnapshot` 会遍历 `Devices`/`Mappings`（`:351-403`）。
  - 影响：后台保存与 UI 线程改集合并发时 `CreateSnapshot` 可能枚举到被修改的集合而抛异常，保存被吞且无提示，重启后设置回退。
  - 建议：写操作收敛到 `Update`/`UpsertDevice` API，`Current` 对外只读/返回快照；让 `Save` 返回成功状态或抛可观察错误。短期至少把防抖定时器改为 UI 线程触发或统一走 `Update`。
  - 取舍：完整改造面较大；第一步改动小。

---

## 可选改进

### 架构与可维护性

- `IBluetoothManager` 仍是「单设备形状」接口（`IBluetoothManager.cs:24-45` 的 `ConnectedDeviceAddress`/`CurrentGain`/`IsReady`），多实现里定义为「当前/最近连接」设备（`MultiRemoteBluetoothManager.cs:37-66`）；App 层直接依赖具体类型（`App.xaml.cs:82,168-194`、`MainViewModel.cs:11-12,631`、`SubViewModels.cs:13-16`）。未导致运行时错误，但单设备分支在生产中是死代码，只服务测试。建议拆出更小的 App 特有接口并把组合根集中在 App。
- `CancellationTokenSource` 取消后立即 `Dispose` 有竞态：`MainViewModel.cs:408-419` 在 `Cancel()` 后立刻 `Dispose()`，而 `ChromecastButtonReceiver.cs:111-119` 仍会用该 token `CreateLinkedTokenSource`，可能抛 `ObjectDisposedException`；同类写法见 `MainViewModel.cs:472-515`、`ChromecastEtwReceiver.cs:1008-1015`。建议先 Cancel、等在途 Task 结束再 Dispose，或不 Dispose。
- 映射编辑每次输入都同步写盘（`SubViewModels.cs:627-653,975-1004` → `ConfigService.cs:273-284`），HID 枚举每次全量执行（`HidDeviceReader.cs:193-229`）并在多处重复调用（`SubViewModels.cs:58-69,155-166`、`MainViewModel.cs:385-399`）。建议加映射编辑防抖（已有增益防抖模式）与 HID 枚举短时缓存；缓存需配合现有 `WM_INPUT_DEVICE_CHANGE` 失效逻辑，代价是热插拔识别延迟。
- 测试钩子散落生产代码：`SubViewModels.cs:327,415-426` 用反射扫 AppDomain 跳过确认框，`MainViewModel.cs:244,422,529`、`WinRtBluetoothManager.cs:1602-1604` 有 `*ForTest` 入口。建议用 `IConfirmationService` 等接口注入替换。
- 重复逻辑：`MappingPage.xaml.cs:175-193` 与 `VoiceShortcutRecorder.cs:147-169` 的快捷键格式化；`AboutViewModel.cs:1627-1638` 与 `SettingsViewModel.cs:1292-1303` 的版本读取。
- `AppLogger.WriteEntry`（`AppLogger.cs:160-209`）在调用线程同步 `File.AppendAllText` 并取锁，BLE 回调与 UI 线程高频调用；`Flush()`（`:257-260`）是空实现却被 `App.xaml.cs:61,67,162` 当可靠性保障调用；`OpenLogFolder` 的 macOS 分支（`:282-285`）是死代码。建议后台批量刷盘、删空 `Flush()` 或明确文档化同步写盘是有意设计。代价是崩溃前少量日志可能丢失。

### 蓝牙 / 输入细节

- 保活周期 `WinRtBluetoothManager.cs:1282` 为 8000ms，`docs/device-protocols.md:41,61` 写「超过 10 秒时每隔数秒下发」。8 秒更保守，建议同步文档。
- `WinRtBluetoothManager.cs:2204-2213` `Dispose()` 停止 watcher 但没 `Dispose()` 它；`MultiRemoteBluetoothManager` 会创建 N+1 个 manager（`:23,103`），反复连接/移除可能累积原生句柄。
- `HidDeviceReader.Stop()`（`HidDeviceReader.cs:360-374`）只清 `_pressedButtons` 不派发 key-up，切换 HID 路径但同一遥控器时手势层可能仍认为键按下。
- `NativeInputSender.cs:267-280` `SendShortcutDown` 失败时调 `ReleaseAllHeldSyntheticKeys()`，会连带释放其他 Still-held 快捷键持有的修饰键；建议只回滚本次 inputs。
- 非 F5 快捷键时仍吞掉系统 F5：`RemoteVoiceKeyGuard.cs:89-117` 与 `MainViewModel.cs:853-867` 无条件安装 `WH_KEYBOARD_LL`，`_ownsF5` 分支不受 `IsSuppressionEnabled` 控制，用 RAlt 时语音会话期间浏览器刷新会被吞。建议仅当快捷键含 F5 时安装 hook；代价是需权衡双注入防护。
- `MainViewModel.cs:931-951` 的 `_dedup`/`_buttonLogicalDown` 在断开时未清（`ResetDeviceGestures` 只重置手势），150ms 内重连可能被误判重复而丢键。
- `RawInputReceiver.cs:495-498` `DeviceFilter` 拒绝 key-up，若设备在下压期间变为非目标则释放事件被丢弃；`ResetDeviceState` 仅在 BLE 连接变化路径调用。建议 key-up 不走过滤。
- `WinRtBluetoothManager.cs:754-765` 的 ETW 属性排除集合上限 32，HID 服务含大量特征时后续特征通知不会被排除，可能被当按键；当前设备特征数远低于上限。
- `WinRtBluetoothManager.cs:624-633,683-689` GATT 服务发现失败时任何异常都回退到同一 Invariant，`docs/g9n9n-buttons.md:5` 明确 G9N9N 上有 `AccessDenied`，日志无法区分权限问题与固件缺失；建议按 `GattCommunicationStatus` 细分。

### 音频体验与可观测性

- 文档数值与实现漂移：预卷代码 `VirtualAudioAuditor.cs:263,315` 为 1600 字节=50ms，`docs/virtual-audio-and-driver.md:106` 写 20~40ms、`docs/input-and-hotkeys.md:45` 写 50~100ms；排空代码 `:466-552` 上限 450ms+45ms，`docs/input-and-hotkeys.md:55` 写 40~60ms。建议统一到实测值。
- 共享模式重采样未测量：`VirtualAudioAuditor.cs:189-193` 发现 MixFormat 非 16k 只打日志，`:381` 缓冲 500ms + `:382` `DiscardOnBufferOverflow=true`。当前取舍偏稳健、合理，主要缺端到端延迟的可观测性。
- `ATVVProtocol.cs:179-200` 的 `AUDIO_SYNC` 帧号对齐依赖「delta = frameNumber - _decodedFrameIndex」的解释，`:219-225` 在 Append 回调前已自增 `_decodedFrameIndex`；若遥控器用 1-based 帧号会有 15ms 偏移 → 爆音。现有测试锁定当前解释但未用真实协议数据验证，**不建议在无抓包情况下「修正」**，应先真机确认。
- `VirtualAudioAuditor.cs:75-98` 每次会话把 CABLE Input/Output 设为 100% 且取消静音，会话结束不恢复。建议记录原值恢复，或仅在静音时取消静音。
- 第二遥控器被仲裁拒绝时无 UI 解释：`MultiRemoteBluetoothManager.cs:211-215` first-come-first-served，`WinRtBluetoothManager.cs:2177-2181` 仲裁失败不发 `AudioOutputFailed`。建议加轻量「已被其他遥控器占用」提示并节流。
- `AudioLevelMeter.cs:21` 用 `rms * 4.0` 再 clamp，约 -9dBFS 以上即满格，电平条长期满格失去判断价值；建议改 dB 映射并标定语音范围。
- 安装 VB-CABLE 后默认播放设备变成 CABLE Input 导致扬声器无声，完全依赖 README:54 提示用户手动切回；应用不检测也不引导（`MainViewModel.cs:199-207` 只在输出失败时弹横幅）。当前纯手动方案已被文档覆盖，风险可接受。

### 安全加固（低风险项）

- CI Actions 用可变 major tag（`release.yml:25,30,71,89` 的 `@v4`），建议固定 40 位 commit SHA 并配 Dependabot。
- `scripts/build.ps1:48-52` 从 `https://dot.net/v1/dotnet-install.ps1` 下载后以 `-ExecutionPolicy Bypass` 执行，无 SHA 校验；虽来自微软 HTTPS 官方主机且不在 CI 路径，建议要求预装 SDK 或校验官方 SHA。
- `VbCableInstaller.cs:22` `AllowAutoRedirect=false` 可能使官网回退失效（若 URL 302 到 CDN）。建议仅在 SHA 通过前提下允许重定向或限制到官方 host 白名单。
- HIDMaestro 固定哈希（`RemoteBuddy.Platform.Windows.csproj:25-30`）与 VB-CABLE 哈希（`VbCableInstaller.cs:17`）本次离线未与上游公布值核对；机制本身安全（改内容必破坏哈希、无 TOCTOU、2MB 上限、内存字节校验后解压），建议上线前人工核对一次。
- `RemoteVoiceKeyGuard.cs:40-57` 安装系统全局 `WH_KEYBOARD_LL` 钩子，代码只检查 F5 且其余放行（`:76-117`），有 5s/310s 上限（`:12-13,92`），但 README 隐私段未披露。建议注明「仅语音会话期间抑制 F5，不记录其他按键」。
- `BluetoothAdapterHelper.cs:204-205` 把适配器 InstanceId 以单引号插入 PowerShell 脚本，InstanceId 来自 HKLM Enum（非常规用户可控）；建议白名单正则校验。
- `ChromecastWorkerHost.cs:45` 仅凭名称连接管道，`App.xaml.cs:168-195` 对 `pipeName` 无格式校验。随机 GUID 管道名 + ACL（仅当前用户 + Administrators，`ChromecastWorkerClient.cs:72-92`）已提供基础保护，风险低；建议校验 GUID 格式。

### 测试、CI 与发布工程

- `scripts/release-metadata.sh:24` 用 `count % 1000` 生成标签后缀，超过 1000 提交会回绕到 000 并与历史标签碰撞；建议保留格式但在超过 1000 时告警。
- `scripts/build.ps1:2-5` `[switch]$Publish = $true` 默认即发布，与 PowerShell 惯例相反；`:70-76,99-105` 未传 `-p:Version` 导致本地产物版本号与发布产物不同；`:66,81-85` 强杀 `RemoteBuddy.App` 与 `testhost*` 可能终止 IDE 中并行测试；`scripts/test.ps1:57` 无条件 `--no-build` 可能测试陈旧二进制。建议在 build 成功提示中写明「未运行测试、未注入发布版本号」。
- `.github/workflows/release.yml:65-68` 复制 LICENSE 与 THIRD_PARTY_NOTICES 后直接打包，无 `Test-Path` 断言；`README.md:159` 未提及实际同包分发的 `licenses/` 目录（由 `csproj:18-19` 带入）。建议加三连 `Test-Path` 校验（GPL 合规的失败点值得）并更新 README。
- `docs/architecture.md:40` 仍写打包方式为 Inno Setup，实际是自包含单文件 zip（`release.yml:51-68`、`README.md:46,159`）；属设计期文档漂移。
- 脆弱测试写法：`AudioLevelMeterTests.cs:83-89` 用 `sw.ElapsedMilliseconds < 250` 判「立即返回」在高负载 runner 上可能 flake；多处用反射读写私有字段（`RemoteVoiceKeyGuardTests.cs:18-20,63-64,117-118,139-140` 等）；`ConvertersAndViewModelTests.cs:185` 设置全局静态 `ConfirmDeviceRemovalHandler` 未复位（所幸 `AssemblyInfo.cs:6` 全局禁用并行）；`ConfigServiceTests.cs:153-194` 并发保存只断言 NotNull/NotEmpty 且 `Task.WhenAll` 无超时。
- Release Notes 是固定模板字符串（`.github/workflows/release.yml:102-105`），无 changelog；无 `workflow_dispatch`，无法对指定提交手动补发。
- 构建期依赖 GitHub Releases 下载 HIDMaestro（`csproj:22-31`），有 SHA 校验、可复现，但 CI 可用性受上游影响。

### 跨端对齐记录（不构成缺陷）

- 集成子向导步数不同：macOS `OnboardingSteps.swift:60` 为 4 步，Windows `OnboardingViewModel.cs:347-352` 为 3 步（触发方式与工具配置合并），主向导 8 步与添加设备 4 步两侧一致，功能无缺失。
- 完成更新检测在 Windows 不适用：macOS `RemoteBuddyApp.swift:171-178` + `AppSettings.LaunchTracking.swift` 通过 CFBundleVersion 与 `SUHasLaunchedBefore` 判断；Windows 无应用内更新器，靠手动下载，属 N/A。若未来引入更新需同步补完成检测。
- 配置 schema / 设备标识符不共享：macOS `RemoteDeviceProfile.swift:117-127` 用 UUID + HID fingerprint，Windows `RemoteDevice.cs:28-36,109-117` 用 `ulong BluetoothAddress`/`Guid? ContainerId`，路径格式都不同，互读前提不存在。
- G9N9N OK 键长按默认不同：macOS `AppSettings.ButtonMappings.swift:701-703` 为 `Command+Return`，Windows `RemoteDevice.cs:208-222,249` 为 `ContinuousRepeat`；属平台快捷键适配，非约定冲突。

---

## 需真机验证、单测无法覆盖

1. G9N9N 各变体真实报文：2 字节索引 `[idx1,idx2]`（26.2/26.7 双键）、1 字节短包（A0/26.7、0x0110/26.0）、HID 0x011B 小端 Consumer Usage。`ChromecastEtwTests.cs` 全为构造 ACL 包，无法证明固件实际格式与句柄。
2. AudioStop 第二字节语义：`[0x00]` 1 字节、`[0x00,0x00]`、`[0x00,reason]` 在不同批次分别代表什么，直接决定「松开即停」还是「进免提 5 分钟」。
3. 双键组合是否真由固件发出，及第二键真实行为（对应上文只解第一键问题）。
4. 5 分钟免提上限与固件自身约 60 秒超时、smart-toggle deadline（`WinRtBluetoothManager.cs:1389-1409`）的实际先后关系。
5. 两只同型号遥控器同时按同一键时，ACL 句柄绑定 + Raw Input hDevice 区分 + 手势/去重三层叠加的真实行为。
6. Raw Input 拔插后 hDevice 复用（对应上文幽灵 key-up 问题）。
7. UIPI / 管理员边界：40 字节 INPUT 结构体正确不等于 `SendInput` 能注入提权窗口；USB/IP 路径依赖真实签名驱动与豆包版本。
8. USB/IP 长按模式在豆包「持续模式」下能否稳定产生无 `LLKHF_INJECTED` 的事件。
9. ETW GATT 探针字节序（`ChromecastEtwReceiver.cs:120-121` 的 bigEndian+Reverse）与 WinRT `GetGattServicesForUuidAsync` 实际报文是否逐字节一致，需抓包或核对 `ETW bound` 日志。
10. T1 / RC001 / GY3LE 全型号键位：`T1RemoteProtocol.DecodeReport`（`:65-190`）中 `VoiceHoldUsage=0x00AA`、`Menu=0x65` 等为推断值。
11. ADPCM nibble 顺序：`device.Name.Contains("arn9")`（`WinRtBluetoothManager.cs:423`）与 DIS 判定（`:428-437`）是否覆盖所有出货固件（含本地化名称、DIS 读取失败）。
12. AUDIO_SYNC 帧号基准与一帧偏移（15ms）、真实采集数据回放、WASAPI 中途恢复、CABLE Output 存在性、端到端防吞字/尾音、多遥控器竞争、跨会话采样连续性。

---

## 正面观察

- **分层与依赖干净**：`RemoteBuddy.Core.csproj:2-9` 目标 net10.0、无项目引用、无 Windows API；Platform/App 单向引用 Core，无循环依赖；Core 内未见 Windows 专有类型。
- **无 `async void`**；BLE/ETW/HID 回调进入 UI 前普遍经 `RunOnUi`/`Dispatcher.BeginInvoke`（`MainViewModel.cs:531-536,648-664,735-742`），并用 token + generation 丢弃过期事件（`:559-567,666-743,745-806`）。
- **配置持久化稳健**：临时文件 + `File.Replace` + `.bak` 回退（`ConfigService.cs:294-349`）、损坏文件隔离（`:73-113`）、加载时去重与默认值补全（`:121-152,154-212`），有并发保存/损坏恢复回归测试。
- **x64 INPUT 结构体正确**：`NativeInputSender.cs:9-51` 显式 union 使 `Marshal.SizeOf<INPUT>()` 为 40 字节，与 `docs/input-and-hotkeys.md:65-67` 一致且有测试断言。
- **ETW/ATT 解析边界稳健**：`ChromecastEtwReceiver.cs:703-726` 校验 HCI ACL 头、PB/BC 位、`aclLength == l2capLength+4`、L2CAP CID=4；`GetEventProperty`（`:970-1006`）限 65536 字节；`ProcessEventRecord`（`:361-382`）双重校验长度；协议无 varint，未发现越界读取；>16 字节载荷记 `<omitted>` 且每连接最多 32 条（`:674-698`）。
- **活跃 ATVV 服务保留正确**：`WinRtBluetoothManager.cs:671-681` 的 `finally` 用 `ReferenceEquals` + UUID 双重判断，避免释放正在承载通知的 GattDeviceService（commit 0d02598 的核心风险已落地）。
- **多遥控器地址作用域与代际隔离完整**：`MultiRemoteBluetoothManager.cs:104-121,157-185` 每地址独立 manager；`WinRtBluetoothManager.cs:968-977,1022-1042,1174-1183` 三重 generation 校验；`WinRtBluetoothManager.cs:559-568` 在 ACL 句柄被另一来源重绑定时重置旧来源。
- **音频核心实现正确**：标准 IMA StepTable/IndexTable（`ImaAdpcmDecoder.cs:8-20`）、predictor/stepIndex 双 clamp（`:70-88`）、跨通知分帧与尾包 Flush（`ATVVProtocol.cs:212-254`）、ArrayPool `finally` 归还（`WinRtBluetoothManager.cs:1566-1572`）；WASAPI 恢复在回调线程外 `Task.Run` 避免自连接死锁（`VirtualAudioAuditor.cs:438-444`）；短语音强制释放预卷（`VirtualAudioAuditor.cs:473,526`）。
- **供应链护栏到位**：HIDMaestro 与 VB-CABLE 均 HTTPS + SHA-256 固定 + 2MB 上限 + 内存字节校验后解压 + 临时目录用后删除 + 退出码白名单 + UAC 取消处理（`csproj:25-28`、`VbCableInstaller.cs:16-22,35-87,103-118`）；`app.manifest:7` 为 `asInvoker`，开机自启只写 HKCU Run（`StartupService.cs:68-78`），提权均经 UAC `runas`，提权 worker 用随机 GUID 管道 + ACL。
- **无遥测/统计/第三方分析 SDK**；生产代码唯一网络出口是 VB-CABLE 下载（`VbCableInstaller.cs:19-20`）；**不保存录音、不保存识别文本、日志无音频/波形/转写内容**（`AppLogger.cs:17-21`）。
- **CI 门禁完整**：`release.yml:24-75` 严格 checkout → setup → 版本 → restore → build → test → publish → archive，测试在 `windows-latest` 执行，失败阻断发布；publish job 有 `needs: build` + push/main 双重约束（`:78-79`）；权限最小化（`:9-10,81-82`）；制品名含 tag 且 `if-no-files-found: error`；重复运行用 `gh release upload --clobber` 幂等。
- **硬件验收边界表述清晰**：`docs/g9n9n-compatibility.md:5` 明确「构建与单元测试通过不等于真机验收」，`README.md:68-72` 对 RC001/GY3LE/T1 标注「Windows 真机验收待补」，符合 AGENTS.md 约定。
- **与 macOS 主干约定一致**：零遥测/无转写历史、主向导 8 步 + 添加设备 4 步、「重连」对所有含蓝牙标识的 profile 可见（Windows `DevicesPage.xaml:73,84-87`，未加 `!IsConnected` 条件，符合 owner 指示）、按键映射按设备隔离（300ms 双击阈值两侧一致）、同型号换硬件走全新配置无 rebind、音频就绪门控（`OnboardingViewModel.cs:706-717`）。

---

## 审查范围与限制说明

- 本机为 macOS + dotnet 10.0.402，WPF/WinRT/WASAPI 项目无法在本机真实构建，因此未执行 `dotnet test`；这是环境限制，不作为缺陷。CI 在 `windows-latest` 上运行测试，已静态确认门禁有效。
- 本次为源码静态审查 + 与 GitHub 远端交叉核对；未做真机验证、未联网核对两个固定 SHA-256 与上游公布值、未做 UI 交互测试。
- 本次未提交 GitHub review、未合并 PR、未修改仓库文件；本地工作区 `git status` 干净。

**报告文件**：`/Users/zfdang/workspaces/remote-buddy/.workbuddy/review-remote-buddy-windows-2026-11-23.md`
