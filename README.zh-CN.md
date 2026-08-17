# Language Switcher

*[English](README.md)*

**[在 Nexus Mods 下载](https://www.nexusmods.com/stardewvalley/mods/50762)** · [更新日志](CHANGELOG.md)

星露谷的语言在启动时就固定了——想看看某句话换个语言怎么说，正常得退出游戏、改设置、重新读档。

这个 mod 让你在游戏过程中按一个键即时切换语言。主要给用星露谷学外语的玩家用：切到目标语言看看某个菜单、物品或对话是怎么表达的，再切回来确认自己理解得对不对。

支持游戏自带 12 种语言中的**任意两种**互切，不必以英文为一端。比如你用中文玩、在学意大利语，那就是中文 ↔ 意大利语，全程不需要经过英文。

## 功能

**一键切换语言**（默认 `K`）
菜单、物品名、提示框、对话立即生效——不用重启，也不用重新读档。切换发生在你当前所用语言和设定的目标语言之间。

**对话回放日志**（默认 `L`）
一个可滚动的窗口，显示最近的 NPC 对话，按一个键即可在原文和译文之间切换。它存在的原因是：游戏在文字绘制的那一刻就把语言固定了，已经显示在屏幕上的对话无法再翻译（见下方"已知限制"）——这个日志就是让你事后回看的方式。玩家的对话选项也会被记录，包括你没有选的那些。

**标题画面配置**
标题画面上的 **Language Switch** 按钮可以选目标语言、改两个快捷键。游戏已占用的按键（WASD、方向键、E、F、M、Y、Tab、数字键……）会被拒绝，不会误伤你的操作。

**支持 Generic Mod Config Menu**（可选）
装了 [GMCM][gmcm] 的话，同一套设置也能从游戏内的 Mod Options 页面修改。

支持的语言：English、Русский、中文、Deutsch、Português、Français、Español、日本語、한국어、Italiano、Türkçe、Magyar

## 依赖

- [SMAPI](https://smapi.io/) 4.5.0 或更高版本
- Stardew Valley 1.6+
- [Generic Mod Config Menu][gmcm]（可选，不装也完全能用，只是少了游戏内菜单这一种配置方式）

## 安装

1. 安装 SMAPI（如果还没装）
2. 从 [Nexus Mods](https://www.nexusmods.com/stardewvalley/mods/50762) 下载本 mod
3. 解压到 `Stardew Valley/Mods/` 目录，最终应为 `Mods/LanguageSwitcher/manifest.json`
4. 用 SMAPI 启动游戏
5. 在标题画面点击右下角的 **Language Switch** 按钮选择目标语言

## 使用

- 进入存档后按 **K**：切换语言，右上角会弹一条提示
- 按 **L**：打开对话回放日志。在日志窗口**内**按 K 只切换这个窗口显示原文还是译文，不会改变游戏本身的语言
- 标题画面右下角 **Language Switch** 按钮：选目标语言、改快捷键，不需要 GMCM

### 配置项

| 字段 | 默认值 | 说明 | 可修改位置 |
|---|---|---|---|
| `PreferredLanguage` | `zh` | 切换的目标语言：`en`/`ru`/`zh`/`de`/`pt`/`fr`/`es`/`ja`/`ko`/`it`/`tr`/`hu`（与游戏自带的语言列表一致） | 标题画面、GMCM 或 `config.json` |
| `HotKey` | `K` | 切换语言的快捷键 | 标题画面、GMCM 或 `config.json` |
| `ReplayLogHotKey` | `L` | 打开对话回放日志的快捷键 | 标题画面、GMCM 或 `config.json` |
| `ShowNotifications` | `true` | 切换语言时是否显示 HUD 提示 | 仅 `config.json` |
| `NotificationDuration` | `3` | 提示显示几秒 | 仅 `config.json` |
| `VerboseLogging` | `false` | 是否把对话捕获、翻译查找和字体的诊断信息写进 SMAPI 日志。排查"某句话没被记录/没有译文"时打开，报问题时附上日志会很有帮助；错误和警告不受此开关影响，始终会记录 | 仅 `config.json` |

`config.json` 在首次运行后自动生成于 mod 文件夹内。

两个快捷键字段支持多组绑定和组合键（`K, LeftShift + K`），但只能通过 `config.json` 或 GMCM 设置。标题画面的菜单只能捕获单个按键，所以在那里改键会把多重绑定替换成单键——它会在覆盖前提示你。

## 已知限制

这些限制源自游戏本身的文字处理方式：文字在显示的那一刻就已经确定了语言，之后无法追溯改变。

- **切换会等待当前界面结束**：如果按下切换键时有菜单打开（NPC 对话、电视、信件、地图、背包等）或屏幕上有通知提示，切换会自动推迟到它们关闭之后再执行。这样可以保证界面文字始终完整可读，代价是切换有短暂延迟
- **已显示的对话保持原语言**：正在进行或已经看过的对话不会追溯翻译。想查看双语对照，请使用对话回放日志（`L`）
- **分支对话的翻译为尽力而为**：涉及随机分支或玩家选择分支的对话，翻译依赖于能否还原实际触发的那个分支；无法确定时会显示"无翻译"，而不是给出可能错误的内容

## 从源码构建

需要 [.NET 6 SDK](https://dotnet.microsoft.com/download)。游戏自带的是 .NET 6 运行时，所以目标框架必须是 `net6.0`，不能升到更新的版本。

```bash
dotnet build LanguageSwitcher/LanguageSwitcher.csproj
```

[Mod Build Config](https://github.com/Pathoschild/SMAPI/blob/develop/docs/technical/mod-package.md) 会顺带做两件事：把编译结果部署到 `Stardew Valley/Mods/LanguageSwitcher/`（启动 SMAPI 就能直接测），并在 `LanguageSwitcher/bin/<配置>/net6.0/` 下生成可发布的 zip。

游戏运行时会占用 DLL，此时部署会失败并报 `being used by another process`。关掉游戏再编译，或者只编译不部署：

```bash
dotnet build LanguageSwitcher/LanguageSwitcher.csproj -p:EnableModDeploy=false
```

打发布包用 Release 配置，产物是 `LanguageSwitcher/bin/Release/net6.0/LanguageSwitcher <版本号>.zip`：

```bash
dotnet build LanguageSwitcher/LanguageSwitcher.csproj -c Release
```

排查问题时把 `config.json` 里的 `VerboseLogging` 设为 `true`，对话捕获和翻译查找的过程就会写进 SMAPI 日志。

## 许可证

[MIT](LICENSE)

[gmcm]: https://www.nexusmods.com/stardewvalley/mods/5098
