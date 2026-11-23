# 豆包语音快捷键与 USB/IP 虚拟键盘

## 为什么普通模拟按键会失败

豆包的「长按模式」区分左右修饰键，并分别响应按下与松开。Windows 的低级键盘钩子可以看到这些信息，还可以从 `LLKHF_INJECTED` 辨别软件模拟的按键。参考项目记录了豆包语音路径会忽略带此标志的事件。豆包没有公开当前版本的内部判定规则。 [Windows 低级键盘钩子](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc) · [Windows 按键事件标志](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-kbdllhookstruct) · [参考项目记录](https://github.com/BenjmSek/windows-remote-mic-app/blob/53fa07eb92d4332c202b5351e9b36044c88917b3/src/ovb/doubao_rpc.py#L1)

在本机，实体键盘的右 Alt 能唤起豆包。发送相同按键的 `SendInput` 和 `keybd_event` 则未能唤起，即使前台输入框确实收到了按下和松开。「全局唤起语音」让快捷键可以跨输入法生效。它不会改变 Windows 给模拟输入加上的标志。

## Remote Buddy 的发送方式

- 选中「豆包输入法」时，Remote Buddy 使用 Microsoft 签名的 `usbip-win2` 主机控制器和 HIDMaestro 的公开 USB/IP 接口创建虚拟键盘。它将整组语音快捷键转换为键盘报告，保留左右修饰键，并在会话结束时释放按住的键。单击切换模式也用同一个后端。
- 选中其他工具时，语音快捷键继续走原有 `SendInput` 路径。普通按键映射和 VB-CABLE 音频路由沿用原有实现。
- Remote Buddy 依靠输入法的全局热键，保持当前输入法。USB/IP 路径使用 Windows 已信任的驱动签名。

## 设置与验收

1. 在豆包「语音输入」设置中，把「全局唤起语音」设为「持续模式」，把麦克风设为 `CABLE Output (VB-Audio Virtual Cable)`。
2. 让豆包「长按模式」的快捷键与 Remote Buddy 一致，包括左右修饰键。Buddy 显示的 `Ctrl+Shift+D` 对应左 Ctrl + 左 Shift + D。
3. 以管理员身份运行 Remote Buddy，选中豆包和「按住说话」，按向导完成转写验收。首次使用会准备签名的 USB/IP 主机控制器。

Windows 11 独立测试通过了右 Alt 和左 Ctrl + 左 Shift + D。两组键的低级钩子日志都没有 `LLKHF_INJECTED`，豆包语音条都在按下时出现并在松开后结束。测试比较了本机和当前用户的受信任根和发布者证书列表，安装前后无差异。

### 对应源码

- [HIDMaestro v2.3.0 源码和发布包](https://github.com/hifihedgehog/HIDMaestro/releases/tag/v2.3.0)
- [usbip-win2 0.9.8.1 驱动源码和发布包](https://github.com/vadimgrn/usbip-win2/releases/tag/v.0.9.8.1)
