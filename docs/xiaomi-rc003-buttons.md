# 小米 RC003 按键与 Windows 报告

本文记录 2026-11-23 在 Windows 11 上验证的小米蓝牙语音遥控器 RC003，VID `2717`、PID `32B8`，应用中也可能显示为 `MI RC`。其他型号、固件的 GATT 属性和报告格式需要另行实测。

## 普通键采集

| 按键 | 本机采集路径 |
| --- | --- |
| 上、下、左、右 | Raw Input 键盘事件，VKey 分别为 `26 / 28 / 25 / 27` |
| 确认 | Raw Input 的 Enter（VKey `0D`） |
| 主页、菜单 | Raw Input 的 Home（VKey `24`）、应用菜单（VKey `5D`） |
| TV | Raw Input 的 VKey `C0`；实测扫描码（MakeCode） `29` |
| 电源 | 仅把 VKey `FF` 且 MakeCode `5E` 的组合解释为电源 |
| 返回、音量加、音量减 | 已绑定的小米连接上的 ETW 键盘报告，见下表 |
| 语音 | ATVV。实机可直接收到 `AudioStart`，随后以 `AudioStop` 结束 |

三个缺失键的原始通知都来自 ATT 属性 `0x0064`。报告为八字节，无报告 ID。头两字节为修饰位和保留位，本次实测均为零，后六字节为键盘 Usage 槽。

| 操作 | 实测报告（十六进制） |
| --- | --- |
| 确认按下（对照） | `00 00 28 00 00 00 00 00` |
| 菜单按下（Usage `0x65`） | `00 00 65 00 00 00 00 00` |
| 返回按下 | `00 00 F1 00 00 00 00 00` |
| 音量加按下 | `00 00 80 00 00 00 00 00` |
| 音量减按下 | `00 00 81 00 00 00 00 00` |
| 松开 | `00 00 00 00 00 00 00 00` |

返回、音量加、音量减的报告到达了 BthPort，但本机没有交付对应的 Raw Input 按键。当前实现在 `0x0064` 上同时解码 `F1`、`80`、`81` 与菜单 `65`（当菜单被重映射或处于常用语浮窗期间，低级钩子抑制 `VK_APPS` 会一并屏蔽系统的 Raw Input 交付，此时由 ETW 接管菜单键），确认等标准键仍走 Raw Input，避免一次物理按压触发两次。其他已支持的小米报告格式仍独立解析。

在本机逐键验证了返回、音量加、音量减的按下和松开。这里只验证了输入是否交付给映射层；具体执行的动作取决于用户为该键配置的映射。

## 语音键

这只 RC003 按下时可在 ATVV Control 特征值直接发送 `AudioStart`（`0x04`），无需预先收到 HID 语音键或 `StartSearch`。连接就绪且语音空闲时，应用接纳这条流，记录该次 Session ID。松开时实测收到 `AudioStop`（`0x00`）。若另一只设备正在使用语音，应用会拒绝本次会话，保留原来的占用。

## 默认映射

方向和确认对应方向键及 `Enter`，主页为 `Win+Tab`，返回为 `Esc`，电源为 `Backspace`，音量键发送系统音量命令，TV 默认为媒体播放／暂停。菜单键默认动作类型为文本短语，需填写文本后才发送。方向、确认、音量加减和电源默认长按连发，菜单、TV、主页和返回默认不连发。用户保存的映射可以不同。

参见 [Windows 遥控器输入实现](windows-remote-input.md)。实现见 [Raw Input 接收器](../src/RemoteBuddy.Platform.Windows/Input/RawInputReceiver.cs)、[ETW 接收器](../src/RemoteBuddy.Platform.Windows/Input/ChromecastEtwReceiver.cs)和[映射默认值](../src/RemoteBuddy.Core/Models/RemoteDevice.cs)。
