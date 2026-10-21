# CLAUDE.md — Lexi 开发指南

Lexi 是 PowerToys Command Palette 的英/德 → 中词典扩展（C# / .NET 10，官方 CmdPal SDK）。用户文档见 [README.md](README.md)，架构见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)，数据源与许可见 [docs/DATA_SOURCES.md](docs/DATA_SOURCES.md)。

## 环境与命令

- .NET 10 SDK 装在用户目录：`$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"`（PATH 里的 dotnet 不一定是 10.x，脚本里用 `Get-LexiDotnet`）。
- 没有 Visual Studio，也没有 Windows SDK。CsWinRT 元数据来自 NuGet 的 `microsoft.windows.sdk.net.ref\<ver>\winmd`；MakeAppx/MakePri 来自 `Microsoft.Windows.SDK.BuildTools`。
- 本机词典：`%LOCALAPPDATA%\Lexi\data\lexi.db`（约 495 MB）。原始数据在 `%LOCALAPPDATA%\Lexi\raw`（约 1.2 GB）。可以用 `LEXI_DB` 指定其他位置。

```powershell
& $dotnet build src\Lexi.Extension\Lexi.Extension.csproj -c Debug -p:Platform=x64   # 扩展 + 可注册的布局（bin\x64\Debug\...\win-x64）
& $dotnet test tests\Lexi.Tests\Lexi.Tests.csproj                                    # 123 个测试，期望 0 失败 0 跳过（有数据库时）
& $dotnet build src\Lexi.Cli\Lexi.Cli.csproj -c Release
& $dotnet src\Lexi.Cli\bin\Release\net10.0\lexi.dll quality                          # 15/15
& $dotnet src\Lexi.Cli\bin\Release\net10.0\lexi.dll query <词> | show <词> [--full] | stats
& $dotnet src\Lexi.Cli\bin\Release\net10.0\lexi.dll bench --rounds 5 --sample 300 --out docs/BENCHMARKS.md
& $dotnet src\Lexi.Cli\bin\Release\net10.0\lexi.dll ai <词> [--kind grammar] --prompt  # 只打印提示词，不调用 Claude
powershell -ExecutionPolicy Bypass -File scripts\self-test.ps1 [-Package|-Installed] [-Network] [-Ai]
powershell -ExecutionPolicy Bypass -File scripts\package.ps1                          # Release MSIX → artifacts\msix
powershell -ExecutionPolicy Bypass -File scripts\install.ps1                          # 需要开发者模式
powershell -ExecutionPolicy Bypass -File scripts\deploy-dev.ps1                       # 注册 Debug 布局，需要开发者模式
powershell -ExecutionPolicy Bypass -File scripts\import-data.ps1 [-NoEcdict] [-Limit N]
```

## 规矩（来自项目要求，必须遵守）

- 不修改 PowerToys 源码；不注册或占用 `Alt+Space` 或任何全局热键（别名和热键由用户在 CmdPal 设置里自己配）。
- 不提交、不打包词典数据（`.gitignore` 已排除 `*.db`、`data/raw`）。ECDICT 标记为不可再分发。不用网页抓取绕过许可。
- AI 只能由用户触发，结果必须标注为 AI 生成，不能覆盖词典内容。不保存、不记录任何凭据或 Key。AI 使用本机 Claude Code CLI（用户明确要求，替代原需求中的 OpenAI 兼容配置）。
- 不得为了让测试通过而硬编码测试输入。验收用例只能用来检验结果（`AcceptanceCases.cs`），搜索代码里不能有特判。
- 没跑过的测试不能说通过。占位实现不能当成完成。部署受阻时如实说明。
- 需要管理员权限、改系统设置（包括开发者模式）、做不可逆操作之前，先征得用户同意。

## 踩过的坑

- **Claude 桌面版是 MSIX 应用，它启动的 shell（也就是 Claude Code 的工具进程）对 `%LOCALAPPDATA%` 的写入会被重定向**到 `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\…`。
  - 受影响的有：本机词典、原始数据、install.ps1 解压的安装目录、.NET SDK（`%LOCALAPPDATA%\Microsoft\dotnet`）。这个容器里的进程看得到这些文件；CmdPal、AppX 部署服务、普通终端都看不到。所以从这里 `Add-AppxPackage` 注册 AppData 下的目录会报 0x80070003。
  - `GetCurrentPackageFullName` 在这里报告「无包身份」，所以检测要用探针：`DataPaths.DetectAppDataRedirection` 和 `common.ps1` 里的 `Get-AppDataRedirection`。
  - 仓库目录（`C:\workspace\…`）不受影响：Debug 布局可以从这里注册，`artifacts\` 里的产物也是真实文件。
  - 在容器外起进程（如 WMI）会被自动模式拦截。需要写真实 AppData 的步骤（`install.ps1`、`import-data.ps1`）只能请用户在普通终端运行。
  - 扩展支持从 `程序目录\Data\lexi.db` 读取词典，Debug 布局验证时可以把词典复制到那里。
- **CmdPal 失去焦点会自动收起**：窗口是用 DWM cloak 隐身的（`IsWindowVisible` 仍为 true），隐身后 UIA 读不到任何内容。用户正在操作其他窗口时，后台进程也抢不到前台。做 UI 自动化验证前，要请用户暂时别动电脑。
- CmdPal 的搜索框 AutomationId 是 `MainSearchBox`。`x-cmdpal://settings` 打开设置，其他 `x-cmdpal://…` 地址会唤出主窗口。验证脚本在 `artifacts\cmdpal-verify\`（`uia.ps1`、`verify*.ps1`，不提交）。
  - **含中文的 .ps1 必须带 UTF-8 BOM**，否则 PowerShell 5.1 会按 GBK 解析，直接报语法错误。
  - **在根页面按 `Alt+←` 会收起窗口**（NavigateBack 发现没有上一页时会触发 Dismiss）。回主页要用 `Alt+Home`。
  - 通过上下文命令快捷键（如 Ctrl+I）打开的页面拿不到键盘焦点，Esc 无效。这是宿主的问题，从 Ctrl+K 菜单或回车打开则正常。
  - 设置窗口里的扩展列表是虚拟化的，要先用「搜索扩展」框过滤。不要在设置窗口里批量点击，那里的开关改的是用户自己的配置。
- 同一个包反复注册（Debug 和 Release 交替）后，CmdPal 设置里会出现多个 Lexi 条目，需要「Reload」清理。
- **不要用 Windows PowerShell 5.1 的 `Get-Content`/`Set-Content` 改源码**：它默认按 GBK 读写，会把中文变成乱码（曾经损坏过 3 个文件）。改源码用 Edit/Write 工具，或者 Git Bash 的 `sed`（按字节处理，安全）。
- `Lexi.Extension` 必须指定平台（`-p:Platform=x64`）。项目文件里已经把 AnyCPU 映射到 x64。
- `CsWinRTWindowsMetadata` 指向 NuGet 里的 winmd。不设置的话，CsWinRT 会去注册表找 Windows SDK，然后报错。
- 缺少 `Microsoft.Windows.SDK.BuildTools` 会报 APPX3217 `UAP.props`。资源清单默认语言要显式写成 `<DefaultLanguage>en-US</DefaultLanguage>`，否则会取构建机的 UI 语言。
- 资源文件（`Assets\*.png`）要设置 `CopyToOutputDirectory`，构建输出目录才能直接当作布局注册。
- **工具包的 `CommandItem.Title` 为空时会回退到 `Command.Name`**。想隐藏 fallback，必须同时把 `Command` 换回无名的 `NoOpCommand`。
- 宿主同步调用 fallback 的 `UpdateQuery`（专用线程，慢阈值 200 ms），这里只能做低成本工作。主页每次按键都会创建一个 `DictionaryPage`，页面要等第一次 `GetItems()` 才搜索。
- `DictionaryPage.SearchText` 的 getter 带副作用：顶层页面超过 1 秒没有输入时，读取会重置回首页（用来实现「每次打开都从首页开始」）。测试或驱动代码不要随便读它。
- 从 Claude Code 会话里启动子进程 `claude` 时，会继承 `CLAUDECODE`、`CLAUDE_CODE_SIMPLE` 等环境变量，进入 bare 模式后 OAuth 失效。`UserEnvironment.Apply` 会从注册表重建环境变量并移除这些变量。
- PowerShell 5.1 里没有 `ProcessStartInfo.ArgumentList`，只能用 `.Arguments`。
- 刚解压或刚构建的二进制第一次运行很慢（约 600 ms，Defender 扫描 / 首次加载）。性能要看第二次运行。
- Wikimedia 偶尔对单个请求返回瞬时错误。`Pronouncer` 会重试一次，并把原因记录在 `LastDownloadError`。
- 用 PowerShell 捕获 `lexi` 的输出时，`→` 之类的字符会显示成乱码（因为按 GBK 解码）。直接在控制台运行时显示正常。

## 进度记录

### Phase 0 — 环境检查与项目骨架 ✅

- 环境：Windows 11 x64（10.0.26300）、PowerToys 0.100.2（CmdPal）。用户目录下装了 .NET SDK 10.0.402；没有 VS 和 Windows SDK。
- 做了什么：
  - 建立解决方案：Core / Dictionary / AI / Extension / Cli / Tests；
  - 用中央包版本管理；
  - 照官方 ExtensionTemplate 写好 MSIX 清单（CLSID `EB940CBB-5F48-4AC0-8050-328A3170ABF7`）；
  - 写好脚本 `common.ps1`、`deploy-dev.ps1`、`generate-icons.ps1`。
- 验证：扩展构建成功，输出目录是完整的 MSIX 布局（含 AppxManifest.xml、resources.pri、Assets）。

### Phase 1 — 能在 CmdPal 中运行的最小垂直功能 ✅（2026-10-21 在 CmdPal 界面实测）

- 做了什么：
  - Provider、顶层命令 Lexi（副标题带 `dict`）、`DictionaryPage` 实时搜索加详情面板、完整词条页；
  - 语言筛选、空结果提示；
  - 主页 fallback（`dict 词`）、设置页、历史记录。
- 已验证：
  - 自检 `--self-test` 在 Debug 构建和裁剪后的 Release MSIX 内容上都通过（当前 34 项）；
  - 自检发现并修复了 fallback 残留「打开词典」条目的 bug；
  - 2026-10-21，开发者模式打开后，在 CmdPal 界面里用 UI 自动化加截图实测（Debug 布局注册，词典放在布局的 `Data\` 下）：
    - 注册后 CmdPal 立即自动启动扩展；
    - Lexi 出现在主页，主页搜 `dict` 时排第一；
    - 进入 Lexi 页面：首页提示、语言筛选下拉框、详情面板、命令栏（↵ / Ctrl+↵ / Ctrl+K）都正常；
    - 15 个验收用例在界面里结果都正确。
  - 界面实测发现并修复了两个问题：英语副标题词性重复（`n. n.`），按义项的对应词重复（房子 ×3）。
- 部署时发现的问题：从 Claude 的 shell 运行 install.ps1 时，AppData 写入被重定向，注册失败（见上文「踩过的坑」）。已加上检测、拒绝执行和数据接管（`Move-LexiRedirectedData`）。
- 用户在普通终端运行 `install.ps1 -SkipBuild` 后，安装成功：数据从 Claude 的私有目录移到了 `%LOCALAPPDATA%\Lexi`，Release 包从 `%LOCALAPPDATA%\Lexi\app\0.1.0.0-x64` 注册。之后在 CmdPal 界面实测（`verify.log`、截图 20–35）：
  - 完整词条页能渲染，含「来源与许可」；Esc 返回列表时保留查询词；
  - Ctrl+K 菜单列出复制释义、发音、复制单词、UK/US 发音、三个 AI 项、Wiktionary；
  - Ctrl+I 打开 AI 页：打包后的扩展调用 Claude Code 成功（8 秒），有 AI 标注；
  - 德语详情里对应词已去重；语言筛选选 Deutsch 后只剩德语结果；
  - 离开后重新进入 Lexi，会回到首页并显示历史记录；
  - 主页 fallback：`dict vulnerability` 和不带前缀的 `Häuser` 都显示一行释义，回车后进入的页面预填查询并列出 das Haus；
  - Lexi 出现在设置的「已安装」扩展里（1 个命令，1 个回退命令）。
- 完整词条页的例句原来是列表项里嵌套的引用块，在 CmdPal 里不换行，相邻两条还会并成一段。现在改成列表项内硬换行加斜体，并过滤掉 Wiktionary 误归为例句的「Synonym: …」「Near-synonym: …」说明。CLI 渲染和单元测试已验证，界面效果要等用户重新安装后再看。
- 界面实测又发现并修复了一个问题：AI 页完成后，回车仍然执行「取消请求」。现在命令随状态切换（加载中=取消，完成=复制，失败=重新生成），并修复了「重新生成」时被取消的旧请求覆盖新请求界面的竞态。这些改动需要用户再运行一次 install.ps1 才会生效。
- 未在界面验证：
  - 发音：用户要求先不播放声音。录音下载和语音合成已经由自检在不出声的情况下验证过；
  - 设置里 Lexi 详情页的表单和别名输入框：自检验证过表单能渲染和保存，界面上还没点进去看。

### Phase 2 — 真实词典数据 ✅

- 数据源调研（ECDICT、Kaikki en/de/zh、FreeDict、FrequencyWords）以及许可结论见 `docs/DATA_SOURCES.md`。
- 统一模型 `EntryData`，SQLite schema v1，可重复的导入流水线（下载约 1 分钟，导入实测约 9–10 分钟）。
- 完整数据库：975,975 个词头，1,322,126 条释义，1,092,295 个词形，模糊索引 952,321 个键。
- 验证：
  - 用样例文件的完整导入测试；
  - 真实数据抽查：Schwachstelle、gehen、Haus、vulnerability 等。

### Phase 3 — 搜索与纠错 ✅

- 做了什么：
  - 规范化（NFKC、fold、translit）、反向转写、DAWG 模糊索引（OSA 距离 ≤ 2）；
  - 前缀补全、词形还原、热度排序（Zipf）；
  - 强匹配时抑制纠错，专有名词、词组降权。
- 验证：验收用例 15/15（`lexi quality`、`RealDictionaryTests`、自检中走页面路径的用例）。
  - `authen` 期望的是「第一屏」（≤ 8），实际排第 6。前面几位是更常用的 authentic、authenticity 等，按词频排在前面是合理的，不为测试特调。

### Phase 4 — 发音与 AI ✅（AI 已在 CmdPal 界面验证；发音应用户要求未在界面播放）

- 发音：Commons 真人录音（MD5 路径、缓存、失败重试一次）→ Windows TTS → 明确提示「无可用发音」。三种情况在提示中区分。
- AI：
  - 通过 Claude Code CLI 的无头模式（stream-json 流式输出）；
  - 支持取消、超时、30 天缓存、串行加 1.5 秒间隔、标注为 AI 生成；
  - 本地词典事实作为参考上下文一起发送（`AiContext`）；
  - 5 类任务，以及特殊输入「A vs B」「词 @领域」和句子。
- 验证：
  - `lexi ai Schwachstelle --kind grammar` 真实调用成功（流式输出，7 秒）；
  - 自检 TTS 能为 en-US / de-DE 合成；
  - 录音下载成功；
  - 单元测试覆盖参数、流事件解析和提示词。

### Phase 5 — 性能与发布 ✅

- 性能（`docs/BENCHMARKS.md`）：
  - 全部查询 p50 1.7 ms、p95 3.5 ms、最大 8 ms；
  - 冷启动打开数据库约 25 ms；
  - 详情构建 p50 0.6 ms（加了 `ix_forms_lemma`，原来是 76 ms）；
  - 扩展端到端（含 40 ms 去抖）约 45–65 ms；
  - provider 构造约 15 ms。
- 发布：
  - `package.ps1`：自包含、裁剪、R2R 的 MSIX，16.3 MB；
  - `install.ps1`：解压到 `%LOCALAPPDATA%\Lexi\app` 后注册；
  - `uninstall.ps1`；
  - `self-test.ps1`。
- 文档：README、ARCHITECTURE、DATA_SOURCES、BENCHMARKS、THIRD-PARTY-NOTICES，以及本文件。

## 待办

1. **用户在普通终端操作**：再运行一次 `install.ps1 -SkipBuild`，装上 AI 页的修复（`artifacts\msix` 里的包已经是最新的）；然后在 CmdPal 里执行 Reload，清理设置里重复的 Lexi 条目。
2. 用户方便时再验证两项：
   - 发音（Ctrl+P，会出声）；
   - 设置里 Lexi 详情页的表单和别名（Alias `dict`，Indirect）。
3. Ctrl+I 打开的页面里 Esc 无效是宿主问题。可以考虑向 PowerToys 报告，扩展这边不需要改。
4. （可选）签名发布：`package.ps1 -Sign` 会生成自签名证书。在其他电脑上信任证书需要管理员权限，执行前先征得用户同意。
5. （可选）FreeDict deu-eng 作为用户本机可选的补充来源（GPL/AGPL，不要合并进可分发的数据库）。
6. （可选）中文反查外语（目前交给 AI 处理）。
7. 还没有提交任何 git 提交。用户要求时再提交，提交信息末尾加 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`。
