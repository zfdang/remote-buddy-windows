# G9N9N 按键与 Windows 报告

本文针对 VID `18D1`、PID `9450` 的 Chromecast Voice Remote（G9N9N）。同系列 GY3LE 的实机兼容性另行验证。

G9N9N 普通键属于 HID over GATT。Windows 提供 Consumer Control HID 接口，但在 2026-11-23 的测试中，应用能打开直接 HID 接口却未读到报告，WinRT 枚举 HID Report 特征返回 `AccessDenied`。本机可靠的补充路径是绑定到这只遥控器后的 BthPort ETW。

| 按键 | 八字节报告首字节 | Consumer Usage |
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

这张表列的是代码支持的键值。2026-11-23 在修复版真机确认了方向上、确认、返回、主页、静音、YouTube 和 Netflix 的按下与松开。

ETW 解码接受属性 `0x0029` 上首字节为索引、其余七字节为零的报告。例如 `07 00 00 00 00 00 00 00` 表示确认键按下，全零表示松开。属性 `0x0025`、`0x0028`、`0x0029`、`0x002D` 的两字节格式按小端 Consumer Usage 解码，例如 `24 02` 表示返回。同一白名单也接受代码支持的三字节格式，第一字节为报告 ID 1 或 2。两字节报告不能走索引猜测，否则语音停止相关的 `00 02` 可能被误读成播放／暂停。

如果 ETW 只交付了 `0x0029` 的报告头，向导可提示检测到活动但没有键码。应用不会据此触发映射。语音键使用独立的 ATVV GATT 路径，普通键的 ETW 可用性不影响语音协议握手。

## 默认单击动作

| 实体键 | 默认动作 |
| --- | --- |
| 电源 | Backspace |
| 输入源 | 媒体播放／暂停 |
| 主页 | Win+Tab |
| 返回 | Esc |
| YouTube | Ctrl+R |
| Netflix | Ctrl+W |

默认长按仅为方向上、下、左、右、音量加、音量减和电源键启用连发。连发重复该键的单击动作，电源键因此重复发送 Backspace。其他键默认没有长按动作。

新设备及「恢复默认」使用这套配置。已保存的映射可在「按键映射」中单独编辑。

参见 [Windows 按键采集与隔离](windows-remote-input.md) 和 [G9N9N 历史排查](chromecast-g9n9n-button-handling.md)。实现见 [G9N9N 协议](../src/RemoteBuddy.Core/Protocols/G9N9NProtocol.cs) 和 [ETW 接收器](../src/RemoteBuddy.Platform.Windows/Input/ChromecastEtwReceiver.cs)。
