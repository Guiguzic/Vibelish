# 输入翻译

Windows 托盘小工具：在当前输入框里输入中文，按 **考研英语** 的难度翻成英文，并在输入框旁边显示一个不抢焦点的卡片。

![图标](assets/preview.png)

## 安装

双击 `installer\InputTranslate-Setup-1.1.0.exe`：

- 安装到当前用户目录 `%LOCALAPPDATA%\Programs\InputTranslate`，**不需要管理员权限**，也**不需要另外装 .NET**（安装程序用 Windows 10/11 自带的 .NET Framework 4.8，装进去的主程序是自包含的）。
- 已经装过会显示「更新」，沿用原来的开机启动和桌面快捷方式选择；设置和 API Key 都会保留。正在运行的输入翻译会先被关掉。
- 装完显示用法和实际热键，可以选择立即启动。
- 静默安装：`InputTranslate-Setup-1.1.0.exe --silent`（或 `/S`），默认开机自动启动、不建桌面快捷方式；失败原因写在 `%TEMP%\InputTranslate-Setup.log`。
- 卸载：设置 → 应用 → 输入翻译，或开始菜单里的「卸载输入翻译」，也可以运行 `InputTranslate.exe --uninstall`（加 `--yes` 不弹确认）。

卸载会删除程序文件、开始菜单/桌面快捷方式和开机启动项，`%APPDATA%\InputTranslate` 里的设置会保留。只有确认是安装目录时才删程序文件夹；从别处运行的那一份只清理快捷方式和启动项，不会动所在文件夹。

## 使用

1. 运行后托盘出现图标，屏幕上方短暂显示提示卡片（约 9 秒后自动隐藏）。
2. 按热键 **Ctrl+Alt+T** 开始（设置里可以改）。如果被其他程序占用，会自动改用 `Ctrl+Alt+E`、`Ctrl+Alt+Q`、`Ctrl+Shift+F9` 或 `Ctrl+Alt+G`；实际生效的组合显示在卡片和托盘菜单里，只有热键出问题时才弹托盘气泡。
3. 在任意输入框里输入中文。停顿约 0.6 秒（设置里可选 0.35 / 0.6 / 1 秒）就开始翻译，打出句号、逗号会立刻翻译。用大模型时译文逐字显示，第一个词通常一秒内出来。改了中文以后，旧译文会变暗，直到新译文出来。
4. 按 **Enter** 完成：还没翻完的会立刻翻完。**Shift+Enter** 照常换行，**Esc** 取消并收起卡片。再按一次热键也能结束。可以设置完成后几秒自动收起卡片。
5. 点英文里的单词可以查释义。带「本句」的是这个词在当前句子里的意思，也可以改选别的义项，再按那个意思改一句，觉得合适就「采用这句」。再点同一个词，或点「收起」，就关掉释义。
6. 配置了大模型接口时，译文下面会列出几个「重点词」，和译文在同一次请求里生成，不用多等。显示方式可选「仅单词」「带释义」（默认）「详细」（多一列为什么值得记）。
7. 卡片上的「写入输入框」「复制」和朗读按钮可以直接用（朗读用 Windows 自带的英文语音）；翻译失败时有「重试」。右上角是设置和关闭（×，翻译中按它等于 Esc）。卡片可以拖动，拖过以后就停在那里。

托盘图标：单击显示/隐藏卡片，双击开始/结束；正在听写时图标带绿点。右键菜单：开始翻译 / 显示卡片 / 最近翻译（最近 8 条，点一下显示并复制）/ 设置… / 启用热键 / 开机自动启动 / 退出。

再次双击 exe 不会启动第二份，而是让已在运行的那份把卡片显示出来。

## 设置

右键托盘图标 → 设置…，或直接编辑 `%APPDATA%\InputTranslate\config.json`。设置窗口左边分六页，改了立刻生效，不用点保存：

- **通用**：全局热键开关、录新热键（Backspace 恢复默认）、开机自动启动。
- **翻译**：翻译引擎、译文难度（考研 / 六级 / 日常）、开始翻译的快慢、只翻含中文的内容、译完自动复制 / 自动写入。「自动」配置了大模型接口就用它，没配置或出错时改用免费引擎：先问腾讯交互翻译（国内一般 0.3–0.7 秒），1.2 秒还没回来就同时问 MyMemory 和 Google，谁先到用谁。
- **大模型**：任意 OpenAI 兼容接口。内置 DeepSeek、通义千问、Kimi、智谱 GLM、硅基流动、OpenAI、Ollama 的地址，选好以后填 API Key 和模型名，「测试连接」会显示是否可用以及第一个词和整句各用了多久（401、404、429 会给出具体原因）。本机或局域网的接口（如 Ollama）可以不填 Key。这一页还有「逐字显示译文」和「关闭深度思考」。
- **卡片与外观**：主题（跟随系统 / 浅色 / 深色）、强调色（蓝、紫、粉、橙、绿、石墨）、文字大小、卡片位置（跟着输入框 / 屏幕顶部 / 屏幕底部）、是否显示中文原文 / 操作提示 / 译文来源、完成后自动收起。
- **单词与朗读**：是否显示重点词及显示方式、点英文单词查释义、朗读按钮、查词时自动朗读、语速。没有英文语音时会提示去 Windows 设置 → 时间和语言 → 语音 里添加。
- **关于**：版本、打开配置文件夹、查看日志。

API Key 用 Windows DPAPI 按当前用户加密，`config.json` 里不出现明文。手动填明文也能读，下次保存时会自动加密。

| 键 | 说明 |
|---|---|
| `Engine` | `auto`（默认）、`openai`、`transmart`（腾讯交互翻译）、`mymemory`、`google` |
| `EnglishLevel` | `kaoyan`（考研英语，默认）、`cet6`（六级）、`plain`（日常）；只对大模型接口生效 |
| `OpenAiBaseUrl` / `OpenAiModel` | OpenAI 兼容接口的地址和模型；地址末尾带不带 `/chat/completions` 都可以 |
| `OpenAiApiKeyProtected` | 加密后的 API Key（在设置里填，不要手改） |
| `StreamOutput` / `FastThinking` | 逐字显示译文 / 关闭深度思考（默认都开） |
| `Hotkey` | 例如 `Ctrl+Alt+T`，至少要带 Ctrl、Alt 或 Win |
| `PauseMs` | 输完停顿多久开始翻译（200–3000，默认 600） |
| `ChineseOnly` | 只翻译含中文的内容 |
| `AutoCopy` / `AutoWrite` | 译完自动复制 / 写回输入框（默认关闭） |
| `StudyWords` / `StudyStyle` | 显示重点词（需要大模型接口）/ `word`、`meaning`（默认）、`detail` |
| `ClickLookup` | 点英文单词查释义 |
| `ShowSpeak` / `SpeakOnLookup` / `SpeakSpeed` | 朗读按钮 / 查词时自动朗读 / `slow`、`normal`、`fast` |
| `Theme` / `Accent` / `CardTextSize` | `system`、`dark`、`light` / `blue`、`purple`、`pink`、`orange`、`green`、`graphite` / `small`、`normal`、`large` |
| `CardPosition` | `input`（跟着输入框，默认）、`top`、`bottom` |
| `ShowSource` / `ShowHints` / `ShowEngine` | 卡片上显示中文原文 / 操作提示 / 译文来源 |
| `AutoHideSeconds` | 完成后几秒自动收起，`0` 表示不收起（3–120） |

配置文件写坏了不会丢：启动时会把它另存为 `config.bad.json` 再用默认值。保存是先写临时文件再替换，不会写一半。

「开机自动启动」只写当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，随时可以在设置或托盘菜单里关闭。

## 原理

- **UI Automation**：后台线程读取焦点输入框的文本（不会卡住界面）。
- **终端**：Windows Terminal、经典控制台（PowerShell、cmd），以及在里面运行的 Grok、Claude。按光标所在的这一段输入读取，换行和行号后面的文字也会带上，不把整屏历史当成输入内容。VS Code / Cursor 里名称包含「终端输入」的输入区同样处理。
- **输入法钩子** `native/imehook.cpp`：注入目标进程，读取 TSF 输入域整段文字，并在 UIA 读不到时兜底。
- **OCR**：对微信这类自绘输入框（`mmui::`），用 Windows OCR 识别输入框区域。
- **翻译提速**：
  - 考研难度以前要两次请求（先直译再润色），重点词又是第三次；现在一次请求直接出考研难度的译文，重点词跟在一行 `<<<WORDS>>>` 后面一起返回。
  - 流式输出（`stream: true`），收到一段显示一段，卡片上有闪烁的光标；还没收到第一个词时显示占位条。
  - 能关掉「深度思考」的服务自动关掉：通义千问 / 硅基流动用 `enable_thinking: false`，智谱 / 豆包用 `thinking: disabled`，OpenAI gpt-5 用 `reasoning_effort: minimal`。服务不认这些参数（400 / 422）时自动去掉重发，并记住这个地址。
  - 按下热键时先预热连接（TLS 握手），之后复用长连接，支持 HTTP/2。
  - 大模型请求 60 秒超时，免费引擎 12 秒。

## 构建

需要 .NET 9 SDK 和 Visual Studio 生成工具（「使用 C++ 的桌面开发」，x64）。

```powershell
# 编译 native + 托管程序
.\build.ps1

# 发布程序并生成安装包到 installer\
.\build.ps1 -Publish

# 不重新编译 native\imehook.dll
.\build.ps1 -SkipNative
```

没有 C++ 生成工具时，普通编译会给出警告并跳过输入法钩子（已有 `native\imehook.dll` 就直接用）；`-Publish` 必须带上钩子。

产物：

- `bin\Release\net9.0-windows10.0.19041.0\InputTranslate.exe`（依赖 .NET 9 桌面运行时）
- `publish\InputTranslate.exe`（单文件自包含）+ `publish\PresentationNative_cor3.dll` + `publish\vcruntime140_cor3.dll` + `publish\hook\imehook.dll`
- `installer\InputTranslate-Setup-1.1.0.exe`（安装包，不需要 .NET 运行时）

> 单文件版旁边的两个 DLL 不能删：UI Automation 的 `UIAutomationTypes` 会 P/Invoke `PresentationNative_cor3.dll`，它卸载时还需要 `vcruntime140_cor3.dll`。缺了之后卡片还能显示（靠输入法钩子），但「复制 / 写入输入框」会失效，退出时还会崩溃。

## 诊断

```powershell
InputTranslate.exe --diag              # 打印当前焦点元素信息（不输出正文）
InputTranslate.exe --diag --text       # 连同正文前 120 字
InputTranslate.exe --translate 你好    # 用当前设置测试翻译，并打印第一个词和整句的用时
InputTranslate.exe --translate 你好 transmart                    # 临时换引擎
InputTranslate.exe --translate 你好 openai http://127.0.0.1:11434/v1 qwen2.5:7b  # 临时换接口和模型，不改配置
InputTranslate.exe --uninstall [--yes] # 卸载（--yes 不弹确认）
InputTranslate.exe --selftest-words    # 卡片排版、流式输出、热键解析等自检
InputTranslate.exe --selftest-terminal # 终端读取自检
InputTranslate.exe --selftest-hook     # 输入法钩子自检（要在前台终端里运行）
InputTranslate.exe --selftest-speech   # 检查有没有英文语音（不出声）
```

日志在 `%APPDATA%\InputTranslate\run.log`，只记事件和长度，不记原文，超过 1 MB 自动轮换。

界面预览（开发用，不读输入、不翻译）：

```powershell
InputTranslate.exe --preview card dark            # card / gloss / stream / detail / words / listening / error / idle
InputTranslate.exe --preview settings:3 light     # 设置窗口，数字是第几页（0 开始）
InputTranslate.exe --preview card dark+purple     # 主题后面可以接强调色
InputTranslate.exe --preview card light shot.png  # 截图后退出
InputTranslateSetup.exe --preview done            # 安装程序：options / busy / error / done
```
