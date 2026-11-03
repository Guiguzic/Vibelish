# 输入翻译

Windows 托盘小工具：在当前输入框里输入中文，按 **考研英语** 的难度翻成英文，并在输入框上方显示一个不抢焦点的卡片。

![图标](assets/preview.png)

## 安装

双击 `installer\InputTranslate-Setup-1.0.0.exe`：

- 安装到当前用户目录 `%LOCALAPPDATA%\Programs\InputTranslate`，**不需要管理员权限**。
- 默认勾选「开机自动启动」，安装后登录 Windows 会静默启动到托盘。
- 可选创建桌面快捷方式；默认安装完成后启动。
- 卸载：设置 → 应用 → 输入翻译，或开始菜单里的「卸载输入翻译」，也可以运行 `InputTranslate.exe --uninstall`。

安装包是单文件 exe，内含完整程序；卸载会删除程序文件、开始菜单/桌面快捷方式和开机启动项。

## 使用

1. 运行后托盘出现图标，屏幕上方短暂显示提示卡片（约 9 秒后自动隐藏）。
2. 按热键 **Ctrl+Alt+T** 开始。若该组合被其他程序占用，会自动改用 `Ctrl+Alt+E`、`Ctrl+Alt+Q`、`Ctrl+Shift+F9` 或 `Ctrl+Alt+G`；实际生效的组合显示在托盘气泡、卡片和托盘菜单里。
3. 在任意输入框里输入中文。停顿约 0.8 秒后自动翻译，卡片显示英文。
4. 点英文里的单词可以查释义。带「本句」的是这个词在当前句子里的意思，也可以改选别的义项，再按那个意思改一句，觉得合适就「采用这句」。再点同一个词，或点「收起」，就关掉释义。
5. 按 **回车** 结束本次；也可以再按一次热键结束。卡片上的「复制」「写入输入框」按钮可以直接使用，卡片右上角「×」只隐藏卡片。

再次双击 exe 不会启动第二份，而是让已在运行的那份把卡片显示出来。

托盘菜单：允许热键 / 开机自动启动 / 开始结束 / 显示卡片 / 设置 / 退出。

## 设置

`%APPDATA%\InputTranslate\config.json`，或右键托盘图标 → 设置。

| 键 | 说明 |
|---|---|
| `Engine` | `auto`（MyMemory → Google）、`mymemory`、`google`、`openai` |
| `EnglishLevel` | `kaoyan`（考研英语，默认）、`cet6`（六级）、`plain`（日常）；只对 OpenAI 兼容接口生效 |
| `OpenAiBaseUrl` / `OpenAiApiKey` / `OpenAiModel` | 任意 OpenAI 兼容接口 |
| `PauseMs` | 输完停顿多久开始翻译 |
| `ChineseOnly` | 只翻译含中文的内容 |
| `AutoCopy` / `AutoWrite` | 译完自动复制 / 写回输入框（默认关闭） |

「开机自动启动」只写当前用户的 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，随时可以在设置或托盘菜单里关闭。

## 原理

- **UI Automation**：后台线程读取焦点输入框的文本（不会卡住界面）。
- **终端**：Windows Terminal、经典控制台（PowerShell、cmd），以及在里面运行的 Grok、Claude。按光标所在的这一段输入读取，换行和行号后面的文字也会带上，不把整屏历史当成输入内容。VS Code / Cursor 里名称包含「终端输入」的输入区同样处理。
- **输入法钩子** `native/imehook.cpp`：注入目标进程，读取 TSF 输入域整段文字，并在 UIA 读不到时兜底。
- **OCR**：对微信这类自绘输入框（`mmui::`），用 Windows OCR 识别输入框区域。
- **翻译**：默认走已配置的 OpenAI 兼容接口。考研难度分两步：先做忠实翻译，再把句子改写成考研阅读的词汇和句式（The Economist 风格），改写时同时参考中文原文，避免走样。

## 构建

需要 .NET 9 SDK 和 Visual Studio 生成工具（C++ x64）。

```powershell
# 编译 native + 托管程序
.\build.ps1

# 发布程序并生成安装包到 installer\
.\build.ps1 -Publish
```

产物：

- `bin\Release\net9.0-windows10.0.19041.0\InputTranslate.exe`（依赖 .NET 9 桌面运行时）
- `publish\InputTranslate.exe`（单文件自包含）+ `publish\PresentationNative_cor3.dll` + `publish\vcruntime140_cor3.dll` + `publish\hook\imehook.dll`
- `installer\InputTranslate-Setup-1.0.0.exe`（安装包，需要 .NET 9 桌面运行时）

> 单文件版旁边的两个 DLL 不能删：UI Automation 的 `UIAutomationTypes` 会 P/Invoke `PresentationNative_cor3.dll`，它卸载时还需要 `vcruntime140_cor3.dll`。缺了之后卡片还能显示（靠输入法钩子），但「复制 / 写入输入框」会失效，退出时还会崩溃。

## 诊断

```powershell
InputTranslate.exe --diag          # 打印当前焦点元素信息（不输出正文）
InputTranslate.exe --diag --text   # 连同正文前 120 字
InputTranslate.exe --translate 你好 # 测试翻译接口
InputTranslate.exe --uninstall     # 卸载（带确认）
```
