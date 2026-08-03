# 语音播报

ICC-CE 插件，用于随机抽选 / 快抽完成后自动语音播报被抽中的姓名或学号，支持多 TTS 引擎、自定义播报模板、提示音与课堂流程自动化。

**本插件代码含有大量VibeCoding成分，介意请无视该项目**

## 功能

- 抽选自动播报：随机抽选 / 快抽完成后自动朗读结果，连续抽选自动截断当前播报，立即播报最新内容。
- 多 TTS 引擎，支持热切换与自动降级：
  - **Windows SAPI**（本地，默认，稳定可靠）
  - **Windows Media**（本地，`Windows.Media.SpeechSynthesis`；未打包宿主可能因缺少应用包身份不可用）
  - **Edge 在线语音**（非官方接口，支持超时设置与失败自动降级到本地引擎）
- 播报模板：使用 `{name}` 占位符（如 `请 {name} 同学回答`）。
- 播报延迟：抽选后延迟指定毫秒再播报。
- 学号逐位朗读：纯数字内容按单个数字逐字朗读（`2024` → `2 0 2 4`）。
- 自定义读音：多音字 / 生僻字字面量替换规则，支持拖拽排序，最多 100 条。
- 提示音：语音朗读前的前奏音与朗读后的后置音，支持 WAV / MP3 / FLAC，受全局打断机制控制。
- 多入口截断：白板工具栏与浮动工具栏「播报截断」按钮、「更多 / 工具」菜单项、全局热键（在宿主「快捷键设置」页内配置组合键）。
- 系统托盘：托盘右键菜单提供「自动播报」开关。
- 课堂自动化：向宿主自动化引擎注册 4 个行动（切换引擎 / 音色、调整语速、调整播报模板、开启 / 关闭自动播报）与 2 条规则（TTS 播报中、当前 TTS 引擎）。
- 播报音频缓存：按引擎、发音人、语速、音量、文本缓存已合成音频，重复播报秒开；支持按容量 LRU / 按保留天数 / 不限制三种策略。
- 预缓存：启动时或手动按名单范围预合成播报音频；切换名单后自动清空旧缓存（按名单指纹检测）。
- 随机发音人：每次播报从中文发音人中随机挑选。
- 设置自动保存：设置页修改实时保存并热生效。

## 使用方法

1. 在 ICC-CE 插件管理器中安装插件，或下载 Release 中的 `.icpx` 文件并放入 `PluginPackages` 目录，重启 ICC-CE。
2. 完成一次随机抽选或快抽，插件会自动朗读结果。
3. 打开 ICC-CE 的插件设置页：
   - 选择播报引擎与发音人，调整语速、音量；在线引擎可设置超时与降级开关。
   - 设置播报模板、播报延迟、学号逐位朗读。
   - 按需添加自定义读音规则。
   - 为朗读前后的提示音选择音频文件（WAV / MP3 / FLAC）。
   - 按需配置播报音频缓存与预缓存范围。
4. 播报进行中，可通过白板 / 浮动工具栏的「播报截断」按钮、菜单项或全局热键立即停止。
5. 在宿主「快捷键设置」页中可配置「截断播报」全局热键的组合键。
6. 课堂自动化：在宿主自动化工作流中添加插件的行动与规则，实现按课堂流程控制播报。

## 配置文件

插件配置保存在 ICC-CE 的插件配置目录中：

```text
PluginConfigs/com.icc.voice/voice_config.json
```

配置包括引擎、发音人、播报模板、提示音、缓存策略与预缓存设置。修改配置文件前建议先关闭 ICC-CE，并自行备份文件。

播报音频缓存与预缓存名单指纹保存在同一目录的 `AudioCache` 与 `.roster_fingerprint.txt` 中。

## 常见问题

### 引擎显示“当前不可用”

- **Windows Media**：宿主为未打包程序时，系统 API 要求应用包身份，通常无法使用，请改用 SAPI 或 Edge 在线语音。
- **Edge 在线语音**：非官方接口，可能随时间失效；失效时按设置自动降级到本地引擎，不影响播报。

### 中文播报用的是默认发音人

在设置页的发音人列表中选择具体的中文发音人；「随机」模式会在每次播报时从中文发音人中随机挑选。

### 预缓存没有生效

预缓存仅对支持音频缓存的引擎（Edge / WinRT）有效；「随机发音人」模式下每次解析出的发音人不同，缓存无法命中，预缓存会自动跳过。

### 播报被截断时没有通知

在设置页开启「截断时通知」后，点击截断按钮或菜单项时会显示应用内通知。

## 权限

插件不声明额外权限（`Permissions: []`），所有功能均通过宿主插件 SDK 接口与系统 API 实现。

## 构建

需要 Windows 和 .NET 6 SDK，以及宿主源码检出（用于构建 `InkCanvas.PluginSdk` 与 `InkCanvas.Controls`）：

```powershell
git clone --branch 1.7.19.9 https://github.com/InkCanvasForClass/community community-1.7.19.9

# 本分支面向宿主 v1.7.19.9（TFM net6.0-windows，SDK 无 INameRosterService /
# IPluginUriService / RegisterBoardToolbarItem，插件已按该 SDK 降级：
# URI 路由与白板工具栏不可用，名单读取走 Names.txt 文件直读）；
# EdgeTTS 在线引擎在 net6 下未打包（EdgeTTS.DotNet 无 net6 目标），
# 播报自动使用 SAPI/WinRT 本地引擎。
dotnet build community-1.7.19.9\InkCanvas.PluginSdk\InkCanvas.PluginSdk.csproj -c Release -p:TargetFramework=net6.0-windows10.0.19041.0
dotnet build community-1.7.19.9\InkCanvas.Controls\InkCanvas.Controls.csproj -c Release -p:TargetFramework=net6.0-windows10.0.19041.0

dotnet build VoicePlugin.csproj -c Release
```

宿主检出在其他路径时，用 `-p:PluginSdkRoot=<宿主仓库根>` 覆盖引用位置。

GitHub Actions 支持推送 `v*` 标签或手动运行工作流，并自动构建、打包和发布 `.icpx` 文件。

## 发布包安装

从 GitHub Release 下载：

```text
com.icc.voice.icpx
```

将文件复制到：

```text
PluginPackages/
```

然后重启 ICC-CE，插件管理器会自动处理安装。
