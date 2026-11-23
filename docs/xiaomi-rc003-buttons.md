# 小米 RC003 按键与 Windows 报告

本文记录 2026-11-23 在 Windows 11 上验证的小米蓝牙语音遥控器 RC003，VID `2717`、PID `32B8`，应用中也可能显示为 `MI RC`。其他型号、固件的 GATT 属性和报告格式需要另行实测。

| 按键 | 本机采集路径 |
| --- | --- |
| 上、下、左、右 | Raw Input 键盘事件，VKey 分别为 `26 / 28 / 25 / 27` |
| 确认 | Raw Input 的 Enter（VKey `0D`） |
| 主页、菜单 | Raw Input 的 Home（VKey `24`）、应用菜单（VKey `5D`） |
| TV | Raw Input 的 VKey `C0`；早期实测 MakeCode `29` |
| 电源 | 仅把 VKey `FF` 且 MakeCode `5E` 的组合解释为电源 |
| 返回、音量加、音量减 | 已绑定的小米连接上的 ETW 键盘报告，见下表 |
| 语音 | ATVV。实机可直接收到 `AudioStart`，随后以 `AudioStop` 结束 |

三个缺失键的原始通知都来自 ATT 属性 `0x0064`。报告为八字节，无报告 ID。头两字节为修饰位和保留位，本次实测均为零，后六字节为键盘 Usage 槽。

| 操作 | 实测报告（十六进制） |
| --- | --- |
| 确认按下（对照） | `00 00 28 00 00 00 00 00` |
| 返回按下 | `00 00 F1 00 00 00 00 00` |
| 音量加按下 | `00 00 80 00 00 00 00 00` |
| 音量减按下 | `00 00 81 00 00 00 00 00` |
| 松开 | `00 00 00 00 00 00 00 00` |

这三个键的报告到达了 BthPort，但本机没有交付对应的 Raw Input 按键。此前的 ETW 解码器只认两字节 Usage 或带报告 ID 的小米格式，因此也会丢弃它们。当前实现在 `0x0064` 上只从这种八字节格式补获 `F1`、`80`、`81`，确认等标准键仍走 Raw Input，避免一次物理按压触发两次。其他已支持的小米报告格式仍独立解析。

修复后在本机逐键验证了返回、音量加、音量减的按下和松开。这里只验证了输入是否交付给映射层；具体执行的动作取决于用户为该键配置的映射。

参见 [Windows 按键采集与隔离](windows-remote-input.md) 和 [RC003 早期排查记录](windows-rc003-validation.md)。实现见 [ETW 接收器](../src/RemoteBuddy.Platform.Windows/Input/ChromecastEtwReceiver.cs) 和 [报告测试](../src/RemoteBuddy.Tests/ChromecastEtwTests.cs)。
