# 官方参考资料与设计决策

核对日期：2026-11-12。下列链接用于实现核验；版本和支持范围在正式开发及发布时还需复查。API能力来自官方文档，默认阈值、误差预算、检查点频率与MVP范围是本项目的设计决策。

## 平台与数据访问

| 资料 | 本项目采用的事实 |
| --- | --- |
| [.NET 支持政策](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core) | .NET 10为LTS，采用受支持修订版；不从旧教程直接复制已过支持期版本 |
| [.NET 10 支持操作系统](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md) | 支持依OS具体版本与架构而定；不能泛称所有Windows10都受支持 |
| [WPF 概览](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/overview/) | WPF提供Windows桌面UI、XAML与数据绑定 |
| [Microsoft.Data.Sqlite](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/) | 可独立使用的轻量ADO.NET SQLite提供程序 |
| [Microsoft.Data.Sqlite 异步限制](https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/async) | 异步ADO.NET方法仍同步执行，不应在UI线程用方法名判断是否阻塞 |
| [SQLite WAL](https://sqlite.org/wal.html) | 读写并发、检查点和同机文件要求；活跃WAL属于数据库状态 |
| [WAL-reset 修复](https://sqlite.org/wal.html#the_wal_reset_bug) | 选择包含修复的原生SQLite版本并验证实际加载版本 |
| [SQLite 在线备份](https://www.sqlite.org/backup.html) | 在写入环境下建立一致备份使用官方备份机制 |

## 前台与身份

| 资料 | 实现注意点 |
| --- | --- |
| [SetWinEventHook](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwineventhook) | 前台事件、进程外回调、消息循环、回调生命周期与重入 |
| [GetForegroundWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getforegroundwindow) | 前台句柄可能为空，必须有未知路径 |
| [GetWindowThreadProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getwindowthreadprocessid) | 从窗口获得进程身份入口 |
| [QueryFullProcessImageNameW](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-queryfullprocessimagenamew) | 查询进程映像名称的权限与参数 |
| [GetApplicationUserModelId](https://learn.microsoft.com/en-us/windows/win32/api/appmodel/nf-appmodel-getapplicationusermodelid) | 打包应用身份辅助信息；失败需处理 |

## 空闲、会话、电源与时钟

| 资料 | 实现注意点 |
| --- | --- |
| [GetLastInputInfo](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getlastinputinfo) | 当前会话输入时间，返回tick非严格单调 |
| [LASTINPUTINFO](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-lastinputinfo) | dwTime为DWORD，需处理回绕 |
| [WTSRegisterSessionNotification](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsregistersessionnotification) | 订阅会话状态变化 |
| [WM_WTSSESSION_CHANGE](https://learn.microsoft.com/en-us/windows/win32/termserv/wm-wtssession-change) | 锁定、解锁、连接和断开的事件区分 |
| [Power Setting GUIDs](https://learn.microsoft.com/en-us/windows/win32/power/power-setting-guids) | 交互式用户进程使用GUID_SESSION_DISPLAY_STATUS；off/on/dim语义 |
| [System Power Management Events](https://learn.microsoft.com/en-us/windows/win32/power/system-power-management-events) | 挂起/恢复通知及临界挂起可能无预先通知的限制 |
| [高精度时间戳](https://learn.microsoft.com/en-us/windows/win32/sysinfo/acquiring-high-resolution-time-stamps) | QPC与Stopwatch用于持续时长 |
| [QueryUnbiasedInterruptTime](https://learn.microsoft.com/en-us/windows/win32/api/realtimeapiset/nf-realtimeapiset-queryunbiasedinterrupttime) | 时间不包含睡眠/休眠，作为恢复/中断检测辅助 |

## 窗口生命周期

| 资料 | 实现注意点 |
| --- | --- |
| [Application.ShutdownMode](https://learn.microsoft.com/en-us/dotnet/api/system.windows.application.shutdownmode?view=windowsdesktop-10.0) | 显式退出用于关闭窗口后保留托盘进程 |
| [NotifyIcon](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon?view=windowsdesktop-10.0) | 系统通知区图标和菜单 |

## 用户项目上下文

[Vivianlee77 GitHub 主页](https://github.com/Vivianlee77) 用于确认账号和未来仓库归属建议。本次未改动该账号仓库；技术选型来自本产品对Windows原生行为的要求，不推断作者的技术熟练度。

## 需要在技术验证中确认的事项

官方接口存在不代表组合方案已被验证。M0必须确认显示状态初值、特定打包应用宿主识别、通知在本机休眠/恢复上的顺序、同用户多会话排他锁及普通账户访问限制。通过真实结果确定支持边界，再实现完整功能。
