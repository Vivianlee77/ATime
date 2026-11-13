# ATime 桌面图标

设计：墨绿底座、暖白色时钟、香槟金时间刻度。圆角与透明边缘适用于明暗桌面；粗线条保留小尺寸下的识别度。

- `atime-icon-v1.png`：透明背景的图标原稿。
- `atime-icon-v1.ico`：Windows 多尺寸图标，包含 16、20、24、32、40、48、64、128、256 像素版本。
- `prompt-v1.txt`：生成提示词。原稿使用内置 imagegen 生成，ICO 仅作尺寸与格式转换。

重新生成 ICO：在 Windows PowerShell 中执行 `./scripts/New-WindowsIcon.ps1 -SourcePath ./assets/branding/atime-icon-v1.png -OutputPath ./assets/branding/atime-icon-v2.ico`。

同一份 ICO 用于桌面快捷方式、`ATime.exe` 的原生图标、WPF 窗口、小窗和统计页标识、系统托盘。

`ATime.App.csproj` 通过 `ApplicationIcon` 编译 EXE 图标，并将 ICO 作为内嵌资源打包。`Branding.cs` 从程序集加载图标，不依赖运行目录外的图片路径。WPF 图像加载后冻结复用；托盘使用独立的 `Icon`，在退出时释放。

PNG 原稿供文档与后续设计使用，运行包无需单独携带 PNG 或 ICO 文件。
