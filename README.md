# 屏幕时光 Screen Time Companion

面向 Windows 11 x64 的个人屏幕时间工具。**v0.1.0 核心 MVP 已实现**：有效使用时长、各应用前台用时、桌面小窗、本地保存与托盘常驻。

默认连续 5 分钟无键鼠输入后停止累计有效使用，前 5 分钟保留。后台进程不重复计时；锁屏、显示关闭、休眠及暂停期间不计入。另提供“屏幕会话”口径，方便参考阅读和视频场景。

## 运行

解压生成的 `artifacts/ScreenTimeCompanion-v0.1.0-win-x64.zip`，保留完整目录，双击 `ScreenTimeCompanion.exe`。自包含包无需安装 .NET SDK。详细操作、数据位置与限制见 [MVP 使用说明](docs/08-running-mvp.md)。

- 小窗：今日时分秒、前三应用、拖动、置顶、暂停、查看详情。
- 详情：今日 / 近 7 日 / 近 30 日、应用排行与占比、两种统计口径、阈值设置、CSV 导出与清空历史。
- 关闭窗口只隐藏；从托盘恢复或退出。主动暂停跨重启保留。
- SQLite 保存于 `%LOCALAPPDATA%\ScreenTimeCompanion\usage.db`；不记录窗口标题、按键内容、网址、剪贴板或截图，无后台联网。

这是先交付核心三项能力的 MVP。完整设计中的登录自启、隐私应用规则、深色主题、趋势图、安装器与自动升级尚未实现。原型不是运行程序，原始设计文档记录了后续目标范围。

## 构建与测试

C#、.NET 10、WPF、Microsoft.Data.Sqlite；SDK与NuGet依赖锁定在仓库文件中。

```powershell
./scripts/build.ps1 -Publish
```

脚本还原、编译、运行测试并生成自包含 Windows x64 ZIP。工程分为 Core、Windows、Storage 与 App；界面不直接累计时间。推送和 Pull Request 会触发 [GitHub Actions](https://github.com/Vivianlee77/screen-time-companion/actions)；成功运行后可在对应任务的 Artifacts 中下载 Windows 运行包。

本轮验证：**30 项计时测试、14 项真实 SQLite 集成测试、9 项输入 tick 转换检查通过**；真实 Windows 采集及控制器暂停/恢复/重启/导出/清除验证通过。具体证据与未覆盖项见 [MVP 验证记录](docs/09-mvp-validation.md)。未进行主动锁屏/休眠、多显示器全矩阵及 24 小时稳定性验收。

## 文档与设计

| 文档 | 内容 |
| --- | --- |
| [产品需求](docs/01-product-requirements.md) | 完整目标范围与统计口径 |
| [界面交互](docs/02-ui-design.md) | 小窗、托盘、详情和异常状态规格 |
| [技术架构](docs/03-technical-architecture.md) | 原生 API、分层、运行生命周期 |
| [计时与数据](docs/04-accounting-and-data.md) | 状态机、跨日、持久化与对账规则 |
| [开发计划](docs/05-development-plan.md) | 完整路线图；不代表所有任务已完成 |
| [测试计划](docs/06-testing-and-acceptance.md) | 完整版本的测试与发布门槛 |
| [官方参考](docs/07-references.md) | 平台与 API 文档 |
| [MVP 使用说明](docs/08-running-mvp.md) | 当前可用功能、操作及限制 |
| [MVP 验证记录](docs/09-mvp-validation.md) | 实际运行与测试结果 |
| [交互设计原型](prototype/index.html) | 原设计 HTML 演示，使用示例数据 |

项目仓库：[Vivianlee77/screen-time-companion](https://github.com/Vivianlee77/screen-time-companion)。作者主页：[Vivianlee77](https://github.com/Vivianlee77)。使用记录、预览浏览器缓存和本地构建产物不提交到仓库。开源许可证由作者后续选择。
