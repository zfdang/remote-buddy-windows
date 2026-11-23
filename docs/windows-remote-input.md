# Windows 遥控器输入实现

小米 RC003 与 Google G9N9N 都通过 BLE GATT 使用 ATVV 传输语音。普通键的报告格式以及 Windows 交付这些报告的方式各有不同。两条采集路径最终交给同一个按设备分发的映射层。

## 两种遥控器的实现对照

| 环节 | 小米 RC003 | Chromecast G9N9N |
| --- | --- | --- |
| 普通键主要来源 | Windows 将方向键、确认、主页、菜单等交付为 Raw Input 键盘事件 | 本机测试以 BthPort ETW 为主，读取蓝牙连接上的 HID over GATT 通知。应用也接受能确认设备来源的 Raw Input 或直接 HID 事件 |
| ETW 解码 | 在本机实测的八字节键盘报告中补取返回、音量加、音量减。标准键继续走 Raw Input | 解码八字节按键索引及受支持的 Consumer Usage 报告。方向、确认、主页、流媒体等共用这条路径 |
| 语音怎么开始 | 实测可直接收到 ATVV `AudioStart`（`0x04`），应用接纳遥控器主动开启的会话 | ATVV `StartSearch`（`0x08`）触发主机发 `MIC_OPEN`，随后等待 `AudioStart` |
| 语音怎么结束 | 实测松开时收到 ATVV `AudioStop` | ATVV `AudioStop` 参与结束。默认智能模式区分短按免提和长按说话，详见设备文档 |
| 设备码表 | [小米 RC003 的键盘码、ETW 报告和默认动作](xiaomi-rc003-buttons.md) | [G9N9N 的按键索引、Consumer Usage 和默认动作](g9n9n-buttons.md) |

G9N9N 语音正常、普通键全无响应时，优先检查 ETW 是否交付带键码的通知。RC003 其他键正常、仅返回和音量加减无响应时，优先检查 ETW 是否绑定到该小米设备，以及是否收到 `0x0064` 的键盘报告。两种情况都可以同时存在，因为 ATVV 语音与普通键采集各自运行。

## 连接、设备身份与按键路由

1. 每个已配置并就绪的遥控器按蓝牙地址维护独立的 BLE／ATVV 连接。启动时会尝试重连已保存的设备，也可以在设备页面分别连接和断开。映射页面的设备选择决定正在编辑哪一份映射。
2. Raw Input 使用来源句柄及蓝牙地址或 Windows Container ID 匹配实体，入口拒绝缺少来源句柄的输入。直接 HID 读取各设备自己的接口。主窗口普通键映射只接受有设备身份的事件。普通键测试步骤会按向导目标设备筛选有来源的事件。非 Chromecast 设备的测试步骤也可使用本地窗口键盘消息或 `WM_APPCOMMAND`，这些消息只影响向导测试。
3. 已知设备共用一个 BthPort HCIRAW Event 402 跟踪会话。应用通过 WinRT 向每只遥控器发送带唯一 UUID 的 GATT 查询。ETW 在同一个 ACL 句柄上看到查询请求和响应后确认来源，也可在观察到唯一的出站请求后用该连接上的有效按键报告确认。断开事件清除相应绑定。绑定确定设备地址，设备型号决定所用的报告解码器。
4. 采集到的按下和松开按蓝牙地址交给映射层。去重、单击、双击、长按和连发状态都按实体维护。两只同型号遥控器同时按同一个键时，各自使用自己的映射。断开一只设备时会取消它挂起的手势。

实现入口见 [连接管理](../src/RemoteBuddy.Platform.Windows/Bluetooth/MultiRemoteBluetoothManager.cs)、[ETW 采集](../src/RemoteBuddy.Platform.Windows/Input/ChromecastEtwReceiver.cs)和[按键路由](../src/RemoteBuddy.App/ViewModels/MainViewModel.cs)。

## 映射和系统原生按键

小米 RC003 和 G9N9N 的主页键默认都发送 `Win+Tab`，返回都发送 `Esc`，电源都发送 `Backspace`。RC003 的主页进入应用前是 Windows 键盘 `Home`，G9N9N 的主页在蓝牙报告中是 Consumer Usage `0x0223`。Raw Input 是监听接口，Windows 仍可能向前台交付原生按键。对比两只的主页表现时，可以分别查看当前已保存的单击映射和前台应用是否处理了原生键。参见[监听与替换的边界](input-and-hotkeys.md)。

方向键、确认、音量加减和默认单击为 `Backspace` 的电源键启用长按连发，以该型号实际提供的映射为准。其余键默认没有长按动作。映射页面的「恢复所有按键的默认映射」在确认后覆盖当前所选遥控器的单击、长按和双击配置。

## 多遥控器共用语音输出

麦克风空闲时，先成功取得占用权的遥控器开始语音会话。占用覆盖开麦、音频输出和收尾，直到输入法快捷键完成释放。占用期间，另一只遥控器的语音键既不会开新会话，也不会结束当前会话。两只的普通键继续按各自的映射工作。当前设备断开或开麦失败会释放占用，被拒绝的按压需要在空闲后重新按一次。

两种遥控器共用下游路径。ATVV 音频经 IMA ADPCM 解码成 16 kHz PCM，由 WASAPI 写入 VB-CABLE，目标输入法从虚拟麦克风采集。参见 [ATVV 协议](device-protocols.md)和[虚拟音频](virtual-audio-and-driver.md)。

## 诊断和验证范围

- 普通键先确认蓝牙已连接，再检查对应的 Raw Input 或 ETW 报告。ETW 依次检查 Event 402、GATT 查询、地址与 ACL 句柄绑定、带地址的按下和松开。进程内启动 ETW 失败时，应用可能请求 UAC 提权运行采集子进程。
- ETW 会话名为 `RemoteBuddy_BthEtw_S<会话 ID>`。应用启动跟踪时可回收同一 Windows 会话内的同名残留跟踪。若跟踪显示已启动却无 Event 402，可先确认旧进程已退出，再重启应用。
- 2026-11-23 的 Windows 11 真机测试覆盖一只 RC003 与一只 G9N9N、两只的普通键和语音，以及语音占用。各型号已逐键核对的范围见设备文档。
- [ETW 自动化测试](../src/RemoteBuddy.Tests/ChromecastEtwTests.cs)覆盖三只设备，其中两只同型号，验证连接及按下、松开状态隔离。[应用测试](../src/RemoteBuddy.Tests/)覆盖映射及语音占用。三只以上和同型号多只的真机兼容性仍待验证。
