# Lexi — Smart Dictionary for PowerToys Command Palette

Lexi 是 PowerToys **Command Palette**（CmdPal）的原生扩展，用来查英语和德语单词，默认给出中文释义；也可以输入中文，反查对应的英语和德语词。

**词典优先，AI 增强，键盘驱动**：

- 释义全部来自可追溯的本地词典（Wiktionary、ECDICT），每一块都标明来源和许可；
- AI 只在你按键时才调用（本机的 Claude Code），结果明确标注为 AI 生成，不写进词典数据；
- 从打开到查词都不用离开键盘。

扩展使用官方 Command Palette SDK（`Microsoft.CommandPalette.Extensions`）开发，以 MSIX 包注册，不修改 PowerToys 源码，也不占用任何快捷键。

## 功能

| | |
|---|---|
| 实时搜索 | 边输入边出结果；精确、前缀补全、拼写纠错（编辑距离 ≤ 2）、词频排序 |
| 词形还原 | `Häuser → Haus`、`ging → gehen`、`went → go`、`Schwachstellen → Schwachstelle` |
| 德语变音 | `Uebertragung → Übertragung`、`hauser → Häuser → Haus`、`ß/ss` 等价 |
| 中文反查 | 输入中文，分「英语 / 德语」两组列出对应词：`房子 → house / das Haus`、`防火墙 → firewall / Firewall`；繁体自动转简体；没有整词对应时（`网络安全漏洞`）列出其中最长的已知词语 |
| 详情面板 | 选中即显示：音标（英/美）、冠词与复数、动词主要形式、中文/英文/德文释义、来源 |
| 完整词条 | Enter 打开：全部义项、例句、变格/变位表、词形、近义/易混词、学习者信息、来源与许可 |
| 发音 | 优先使用真人录音（Wikimedia Commons，首次播放时下载并缓存）；没有录音时用 Windows 语音合成；两者都没有时明确提示「无可用发音」，三种情况在提示里区分清楚 |
| AI（可选） | 解释词义、德语语法、易混词辨析、专业语境、句子翻译、中文译成英语和德语。走本机 Claude Code CLI，可取消、可重新生成，结果缓存 30 天 |
| 在线翻译（可选） | 单独开关，默认关闭；用 Google 翻译把英语/德语句子译成中文、把中文译成英语和德语，按 Enter 才联网，无需 Claude 或 API Key。与 AI 翻译并列，两种可以都开 |
| 主页快查 | 在 CmdPal 主页输入 `dict vulnerability`，底部直接显示一行释义 |
| 其他 | 最近查询、语言筛选（全部 / English / Deutsch）、复制释义/单词 |

## 系统要求

- Windows 11 x64（ARM64 可以构建，但没有测试过）
- PowerToys 0.100 或更新，并启用 Command Palette（开发和测试用的是 0.100.2）
- 构建需要 [.NET 10 SDK](https://dotnet.microsoft.com/)，可以装在用户目录。**不需要** Visual Studio，也不需要 Windows SDK
- 在本机注册未签名的扩展包，需要 Windows **开发者模式**
- 可选：[Claude Code](https://claude.com/claude-code) CLI（AI 功能）；英语/德语语音包（语音合成）

## 快速开始

在仓库根目录打开 PowerShell。

> **请使用普通终端**（开始菜单里的 PowerShell，或 Windows Terminal）。
>
> 由 MSIX 打包应用（例如 Claude 桌面版）启动的终端，对 `%LOCALAPPDATA%` 的写入会被 Windows 重定向到该应用自己的私有目录：
> - 在那里构建的词典，Command Palette 看不到；
> - 在那里安装的 .NET SDK，普通终端也用不了。
>
> 脚本和 `lexi` 会检测这种情况并拒绝执行。如果之前已经在这样的终端里构建过词典，`install.ps1` 会自动把数据移到正确位置。

**1. 构建词典数据库**（只需一次，数据不随扩展分发，原因见[数据与许可](#数据与许可)）

```powershell
powershell -ExecutionPolicy Bypass -File scripts\import-data.ps1
```

脚本依次做四件事：

1. 下载约 1.2 GB 原始数据到 `%LOCALAPPDATA%\Lexi\raw`；
2. 构建约 500 MB 的 `%LOCALAPPDATA%\Lexi\data\lexi.db`，在 Ryzen 9 7940H 笔记本上实测约 10 分钟；
3. 运行验收用例；
4. 打印结果，应为 `All 34 cases passed`（15 个英语/德语用例 + 19 个中文反查用例）。

再次运行时不会重复下载。加 `-NoEcdict` 可以只用 CC BY-SA 来源构建。

> 已经装过旧版词典？中文反查需要新版词典里的中文索引，旧库会在中文查询时提示更新。再运行一次 `import-data.ps1` 即可；如果已经有构建好的新库文件，也可以用 `install.ps1 -Database <文件>` 直接安装。

**2. 打开开发者模式**

设置 → 系统 → 开发者选项 → 开发者模式。

**3. 安装扩展**

```powershell
powershell -ExecutionPolicy Bypass -File scripts\install.ps1
```

脚本会打出优化过的 Release 包（自包含 .NET、裁剪、ReadyToRun），解压到 `%LOCALAPPDATA%\Lexi\app\<版本>`，再注册给当前用户。整个过程不需要管理员权限。

**4. 在 Command Palette 中使用**

按 CmdPal 热键（默认 `Alt+Space`），输入 `dict` 或 `lexi`，回车，再输入单词。如果找不到 Lexi，在 CmdPal 里执行一次 `Reload`。

安装前可以先跑自检，不需要 Command Palette 也能验证扩展本身是否正常：

```powershell
powershell -ExecutionPolicy Bypass -File scripts\self-test.ps1 -Package
```

## 使用

### 打开词典的几种方式

| 方式 | 操作 |
|---|---|
| 基本 | `Alt+Space` → 输入 `dict`（或 `lexi`）→ `Enter` → 输入单词 |
| **推荐：设置别名** | CmdPal 设置 → Extensions → Lexi Dictionary → 命令 **Lexi**：把 **Alias** 设为 `dict`，把 **Alias activation** 设为 **Indirect**。之后 `Alt+Space` → 输入 `dict`+空格，就会直接进入词典 |
| 主页快查 | 在主页输入 `dict 单词`（也可以用 `lexi 单词` 或 `查 单词`），底部的 Lexi 结果显示一行释义，回车进入词典页并带上这个词。输入中文（`dict 房子`）时显示「房子 → house · das Haus」。默认不加前缀也行，但这时只对确实存在的词（含屈折形式、两个字以上的中文词语）显示，不做纠错猜测；可以在 Lexi 设置中关闭 |
| 独立热键（可选） | CmdPal 设置里为 Lexi 命令指定 **Global hotkey**。热键由你自己选，Lexi 不会自行注册 |

> Indirect 表示要输入「别名+空格」才跳转，所以不会挡住以 `dict` 开头的其他搜索（例如 dictionary）。CmdPal 每次按键都会检查搜索框，内容恰好等于 `dict`+空格时就跳转到 Lexi，接着在 Lexi 页面输入单词即可。

### 搜索示例

| 输入 | 结果 |
|---|---|
| `vulnerabilty`、`authentcation` | 纠错到 vulnerability / authentication（标签「纠错」；按 `→` 把正确拼写填入搜索框） |
| `authen` | 前缀补全，第一屏给出 authentic、authenticate、authentication 等，按词频排序 |
| `Häuser`、`hauser` | das Haus（提示「Häuser → 复数 Plural」） |
| `ging` | gehen（提示「过去时 Präteritum」） |
| `Uebertragung` | die Übertragung（标签「ae→ä」） |
| `affect vs effect`、`affect / effect` | 第一项是「AI 辨析」，下面照常列出词典结果 |
| `Schwachstelle @IT Security` | 第一项是「AI 专业语境」 |
| 英语或德语句子 | 先是「翻译这句话」（Google，打开在线翻译后），再是「AI 翻译这句话」（Claude Code），下面照常列出词典结果；短句也可以直接用「Lexi 句子翻译」命令 |
| `房子`、`電腦` | 分两组列出：「英语 English」house …，「德语 Deutsch」das Haus …（繁体自动转简体）。最后是「翻译」组：「Google 翻译成英语和德语」（打开在线翻译后）和「AI 讲解英语和德语说法」 |
| `网络安全漏洞` | 没有整词对应：列出其中最长的已知词语，网络安全 → cybersecurity，漏洞 → leak、loophole（标签「部分匹配」） |
| 中文句子，如 `我想学习德语。` | 最前面是「翻译」组：「Google 翻译成英语和德语」（打开在线翻译后）和「AI 翻译成英语和德语」（Claude Code），下面列出句中词语的对应词 |

### 翻译：AI 和在线两种方式

句子和中文都可以用两种方式翻译，两种可以同时开着，结果各自独立：

| | AI（Claude Code） | 在线翻译（Google） |
|---|---|---|
| 英语/德语句子 → 中文 | 「AI 翻译这句话」：译文加 1–3 个关键词或语法点讲解 | 「翻译这句话」：只给译文，约 1 秒 |
| 中文 → 英语和德语 | 「AI 翻译成英语和德语」（句子）或「AI 讲解英语和德语说法」（词语）：常用译法、区别和例句；词典自己的对应词会作为参考一起发过去 | 「Google 翻译成英语和德语」：同一页显示英语、德语两段译文，可以分别复制 |
| 开关 | 设置里的「启用 AI 增强」（默认开） | 设置里的「启用 Google 在线翻译」（默认关） |
| 费用与网络 | 用你的 Claude Code 用量 | 不需要 Key，不调用 Claude |

两种方式都只在按 Enter 时才请求，输入过程中不会联网。

**在线翻译**还有一个独立命令：打开开关后，主页多出 **Lexi 句子翻译**，可以单独设别名（如 `trans`，Alias activation 选 Indirect）。

- 在里面输入或粘贴英语/德语句子，例如 `The weather is nice today.`、`Wie geht's?`、`Guten Morgen!`；先列出 Google 的「翻译这句话」，AI 开着时下面再列「AI 翻译这句话」。
- 这个入口只接受至少两个词的句子或短语，单个单词继续用 **Lexi** 词典。
- 每次最多 2,000 个字符，不需要本地词典数据库。

在线翻译结果页的操作：
- 翻译中按 Enter 取消；完成后 Enter 复制译文，Ctrl+Enter 重新翻译；
- 最近 128 条译文只缓存在当前扩展进程的内存中，重新翻译会忽略缓存，不会写入词典或查询历史；
- 网络失败、超时或限流时会明确提示，可以从命令菜单打开 Google 翻译网页，不会自动转用 Claude。

> **关于 Google 接口**：在线翻译用的是 Google 网页翻译的免 Key 公共接口（`translate_a/single?client=gtx`），**不是**官方的 Google Cloud Translation API。
> - 这是有意的选择：不需要账号和 Key 就能联网翻译，作为 AI 之外的另一种方式。
> - 它没有公开文档，也没有稳定性保证，可能被限流或随时失效；这类调用方式也不在 Google 给开发者的正式接口之内。
> - 所以它默认关闭、只在按 Enter 时请求，并且有限速。
> - 只建议个人使用。如果要把扩展分享给别人，应换成官方接口（Google Cloud Translation、DeepL、Azure Translator 等，都需要 Key）。

### 快捷键（在结果列表中）

| 按键 | 作用 |
|---|---|
| `Enter` | 打开完整词条 |
| `Ctrl+Enter` | 复制释义 |
| `Ctrl+P` | 发音（英语另有 UK / US 两项，在 `Ctrl+K` 菜单里） |
| `Ctrl+Shift+C` | 复制单词 |
| `Ctrl+I` | AI 解释词义与用法 |
| `Ctrl+G` | AI 讲解德语语法（德语词条） |
| `Ctrl+K` | 全部命令：UK/US 发音、AI 易混词辨析、AI 专业语境、在 Wiktionary 中打开 |
| `→` | 接受拼写纠错 / 变音建议 |
| 筛选下拉框 | 全部语言 / English / Deutsch |
| `Esc` / `Alt+←` | 返回（从 Ctrl+I 打开的 AI 页面只能用 `Alt+←`，见已知限制） |

### AI（Claude Code）

AI 功能通过本机安装的 **Claude Code CLI** 的无头模式运行（`claude -p --output-format stream-json`）：

- 在你自己的 Claude Code 登录和配置下运行，用量计入你的 Claude Code 套餐或 API。Lexi 不读取、不保存任何凭据或 API Key；
- 只在你主动触发时请求。触发方式包括 `Ctrl+I`、`Ctrl+G`、命令菜单里的 AI 项，以及 `A vs B`、`词 @领域`、句子、中文等输入时出现的 AI 项；在线翻译（Google）是另一条独立的通道，不经过 Claude；
- 请求时禁用 Claude Code 的工具（`--tools ""`）、不保存会话，并在一个空的工作目录里运行；
- 本地词典里的事实（词性、复数、变位、音标、释义）会作为参考一起发过去，提示词要求模型不编造词形和音标，与词典不一致时要明说；
- 结果页顶部标明「AI 生成内容（Claude Code · 模型 …）— 不是词典数据」。回车执行的命令随状态变化：
  - 生成过程中，回车是**取消**；
  - 完成后，回车是**复制回答**，Ctrl+Enter 是**重新生成（忽略缓存）**；
  - 失败或取消后，回车是**重新生成**；
- 有超时（默认 90 秒）；同一时间只发一个请求，两次请求至少间隔 1.5 秒；相同问题缓存 30 天；
- 找不到 `claude` 时会给出明确提示，本地词典功能不受影响。

### 设置

在 CmdPal 设置 → Extensions → Lexi Dictionary 中修改。设置保存在扩展包的 LocalState 目录。

| 设置 | 默认值 | 说明 |
|---|---|---|
| 启用 AI 增强（Claude Code） | 开 | 关闭后隐藏所有 AI 入口 |
| 启用 Google 在线翻译 | 关 | 英语/德语句子 → 中文、中文 → 英语和德语的「Google 翻译」项，以及独立的「Lexi 句子翻译」命令。使用 Google 非官方免 Key 接口（见[翻译](#翻译ai-和在线两种方式)），与 AI 开关互相独立；不影响查词本身 |
| Claude Code 可执行文件路径 | 自动 | 查找顺序：PATH → `%USERPROFILE%\.local\bin\claude.exe` → npm 全局目录 |
| 模型 | 空 | 传给 `--model`，空表示沿用 Claude Code 自己的配置 |
| 推理强度 | 沿用 | 传给 `--effort`（low / medium / high） |
| AI 请求超时（秒） | 90 | 可设 10–600 |
| 专业语境 | IT Security | 用于「专业语境解释」和「易混词辨析」 |
| 在主页显示查词结果 | 开 | 不加 `dict` 前缀时，也在主页显示精确词条 |
| 详情中显示德语释义 | 开 | 德语词条额外显示 de.wiktionary 的德文释义 |

## 数据与许可

- **代码**：MIT（见 [LICENSE](LICENSE)）。
- **数据**：仓库和安装包里都不包含词典数据，数据库由你在本机从官方地址构建。
  - **Wiktionary**（经 Kaikki/wiktextract 抽取）：CC BY-SA 4.0，可以再分发，但要署名并以相同方式共享。
  - **ECDICT**：上游没有逐条说明授权，所以只用于本机个人数据库，**不随包分发**。
  - **录音**：使用时才从 Wikimedia Commons 下载。
  - **中文反查**不引入新来源：索引由上面这些来源里的中文文本在本机生成——ECDICT 的中文释义、英文版 Wiktionary 英语词条的翻译表、德文版 Wiktionary 的翻译表、中文版 Wiktionary 的德语释义，以及中文版 Wiktionary 中文词条的英/德翻译表。每个结果在词条页都能看到它来自哪个来源。

  调研过程、质量评估、取舍理由和导入新词典的方法见 [docs/DATA_SOURCES.md](docs/DATA_SOURCES.md)，第三方声明见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。
- 数据库的 `sources` 表记录每个来源的许可、网址、版本和署名。完整词条页底部列出该词用到的来源与许可。

## 性能

完整数据库：975,975 个词头，132 万条释义，109 万个词形，中文反查索引 76 万个中文词语、138 万条对应关系。

| 测量 | 结果 | 来源 |
|---|---|---|
| 单次搜索（预热后，834 个查询 × 5 轮） | p50 1.7 ms，p95 3.5 ms，最大 8 ms | [docs/BENCHMARKS.md](docs/BENCHMARKS.md)（`lexi bench`） |
| 冷启动 | 打开数据库约 25 ms，加载模糊索引约 20 ms | 同上 |
| 详情聚合与渲染 | p50 0.6 ms | 同上 |
| 中文反查（预热后） | 每次约 0.5–9 ms（含部分匹配的拆分查询） | `lexi quality`、`lexi query` |
| 扩展端到端：设置搜索文本 → 结果发布 | 约 45–65 ms，其中 40 ms 是有意的输入去抖 | `scripts\self-test.ps1` |
| 扩展启动（构造 provider） | 约 15 ms，不打开数据库 | 同上 |
| 内存 | 加载词典后工作集约 105–150 MB，私有内存约 60–70 MB | 同上 |

运行时的行为：

- 每次按键都会取消上一次查询；
- 每个结果都带「代号」，旧查询的结果不会覆盖新结果；
- 没有轮询；
- 数据库在第一次查词时才打开，空闲 10 分钟后自动释放。

## 开发

```powershell
$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"   # 或 PATH 里的 dotnet

& $dotnet build src\Lexi.Extension\Lexi.Extension.csproj -c Debug -p:Platform=x64   # 扩展（同时生成可注册的布局）
& $dotnet test tests\Lexi.Tests\Lexi.Tests.csproj                                    # 188 个测试；含真实数据库用例（无库或无中文索引时跳过）
powershell -ExecutionPolicy Bypass -File scripts\self-test.ps1                        # 进程内驱动真实页面（Debug 构建）
powershell -ExecutionPolicy Bypass -File scripts\deploy-dev.ps1                       # 把 Debug 构建注册为开发包
powershell -ExecutionPolicy Bypass -File scripts\package.ps1                          # Release MSIX → artifacts\msix
```

命令行工具 `lexi`（`src\Lexi.Cli`）提供以下命令：

- `download`、`import`：下载原始数据、构建数据库；
- `query`、`show`：调试搜索和词条渲染；
- `quality`：跑验收用例；
- `bench`：性能基准；
- `stats`：数据库统计；
- `zh-index`：给旧数据库的副本补建中文反查索引（`--from 旧库 --out 新库`，约 30 秒，不用重新导入）；
- `ai`：发一个 Claude Code 请求，加 `--prompt` 只打印提示词、不发请求。中文输入（`lexi ai 威胁`）自动按「翻译成英语和德语」处理。

例如：

```powershell
& $dotnet src\Lexi.Cli\bin\Release\net10.0\lexi.dll query hauser
```

架构、搜索算法、线程模型和关键决策见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)。开发约定和进度见 [CLAUDE.md](CLAUDE.md)。

## 卸载

```powershell
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1             # 移除扩展（保留词典数据）
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1 -RemoveData # 同时删除 %LOCALAPPDATA%\Lexi
```

## 故障排查

| 现象 | 处理 |
|---|---|
| CmdPal 里没有 Lexi | 在 CmdPal 执行 `Reload`；用 `Get-AppxPackage Lexi.SmartDictionary` 确认已注册；安装脚本要求开发者模式 |
| 「未找到词典数据库」 | 运行 `scripts\import-data.ps1`。也可以用环境变量 `LEXI_DB` 指定数据库位置 |
| 「词典数据在其他应用的私有目录中」，或脚本报 `This shell runs inside the packaged app …` | 词典是在打包应用的终端里构建的，被重定向了。在普通终端运行 `scripts\install.ps1 -SkipBuild`，它会把数据移到 `%LOCALAPPDATA%\Lexi` |
| `Add-AppxPackage` 报 0x80073CF0 / 0x80070003（找不到 AppxManifest.xml） | 同上：解压出来的文件被重定向了，部署服务看不到。换普通终端运行 |
| 想确认扩展本身是否正常 | `scripts\self-test.ps1 -Installed`（检查已安装的文件），加 `-Network` 测录音下载，加 `-Ai` 测 Claude Code |
| AI 提示找不到 Claude Code | 在 Lexi 设置里填写 `claude.exe` 的完整路径，并确认它在终端里能直接运行（已登录） |
| 发音提示「无可用发音」 | 设置 → 时间和语言 → 语音 → 添加英语/德语语音；有录音的词联网后会播放真人录音 |
| 重新导入时数据库被占用 | `import-data.ps1` 会先结束扩展进程；导入完成后重新打开 Lexi 即可 |
| 输入中文时提示「当前词典数据库还不支持中文反查」 | 词典是旧版构建的，没有中文索引。再运行一次 `scripts\import-data.ps1`（复用已下载的原始数据，约 10 分钟） |

## 已知限制

- 中文反查只用各词典给出的中文释义和翻译表。排序综合了词在释义中的位置、几个来源是否一致和词频：常用译法通常排在前几位，但不一定第一（例如 `弱点` 的 weakness 排第 3，前面是 foible、frailty）。个别来源数据有误（如 ECDICT 把 doobie 释为「数据库」），会照常显示；
- 很多德语词在 Wiktionary 中没有直接中文释义。这时 Lexi 显示英文/德文释义，并注明「暂无可靠的直接中文释义」，**不做**德→英→中的拼接；
- 扩展包没有签名。在其他电脑上安装需要开发者模式，或者自签名后以管理员身份信任证书（`scripts\package.ps1 -Sign`）；
- ARM64 没有实机测试；
- 用 `Ctrl+I` / `Ctrl+G` 打开 AI 页面后，按 Esc **不能**返回，要用 `Alt+←` 或左上角的返回按钮。这是 CmdPal 0.100.2 宿主的焦点问题：通过命令快捷键打开的页面拿不到键盘焦点，所有扩展都会遇到。从 `Ctrl+K` 菜单打开 AI 页面则没有这个问题；
- 同一个包重复注册（例如 `deploy-dev.ps1` 和 `install.ps1` 交替运行）后，CmdPal 设置的扩展列表里可能出现多个 Lexi 条目。可以在主页输入 `reload` 运行「Reload」，或者在 设置 → 扩展 → 已安装 →「更多选项」里重新加载扩展。
