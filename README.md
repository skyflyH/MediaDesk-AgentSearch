# MediaDesk

MediaDesk 是一款面向 Windows 的本地照片与视频素材管理工具。通过素材队列、预览、元数据编辑和目录搜索，集中完成素材筛选、整理与归档；也可按需接入 AI，辅助生成名称、备注和执行智能搜索。

[English](README.en.md)

## 下载与安装

当前版本为 **v0.1.1**。正式发布的安装包可在本仓库的 [Releases](https://github.com/skyflyH/MediaDesk-AgentSearch/releases) 页面获取；本地调试包位于构建目录 `dist/`，不一定已上传至 Releases。普通用户无需克隆源码或安装开发工具。

1. 安装 Microsoft Edge WebView2 Runtime 和 .NET 10 Desktop Runtime x64。
2. 在 Releases 的 Assets 中下载 `MediaDesk-<版本>-win-x64.zip`。
3. 完整解压到本地目录，双击 **`MediaDesk.exe`**。无需 CMD 或 PowerShell 启动脚本，不要直接在压缩包中运行，也不要单独移走 EXE。
4. 更新时先从托盘退出旧版本，再将新包解压到新目录。默认用户数据库与模型设置保存在程序目录之外。

可下载同名 `.zip.sha256` 文件，通过 `Get-FileHash <压缩包路径> -Algorithm SHA256` 比对哈希。哈希用于核对文件完整性，不代表 Windows 发布者签名。

## 功能

- 导入文件或目录，按树形队列浏览素材，支持列表、中图、大图及类型筛选。
- 预览照片、RAW 和视频；视频播放能力受系统解码器及可选 FFmpeg 影响。
- 编辑名称、备注和创作人，自动保存草稿，支持批量处理及另存副本。
- 支持 RAW 配套 XMP 备注；不同格式的属性写入能力不同，界面会展示写入结果。
- 按文件名、目录、备注、日期和媒体属性搜索，打开目录或在资源管理器中定位原件。
- 配置兼容 OpenAI 的模型接口，提供名称、备注建议及自然语言智能搜索，支持保存多组服务商和模型配置、检测连接。
- 提供托盘、全局快捷搜索、快捷键设置、开机启动、主题、字体和背景设置。单击托盘图标可打开主界面。
- 普通文本框支持剪切、复制、粘贴快捷键；快捷键录制框保留按键录制行为。

## 支持的文件格式

以下是当前导入和目录索引实际识别的扩展名，不区分大小写。

| 类型 | 格式与扩展名 |
| --- | --- |
| JPEG | `.jpg`、`.jpeg` |
| PNG | `.png` |
| HEIF / HEIC | `.heif`、`.heic` |
| WebP | `.webp` |
| GIF | `.gif` |
| BMP | `.bmp` |
| TIFF | `.tif`、`.tiff` |
| 视频容器 | MP4 `.mp4`、QuickTime `.mov`、M4V `.m4v`、WebM `.webm`、Matroska `.mkv`、AVI `.avi` |

RAW 按厂商列举如下；这里只列出应用已接入的扩展名，并非 LibRaw 支持的全部格式。

| 厂商 / 标准 | RAW 扩展名 |
| --- | --- |
| 佳能 Canon | `.cr2`、`.cr3` |
| 尼康 Nikon | `.nef`、`.nrw` |
| 索尼 Sony | `.arw` |
| 富士 Fujifilm | `.raf` |
| 松下 Panasonic | `.rw2` |
| 奥林巴斯 Olympus / OM SYSTEM | `.orf` |
| 宾得 Pentax | `.pef` |
| Adobe Digital Negative（包括部分徕卡、理光、手机及其他设备生成的 DNG） | `.dng` |

**预览与属性能力：**

- RAW 通过 LibRaw 读取内嵌预览，必要时进行半尺寸解码。具体相机型号、RAW 压缩方式及文件完整性会影响兼容性；识别扩展名不代表所有机型均已实测。
- HEIC / HEIF、TIFF 等图片的预览取决于 Windows 图像解码器，部分系统需要安装相应图像扩展。GIF 的缩略图和完整预览表现可能不同。
- 视频扩展名表示容器，并不保证其中所有编码均可直接播放。播放能力取决于 WebView2 / 系统解码支持；无法直接播放时，可通过可选 FFmpeg 生成兼容预览代理，原视频保留不变。
- 备注、创作人和拍摄属性的读取或写入取决于文件格式、Windows 属性处理程序与可选 ExifTool。RAW 备注支持配套 `.xmp` 文件；`.xmp` 是辅助文件，不作为独立照片或视频导入。写入是否成功以界面反馈为准。

## 搜索与智能搜索

普通搜索可检索文件名、备注和目录线索；智能搜索可理解自然语言，结合名称、备注、创作人、目录及可读取属性筛选素材。支持媒体类型、格式、创建或拍摄日期、拍摄时段，以及大小、快门、光圈、ISO、焦距、曝光补偿、分辨率、视频时长、帧率和码率等条件；文件中缺失的属性不会被视为已满足条件。

智能搜索会根据实际候选逐步理解关键词关系，并扩展紧密相关词。

结果按组展示：**最匹配的素材**在上方，统一说明匹配依据；**部分关键词匹配**在下方，说明命中线索及未满足或未确认的条件。素材卡片不再逐项重复理由。模型未完成核对时，候选单独标为待核对；模型输出格式异常时最多尝试一次格式纠正，不重复本地检索，所有返回素材仍需通过本轮本地查询的来源校验。

首次索引尚未完成且未找到结果时，会提示等待索引加载完成后重搜。在素材整理中保存原件名称或备注后，会同步更新相关搜索索引，无需重启应用；仅自动保存编辑草稿不等于已写回原件。

智能搜索主要依赖文本和元数据，不会自动把本地图片上传至在线多模态模型。可选的本地照片语义检索需单独部署 Chinese-CLIP 模型，默认包不含模型权重；视频画面识别尚未提供。

## 技术结构

目录搜索索引保存在本地 SQLite 中。启动后可直接查询已有索引，后台同步不会阻塞搜索；首次添加目录或升级到持久化快照版本时需要进行一次后台校验。

在可读取 NTFS USN 日志的磁盘上，应用保存变更断点，重启后只追赶变动。日志不可用、权限受限或文件系统不支持时，自动使用目录监听，并在启动时后台比较文件快照；未变化文件复用已有属性。日志断档或监听事件溢出时会进行补偿校验，目录离线时保留索引。主程序不会自动提权，也不会创建或修改系统 USN 日志。

后台使用单个同步队列，合并重复事件，限制待处理路径数量；重叠目录共享监听，移除搜索目录后释放监听器。空闲时不反复扫描目录；同步后的新结果在下一次搜索中可见，初始同步完成时普通搜索会自动刷新一次。

缩略图按当前视图需求排队，切换视图或素材离开可见范围后取消不再需要的待处理任务，优先加载当前预览。已经进入系统解码阶段的任务可能需要完成后才能释放。

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

- `dist/MediaDesk-0.1.1-win-x64.zip`
- `dist/MediaDesk-0.1.1-win-x64.zip.sha256`

文件名版本来自 `package.json`。压缩包完成后进行完整性检查；打包使用明确的文件清单，不包含个人数据库、模型密钥、调试 profile 或 PDB。

WebView2 包请先完整解压，再双击 `MediaDesk.exe`。发布包不包含 CMD / PS1 启动脚本，开机启动也直接使用 EXE；包内其他文件仍是运行必需文件。优先选用 PATH 中可运行的 Node.js 22+，没有时尝试随包 Node；可用 `APM_WEBVIEW_NODE` 指定绝对路径。

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

- 当前版本为 `0.1.1`，支持 Windows x64，仍在持续完善。
- 照片、视频解码和元数据写入能力取决于文件格式、系统编解码器及可选工具。并非所有格式都支持相同的属性读写能力。
- 照片语义检索需要单独配置本地模型；在线 AI 功能需要可用的模型接口。
- 运行发布包只需安装运行环境；从源码构建则需要前文所列开发依赖。

## 许可

本项目目前标记为 `UNLICENSED`，尚未授予开源许可。第三方组件和模型分别受各自许可证约束。
