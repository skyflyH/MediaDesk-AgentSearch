# MediaDesk

MediaDesk 是一款面向 Windows 的本地照片与视频素材管理工具。通过素材队列、预览、元数据编辑和目录搜索，集中完成素材筛选、整理与归档；也可按需接入 AI，辅助生成名称、备注和执行智能搜索。

[English](README.en.md)

## 下载与安装

已打包版本通过本仓库的 **Releases** 页面提供。普通用户无需克隆源码或安装开发工具。

1. 安装 Microsoft Edge WebView2 Runtime 和 .NET 10 Desktop Runtime x64。
2. 在 Releases 的 Assets 中下载 `MediaDesk-<版本>-win-x64.zip`。
3. 完整解压到本地目录，运行 `Start-MediaDesk.cmd`。不要直接在压缩包中运行。
4. 更新时先从托盘退出旧版本，再将新包解压到新目录。默认用户数据库与模型设置保存在程序目录之外。

可下载同名 `.zip.sha256` 文件，通过 `Get-FileHash <压缩包路径> -Algorithm SHA256` 比对哈希。哈希用于核对文件完整性，不代表 Windows 发布者签名。

## 功能

- 导入文件或目录，按树形队列浏览素材，支持列表、中图、大图及类型筛选。
- 预览照片、RAW 和视频；视频播放能力受系统解码器及可选 FFmpeg 影响。
- 编辑名称、备注和创作人，自动保存草稿，支持批量处理及另存副本。
- 支持 RAW 配套 XMP 备注；不同格式的属性写入能力不同，界面会展示写入结果。
- 按文件名、目录、备注、日期和媒体属性搜索，打开目录或在资源管理器中定位原件。
- 配置兼容 OpenAI 的模型接口，提供名称、备注建议及智能搜索；照片语义检索需要额外部署本地 Chinese-CLIP 模型。
- 提供托盘、全局快捷搜索、快捷键设置、主题、字体和背景设置。

## 技术结构

| 部分 | 实现 |
| --- | --- |
| 界面 | Vue 3、TypeScript、Vite |
| WebView2 桌面端 | .NET 10 WinForms 宿主、系统 WebView2、Node.js 业务桥接 |
| 本地服务 | C# / .NET 10，通过 JSON Lines 与桌面进程通信 |
| 数据与媒体 | SQLite / FTS5、LibRaw、Windows 图像及属性接口、ONNX Runtime |
| AI 调用 | LangChain、兼容 OpenAI 的接口 |

## 环境要求

开发及构建需要 Windows x64、Node.js 22 或以上版本（推荐 22 LTS）、npm 和 .NET 10 SDK。首次安装需要访问 npm 和 NuGet。

运行需要 Microsoft Edge WebView2 Runtime 和 .NET 10 Desktop Runtime x64。构建前还需准备下文的 WebView2 SDK 文件。

可选组件：

- FFmpeg：生成兼容预览代理，通过 `APM_FFMPEG` 指定可执行文件或放入 `PATH`。
- ExifTool：部分格式的嵌入式元数据读写，通过 `APM_EXIFTOOL` 指定可执行文件或放入 `PATH`。
- Chinese-CLIP ONNX 模型：用于照片语义检索，通过 `APM_PHOTO_MODEL_DIR` 指定模型目录。模型权重不包含在仓库中。

## 开发运行

在项目根目录执行：

```powershell
$env:ELECTRON_SKIP_BINARY_DOWNLOAD = "1"
npm ci
npm run dev
```

`npm run dev` 会构建服务、Vue 页面和原生宿主，再启动 WebView2 应用；需要先准备 WebView2 SDK。此入口不提供热重载，修改后需退出并重新执行。

如仅需检查前端构建，可执行 `npm run build:renderer`。项目暂保留 Electron 相关开发依赖及配置，用于共享业务 API 类型和历史兼容性回归测试；上面的环境变量可跳过 Electron 运行时下载。发布包不包含 Electron 或独立 Chromium。

## 检查与测试

```powershell
npm run typecheck
npm run test:desktop
npm run test:service
```

原生窗口、文件选择、托盘、全局焦点和资源管理器行为需要在可交互的 Windows 桌面验证，不能以单元测试通过替代。

如果执行环境无法启动默认 testhost，可显式指定系统 dotnet 路径，例如：

```powershell
dotnet test tests/MediaLibrary.Service.Tests/MediaLibrary.Service.Tests.csproj -- RunConfiguration.DotNetHostPath="C:\Program Files\dotnet\dotnet.exe"
```

部分服务测试依赖外部夹具，没有提供时会跳过：`APM_TEST_PHOTO_MODEL` 为导出的模型及对照数据目录，`APM_TEST_SECOND_VOLUME` 为第二磁盘卷上的可写测试目录，`APM_TEST_FORMATS` 为媒体格式夹具目录。

## 构建与打包

WebView2 使用外置 SDK 文件构建。准备 Microsoft WebView2 SDK（当前使用版本 `1.0.2535.41`），在 `artifacts/webview-sdk/` 中放置：

```text
Microsoft.Web.WebView2.Core.dll
Microsoft.Web.WebView2.WinForms.dll
WebView2Loader.dll                 # x64
```

这些文件可从对应版本的 Microsoft.Web.WebView2 NuGet 包提取，SDK 和模型等大型依赖不随源码提交。原生项目也支持 MSBuild 属性 `WebViewSdkDirectory` 指定其他 SDK 路径。

安装 7-Zip（脚本默认路径为 `C:\Program Files\7-Zip\7z.exe`，或通过 `SEVENZIP_PATH` 指定），然后执行：

```powershell
npm run build
npm run package:win
```

`npm run build` 构建服务、页面与 WebView2 宿主，并在 `artifacts/webview-app/` 中组装可运行目录。`npm run package:win` 会重新构建并生成 WebView2 ZIP，`npm run package:webview` 是相同入口：

- `dist/MediaDesk-0.1.0-win-x64.zip`

文件名版本来自 `package.json`。压缩包完成后进行完整性检查；打包使用明确的文件清单，不包含个人数据库、模型密钥、调试 profile 或 PDB。

WebView2 包请先完整解压，再运行 `Start-MediaDesk.cmd` 或 `MediaDesk.exe`。启动脚本通过系统 dotnet 启动宿主。优先选用 PATH 中可运行的 Node.js 22+，没有时尝试随包 Node；可用 `APM_WEBVIEW_NODE` 指定绝对路径。

## 数据与模型配置

数据库、服务缓存和模型配置默认存于 `%LOCALAPPDATA%/ai-photos-manager`。Windows 模型密钥由当前用户的 DPAPI 加密，界面偏好存于 WebView2 用户配置。关闭窗口通常收起到托盘，完整退出请使用应用或托盘的退出入口。

模型接口地址、模型名称、密钥及数据使用范围在应用设置中配置。常规整理和筛选可离线使用；启用模型功能后，所选范围内的内容可能发送给配置的模型服务，请按实际需要设置范围。项目不提供在线模型账号或密钥。

保留的 `scripts/export-photo-model.py` 用于把固定版本的 Chinese-CLIP 检查点导出为 ONNX，并核验数值一致性；需要单独准备 Python、PyTorch、Transformers、ONNX、ONNX Runtime、NumPy 和 Pillow，以及脚本指定的本地检查点。它不是普通启动依赖。

## 项目结构

```text
apps/webview/  Windows 桌面宿主与页面、Node.js 桥接
apps/desktop/  共用界面、业务逻辑、IPC 类型及回归测试
src/           .NET 本地服务
tests/         服务测试与夹具
resources/     图标和品牌资源
scripts/       构建、启动、打包及模型导出工具
```

## 问题反馈

欢迎通过 GitHub Issues 报告问题或提出建议。报告问题时，请提供 MediaDesk 版本、Windows 版本、复现步骤以及相关错误提示；媒体兼容性问题请注明文件格式。分享日志前请移除 API 密钥和个人路径等敏感信息。

## 兼容性

- 当前版本为 `0.1.0`，支持 Windows x64，仍在持续完善。
- 照片、视频解码和元数据写入能力取决于文件格式、系统编解码器及可选工具。并非所有格式都支持相同的属性读写能力。
- 照片语义检索需要单独配置本地模型；在线 AI 功能需要可用的模型接口。
- 运行发布包只需安装运行环境；从源码构建则需要前文所列开发依赖。

## 许可

本项目目前标记为 `UNLICENSED`，尚未授予开源许可。第三方组件和模型分别受各自许可证约束。
