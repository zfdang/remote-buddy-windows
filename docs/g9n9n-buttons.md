# G9N9N 按键与 Windows 报告

本文针对 VID `18D1`、PID `9450` 的 Chromecast Voice Remote（G9N9N）。同系列 GY3LE 的实机兼容性另行验证。

G9N9N 普通键属于 HID over GATT。Windows 提供 Consumer Control HID 接口，但在 2026-11-23 的测试中，应用能打开直接 HID 接口却未读到报告，WinRT 枚举 HID Report 特征返回 `AccessDenied`。本机可靠的补充路径是绑定到这只遥控器后的 BthPort ETW。

## 普通键采集

| 按键 | 索引（1 / 2 / 8 字节 Report 1） | Consumer Usage（Report 2 / `0x011B`） |
| --- | --- | --- |
| 电源 | `01` | `019E` |
| 播放／暂停 | `02` | `00CD` |
| 上、下、左、右 | `03 / 04 / 05 / 06` | `0042 / 0043 / 0044 / 0045` |
| 确认 | `07` | `0041` |
| 静音 | `08` | `00E2` |
| 菜单、主页、返回 | `09 / 0A / 0B` | `0221 / 0223 / 0224` |
| 音量加、音量减 | `0C / 0D` | `00E9 / 00EA` |
| YouTube、Netflix | `0E / 0F` | `0077 / 0078` |
| 自定义、输入源 | `10 / 11` | `0079 / 0089`（输入源还接受 `0189`） |

这张表列的是代码支持的键值。2026-11-23 在 Windows 11 真机确认了 `0x0029` 八字节样机的方向上、确认、返回、主页、静音、YouTube 和 Netflix 的按下与松开；其余批次（`0x0110 / 26.2` 2 字节索引、`A0 / 26.7` 与 `0x0110 / 26.0` 1 字节短包、`HID 0x011B` 2 字节小端 Consumer Usage）已在协议和 ETW 层兼容并通过单元测试，待对应批次真机验收，详见 [G9N9N 多版本兼容性与诊断日志指南](g9n9n-compatibility.md)。

连接 G9N9N 后，应用会读取 GATT 服务布局定位 HID 服务（`0x1812`）句柄范围并排除 ATVV（`TX / Audio / Control`）等非 HID 特征；如在经典句柄 `0x0029` 上确认到按键按下，则立即锁定 `0x0029` 以确保已验证机型零回归。ETW 接收器支持 8 字节 `[idx, 0..0]`、1 字节 `[idx]`、2 字节索引 `[idx1, idx2]`（强制首字节 `>= 1`，避免将 ATVV `AUDIO_STOP` `00 02` 误读为播放／暂停）、2/3 字节小端 Consumer Usage 及含 Report ID `1/2` 的 3 字节报告，并将未识别的 ATT 通知和硬件信息记录到诊断日志以辅助排查新变种。

如果 ETW 只交付了受支持 HID 特性（如 `0x0029` / `0x0046` 或 HID 区间内特性）的报告头，向导可提示检测到活动但没有键码。应用不会据此触发映射。语音键使用独立的 ATVV GATT 路径，普通键的 ETW 可用性不影响语音协议握手。

## 语音键

按下语音键时，设备可通过 ATVV Control 特征值发送 `StartSearch`（`0x08`）。应用取得语音占用后发送 `MIC_OPEN`，由 `AudioStart` 确认会话并提供 Session ID。普通按键报告缺失时，这条语音路径仍可工作。

默认智能模式以 700 毫秒区分短按与长按。短按松开触发的 `AudioStop` 会让应用尝试重开麦克风，进入免提，之后再按一次结束。长按松开则收尾并结束会话。设备设置也可选择按住说话模式。免提期间也会持续占用语音输出。实现见 [智能语音键状态机](../src/RemoteBuddy.Core/Protocols/G9N9NSmartToggleSession.cs)。

## 默认单击动作

| 实体键 | 默认动作 |
| --- | --- |
| 电源 | Backspace |
| 输入源 | 媒体播放／暂停 |
| 主页 | Win+Tab |
| 返回 | Esc |
| YouTube | Ctrl+R |
| Netflix | Ctrl+W |

默认长按仅为方向上、下、左、右、确认、音量加、音量减和电源键启用连发。连发重复该键的单击动作，确认键重复发送 Enter，电源键重复发送 Backspace。其他键默认没有长按动作。

新设备及「恢复所有按键的默认映射」使用这套配置。已保存的映射可在「按键映射」中单独编辑。

参见 [G9N9N 多版本兼容性与诊断日志指南](g9n9n-compatibility.md) 和 [Windows 遥控器输入实现](windows-remote-input.md)。实现见 [G9N9N 协议](../src/RemoteBuddy.Core/Protocols/G9N9NProtocol.cs)、[ETW 接收器](../src/RemoteBuddy.Platform.Windows/Input/ChromecastEtwReceiver.cs) 和 [蓝牙连接管理](../src/RemoteBuddy.Platform.Windows/Bluetooth/WinRtBluetoothManager.cs)。
