# 第三方组件说明

NewScope 使用以下第三方项目。具体版本以项目文件、`global.json` 和构建还原结果为准；各组件的许可证及版权声明由其上游提供。

| 组件 | 用途 | 上游 |
|---|---|---|
| .NET / Windows Desktop 相关运行组件 | 自包含运行时和基础库 | https://github.com/dotnet/runtime |
| Avalonia、Fluent、Desktop、DataGrid | 桌面 UI、控件和样式 | https://github.com/AvaloniaUI/Avalonia |
| Avalonia.Fonts.Inter / Inter | 随附字体资源 | https://github.com/rsms/inter |
| CommunityToolkit.Mvvm | MVVM 属性与命令生成 | https://github.com/CommunityToolkit/dotnet |
| SkiaSharp、HarfBuzzSharp 及原生依赖 | 图形、字体整形与渲染 | https://github.com/mono/SkiaSharp |
| xUnit、Microsoft.NET.Test.Sdk、coverlet | 仅开发/测试 | https://github.com/xunit/xunit |
| NSIS | 安装包生成 | https://nsis.sourceforge.io/ |
| pyelftools、Pillow | 可选 Python 开发辅助脚本 | https://github.com/eliben/pyelftools / https://python-pillow.github.io/ |

NSIS 许可证副本位于 [`packaging/NSIS.COPYING.txt`](packaging/NSIS.COPYING.txt)，也随程序包提供。

SEGGER J-Link 软件及 `JLink_x64.dll` 为外部依赖，由用户从 [SEGGER 官网](https://www.segger.com/downloads/jlink/) 获取，不包含在 NewScope 安装包或仓库中。使用时遵守 SEGGER 的许可条款。

Segoe UI、Cascadia Mono、Consolas 等系统字体仅通过字体名称引用。开发时可选的诊断工具按其 NuGet 包许可使用，Release 不包含调试诊断支持资产。

本文件仅说明第三方来源，不为 NewScope 自身代码选择开源许可证；项目自身暂未指定额外许可证。
