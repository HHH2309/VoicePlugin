# Voice 语音播报插件

[InkCanvasForClass](https://github.com/InkCanvasForClass/community) (社区版) 的语音播报与音效增强插件：随机抽选 / 快抽完成后自动朗读被抽中的姓名或学号，支持多 TTS 引擎、自定义播报模板、提示音、音频缓存与课堂流程自动化。

## 功能特性

- **自动播报**：深度监听主程序的随机抽选 / 快抽结果（经宿主抽选历史文件监控实现），抽中即播。
- **多 TTS 引擎**：
  - **Windows SAPI**（本地，默认，稳定可靠）
  - **Windows Media**（本地，`Windows.Media.SpeechSynthesis`；未打包宿主可能因缺少应用包身份不可用）
  - **Edge 在线语音**（非官方接口，文本会上传微软非公开服务；支持超时与自动降级到本地引擎）
  - 引擎失败自动降级，可在设置页查看各引擎可用状态。
- **打断与抢占**：连续触发抽选时自动截断当前语音与音效，立即播报最新内容，绝不对列。
- **播报模板**：`{name}` 占位符（如 `请 {name} 同学回答`）；支持播报延迟、学号逐位朗读（`2024` → `2 0 2 4`）。
- **自定义读音**：多音字 / 生僻字字面量替换规则，支持拖拽排序，最多 100 条。
- **提示音（SFX）**：语音朗读前后的前奏音 / 后置音，支持 WAV / MP3 / FLAC，受全局打断机制控制。
- **多入口截断**：白板工具栏与浮动工具栏「播报截断」按钮（单击截断）、「更多 / 工具」菜单项、全局热键（在宿主「快捷键设置」页内配置）。
- **系统托盘**：托盘右键菜单「自动播报」开关。
- **课堂自动化集成**：向宿主自动化引擎注册 4 个行动（切换引擎 / 音色、调整语速、调整播报模板、开启 / 关闭自动播报）与 2 条规则（TTS 播报中、当前 TTS 引擎）。
- **播报音频缓存**：Edge / WinRT 引擎合成音频按 (引擎, 发音人, 语速, 音量, 文本) 缓存，重复播报秒开；支持按容量 LRU / 按保留天数 / 不限制三种策略。
- **预缓存**：启动或手动预合成名单内容，切换名单自动清空旧缓存（按名单指纹检测）。
- **随机发音人**：每次播报从中文发音人中随机挑选。
- **无缝设置**：设置面板嵌入宿主「应用设置 → 插件设置」，修改实时自动保存并热生效。

## 安装

1. 从 [Releases](../../releases) 下载 `VoicePlugin-<版本>.icpx`，或将 `VoicePlugin-deploy` 目录中的 4 个文件（`VoicePlugin.dll`、`EdgeTTS.DotNet.dll`、`VoicePlugin.deps.json`、`manifest.json`）放入宿主的插件目录（`Plugins` 文件夹）。
2. 在宿主「应用设置 → 插件设置」中确认「语音播报」已启用。

> 要求宿主版本 ≥ 1.7.18。

## 构建

### 环境要求

- Windows 10 1809+（.NET 10 目标）
- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- 宿主源码检出（提供 `InkCanvas.PluginSdk` 与 `InkCanvas.Controls` 的构建产物）

### 本地构建

```powershell
# 1. 检出宿主源码（默认路径：本仓库旁 community-net10/community-net10）
git clone https://github.com/InkCanvasForClass/community community-net10

# 2. 构建宿主 SDK 与控件库（Release）
dotnet build community-net10\community-net10\InkCanvas.PluginSdk\InkCanvas.PluginSdk.csproj -c Release
dotnet build community-net10\community-net10\InkCanvas.Controls\InkCanvas.Controls.csproj -c Release

# 3. 构建插件（自动产出 icpx\VoicePlugin.icpx 插件包）
dotnet build VoicePlugin.csproj -c Release
```

若宿主检出在其他路径，用 `-p:PluginSdkRoot=<宿主仓库根>` 覆盖引用位置（CI 中即按此方式注入）。

### 产物

- `icpx\VoicePlugin.icpx` —— 分发用插件包
- `bin\Release\net10.0-windows10.0.19041.0\` —— 解包部署集（dll + deps + manifest）

## 依赖

| 依赖 | 用途 | 说明 |
|---|---|---|
| [EdgeTTS.DotNet](https://www.nuget.org/packages/EdgeTTS.DotNet) 0.4.0 | Edge 在线 TTS | 随插件分发（不依赖宿主） |
| Microsoft.Extensions.DependencyInjection 6.0.0 | 宿主服务注入 | 编译期引用，宿主内嵌 |
| InkCanvas.PluginSdk / InkCanvas.Controls | 宿主 SDK 与控件 | 引用宿主构建产物，不打进插件包 |

## 目录结构

```
VoicePlugin/
├── VoicePlugin.cs            # 插件入口：初始化、配置、抽选监听（历史监控）
├── AutomationActionComponent.cs  # 自动化行动与规则注册（反射注入宿主）
├── HotkeyComponent.cs        # 全局热键（宿主快捷键页注入）
├── MoreMenuComponent.cs      # 「更多/工具」菜单按钮注入
├── TrayMenuComponent.cs      # 托盘菜单开关注入
├── Config/                   # 配置模型、快照、JSON 持久化（原子写 + 迁移）
├── Services/                 # TTS 引擎、播报队列、音频缓存、预缓存
└── Views/                    # 嵌入式设置页（XAML + 代码）
```

## 许可

[GPL-3.0](LICENSE)
