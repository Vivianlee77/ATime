# Windows 采集层

目标为 Windows 11 x64，普通用户、当前本地交互会话。该项目使用 WinForms 的隐藏消息窗口与 STA 消息循环，不依赖 WPF，不显示窗口，也不会改变锁屏、显示电源或用户输入。

`WindowsSignalSource.Start()` 异步开始；重复调用不创建第二个采集线程。`Observation` 在采集线程触发，调用方只应入队，再在自己的线程更新业务/UI；`Latest` 为不可变最新观察。`Dispose()` 注销通知并停止采集。`LastError` 为不含路径、标题或输入内容的诊断代码。

状态原因包括 `WaitingForDisplay`、`DisplayOff`、`Locked`、`Disconnected`、`UnsupportedSession`、`SessionUnknown`、`InputUnknown`、`Suspended`、`CollectorError`。没有收到可信显示通知前不会假设显示开启。`WindowsProbe.ReadOnce()` 可诊断前台、输入和会话；它无法凭一次读取获得显示状态，因此始终 `Eligible=false`，正常会话时原因为 `WaitingForDisplay`。

时钟为 `GetTickCount64` 自启动毫秒，输入 tick 映射到相同时间轴，`UnbiasedMs` 不包含睡眠/休眠。调用方必须检测观察缺口，不可将两个观察之间的长中断累计给旧应用。前台事件被投递到消息队列后读取当前前台，极短切换可能合并；另有 1 秒采样校正，不声明硬实时。

输入观察时间在 `GetLastInputInfo` 返回后读取，避免 API 调用之间发生的输入被无符号减法误判为超长空闲。纯函数 `InputTickConverter.TryMap` 允许最多32ms计时器量化差，较大未来时间戳拒绝；首次采样无法区分超长无输入与异常tick时保持受限，持续运行时相同原始tick沿用已建立映射。`Verify-InputTicks.ps1` 可运行9项普通、回绕、量化边界和异常输入检查，不产生任何系统输入。

应用身份通过前台 PID、进程创建时间与规范化路径确定；只缓存哈希、展示名及可执行文件名，不保存完整路径。可取得 AUMID 时使用 AUMID 哈希。缓存最多256项；每次复核进程创建时间避免 PID 复用。显示名采用常见应用映射，其余回退到 exe 名；不读取窗口标题。`ApplicationFrameHost` 无法可靠确定目标应用时返回未知桶。

显示通知、WTS 状态查询和前台 Hook 均可能受系统环境限制。Hook 注册失败保留轮询；显示或会话状态不可信时停止有效采集。RDP 和其他远程协议不计时。没有执行任何锁屏、睡眠、显示关闭等主动系统测试。
