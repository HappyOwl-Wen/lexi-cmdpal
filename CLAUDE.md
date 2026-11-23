# CLAUDE.md — Lexi 开发指南

Lexi 是 PowerToys Command Palette 的英/德 → 中词典扩展，也能中 → 英/德反查（C# / .NET 10，官方 CmdPal SDK）。用户文档见 [README.md](README.md)，架构见 [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md)，数据源与许可见 [docs/DATA_SOURCES.md](docs/DATA_SOURCES.md)。

## 环境与命令

- .NET 10 SDK 装在用户目录：`$dotnet = "$env:LOCALAPPDATA\Microsoft\dotnet\dotnet.exe"`（PATH 里的 dotnet 不一定是 10.x，脚本里用 `Get-LexiDotnet`）。
- 没有 Visual Studio，也没有 Windows SDK。CsWinRT 元数据来自 NuGet 的 `microsoft.windows.sdk.net.ref\<ver>\winmd`；MakeAppx/MakePri 来自 `Microsoft.Windows.SDK.BuildTools`。
- 本机词典：`%LOCALAPPDATA%\Lexi\data\lexi.db`（约 495 MB）。原始数据在 `%LOCALAPPDATA%\Lexi\raw`（约 1.2 GB）。可以用 `LEXI_DB` 指定其他位置。

```powershell
& $dotnet build src\Lexi.Extension\Lexi.Extension.csproj -c Debug -p:Platform=x64   # 扩展 + 可注册的布局（bin\x64\Debug\...\win-x64）
& $dotnet test tests\Lexi.Tests\Lexi.Tests.csproj                                    # 236 个测试，期望 0 失败；旧词典（无中文索引）跳过 19 个中文用例
& $dotnet build src\Lexi.Cli\Lexi.Cli.csproj -c Release
& $dotnet src\Lexi.Cli\bin\Release\net10.0\lexi.dll quality                          # 34/34（旧词典：15 通过，19 跳过）
& $dotnet src\Lexi.Cli\bin\Release\net10.0\lexi.dll import --out artifacts\data\lexi.db   # 从这里构建新词典只能写到仓库里（见下方 AppData 重定向）
& $dotnet src\Lexi.Cli\bin\Release\net10.0\lexi.dll zh-index --from <旧库> --out <新库>  # 只重建中文反查索引，约 30 秒
& $dotnet src\Lexi.Cli\bin\Release\net10.0\lexi.dll query <词> | show <词> [--full] | stats
& $dotnet src\Lexi.Cli\bin\Release\net10.0\lexi.dll bench --rounds 5 --sample 300 --out docs/BENCHMARKS.md
& $dotnet src\Lexi.Cli\bin\Release\net10.0\lexi.dll ai <词> [--kind grammar] --prompt  # 只打印提示词，不调用 Claude
powershell -ExecutionPolicy Bypass -File scripts\self-test.ps1 [-Package|-Installed] [-Network] [-Ai]
powershell -ExecutionPolicy Bypass -File scripts\package.ps1                          # Release MSIX → artifacts\msix
powershell -ExecutionPolicy Bypass -File scripts\install.ps1 [-SkipBuild] [-Database artifacts\data\lexi.db] [-Package <.msix>]   # 需要开发者模式；-Database 同时装新词典；-Package 安装下载的 Release 包，不构建
powershell -ExecutionPolicy Bypass -File scripts\deploy-dev.ps1                       # 注册 Debug 布局，需要开发者模式
powershell -ExecutionPolicy Bypass -File scripts\import-data.ps1 [-NoEcdict] [-Limit N]
```

## 规矩（来自项目要求，必须遵守）

- 不修改 PowerToys 源码；不注册或占用 `Alt+Space` 或任何全局热键（别名和热键由用户在 CmdPal 设置里自己配）。
- 不提交、不打包词典数据（`.gitignore` 已排除 `*.db`、`data/raw`）。ECDICT 标记为不可再分发。不用网页抓取绕过许可。
  - **例外（用户 2026-10-21 明确决定）**：在线翻译（`Lexi.Translation.GoogleTranslator`）使用 Google 的非官方免 Key 接口 `translate_a/single?client=gtx`，目的是在 AI 之外再提供一种联网翻译。
    - 它默认关闭，只在按回车时请求，有限速，只缓存在内存里；设置和界面都写明是非官方接口。
    - 不要把它当作违规报告，也不要移除。
    - 如果以后要分发扩展，改用官方 API（Google Cloud Translation、DeepL、Azure Translator 等，Key 存在 Windows 凭据管理器）。
- AI 只能由用户触发，结果必须标注为 AI 生成，不能覆盖词典内容。不保存、不记录任何凭据或 Key。AI 使用本机 Claude Code CLI（用户明确要求，替代原需求中的 OpenAI 兼容配置）。
- 不得为了让测试通过而硬编码测试输入。验收用例只能用来检验结果（`AcceptanceCases.cs`），搜索代码里不能有特判。
- 没跑过的测试不能说通过。占位实现不能当成完成。部署受阻时如实说明。
- 需要管理员权限、改系统设置（包括开发者模式）、做不可逆操作之前，先征得用户同意。
- 提交只署名 Yu Wen（用户 2026-11-23 要求 GitHub 上只显示这一个贡献者）：提交信息里不加 `Co-Authored-By` 等署名行，否则 GitHub 会把 Claude 也列为贡献者。

## 踩过的坑

- **Claude 桌面版是 MSIX 应用，它启动的 shell（也就是 Claude Code 的工具进程）对 `%LOCALAPPDATA%` 的写入会被重定向**到 `%LOCALAPPDATA%\Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\…`。
  - 受影响的有：本机词典、原始数据、install.ps1 解压的安装目录、.NET SDK（`%LOCALAPPDATA%\Microsoft\dotnet`）。这个容器里的进程看得到这些文件；CmdPal、AppX 部署服务、普通终端都看不到。所以从这里 `Add-AppxPackage` 注册 AppData 下的目录会报 0x80070003。
  - `GetCurrentPackageFullName` 在这里报告「无包身份」，所以检测要用探针：`DataPaths.DetectAppDataRedirection` 和 `common.ps1` 里的 `Get-AppDataRedirection`。
  - 仓库目录（`C:\workspace\…`）不受影响：Debug 布局可以从这里注册，`artifacts\` 里的产物也是真实文件。
  - 在容器外起进程（如 WMI）会被自动模式拦截。需要写真实 AppData 的步骤（`install.ps1`、`import-data.ps1`）只能请用户在普通终端运行。
  - 扩展支持从 `程序目录\Data\lexi.db` 读取词典，Debug 布局验证时可以把词典复制到那里。
  - 新词典在这里只能构建到仓库里（`lexi import --out artifacts\data\lexi.db`），再请用户在普通终端运行 `install.ps1 -SkipBuild -Database artifacts\data\lexi.db` 装到 `%LOCALAPPDATA%`。测试、自检要用新库时设置 `$env:LEXI_DB`（`self-test.ps1` 会继承）。
- **CmdPal 失去焦点会自动收起**：窗口是用 DWM cloak 隐身的（`IsWindowVisible` 仍为 true），隐身后 UIA 读不到任何内容。用户正在操作其他窗口时，后台进程也抢不到前台。做 UI 自动化验证前，要请用户暂时别动电脑。
  - `verify7.ps1` 开头用 `GetLastInputInfo` 等鼠标键盘空闲 8 秒再开始（最多等 2 分钟）。第一次运行时用户还在用 Chrome，窗口一弹出就被收起，12 步全部因为找不到窗口而失败；加了这段等待后一次通过。
- CmdPal 的搜索框 AutomationId 是 `MainSearchBox`。`x-cmdpal://settings` 打开设置，其他 `x-cmdpal://…` 地址会唤出主窗口。验证脚本在 `artifacts\cmdpal-verify\`（`uia.ps1`、`verify*.ps1`，不提交）。
  - **验证输入要用真实按键**：`verify`～`verify8` 用 UIA `ValuePattern.SetValue` 写搜索框，不需要键盘焦点，所以热键、焦点类问题一直没测出来。`verify9.ps1`（`verify9-lib.ps1`）用 `keybd_event` 按 Alt+T / Alt+Space、逐字母输入，记录焦点所在控件和前台窗口；只在 CmdPal 是前台窗口时才打字，不会打进别的程序。隐藏状态下窗口是 DWM cloak 的，判断「已显示」要查 `DWMWA_CLOAKED`。
  - **含中文的 .ps1 必须带 UTF-8 BOM**，否则 PowerShell 5.1 会按 GBK 解析，直接报语法错误。
  - **在根页面按 `Alt+←` 会收起窗口**（NavigateBack 发现没有上一页时会触发 Dismiss）。回主页要用 `Alt+Home`。
  - 通过上下文命令快捷键（如 Ctrl+I）打开的页面拿不到键盘焦点，Esc 无效。这是宿主的问题，从 Ctrl+K 菜单或回车打开则正常。
  - 设置窗口里的扩展列表是虚拟化的，要先用「搜索扩展」框过滤。不要在设置窗口里批量点击，那里的开关改的是用户自己的配置。
- 同一个包反复注册（Debug 和 Release 交替）后，CmdPal 设置里会出现多个 Lexi 条目，需要「Reload」清理。
- **重装扩展后，CmdPal 宿主进程还拿着旧扩展进程的对象**。Reload 不会重启宿主（2026-10-21 宿主从 16:32 起一直在跑，中间重装过两次）。
  - 现象：新结果偶尔没有详情面板，Ctrl+K 只剩主命令，Ctrl+P 没反应。
  - 宿主日志 `%LOCALAPPDATA%\Microsoft\PowerToys\CmdPal\Logs\0.100.2.0\Log_<日期>.log` 里有：
    - `SafeSlowInit` … `EventSourceCache` / `IWeakReference.Resolve` 报 `0x800706BA`（RPC 服务器不可用）；
    - 旧 fallback 的 `get_FallbackHandler` 也报同样的错。
  - 是偶发的。界面验证失败时先看这个日志，不要急着改扩展代码：重跑，或者请用户完全退出再启动 CmdPal。
  - 只装一次的普通用户不受影响。
  - 旧对象会一直留在宿主的顶层列表里，热键也可能解析到它们（见下一条）。`install.ps1` 更新已有安装时，如果 CmdPal 在运行，会提示完全退出再启动（托盘图标 → 退出），Reload 不够。
- **fallback 打开的页面不能用顶层命令的 Id**（2026-10-22，用户反馈「Alt+T 唤出后无法直接输入单词」）：
  - 用户在 CmdPal 设置里给 Lexi 绑了 Alt+T，`settings.json` 的 `CommandHotkeys` 存的是 `CommandId = com.lexi.dictionary`。宿主按这个 Id 在顶层列表里取**第一个**匹配项（`TopLevelCommandManager.LookupCommand`）。
  - 宿主 0.100.2 识别 fallback 用的是它**当前命令**的 Id：扩展确实暴露了 `IFallbackCommandItem2.Id`（`com.lexi.fallback`，自检里 QI 成功），但宿主没用上，`settings.json` 里从来没有这个 Id，只有 `com.lexi.dictionary` 和哈希生成的 Id。
  - 原来 fallback 打开的 `DictionaryPage` 也叫 `com.lexi.dictionary`，所以主页「回退」组里的 Lexi 结果行也挂着「Alt + T」（`UpdateHotkey` 匹配后不会清除）。重装留下的旧 fallback 把这个 Id 缓存住了；按 Alt+T 时热键解析到旧进程里的死对象，宿主先回主页、显示窗口，导航静默失败，窗口停在主页，输入的字进了主页搜索（宿主日志里主页收到 `sdf`、`sdfas`）。
  - 现在顶层页面保持 `DictionaryPage.CommandId`（用户的热键和别名存在这个 Id 下，不能改），fallback 打开的页面用 `DictionaryPage.LookupId`（`com.lexi.lookup`）。自检「fallback: pages keep off the Lexi command ID」检查这一点，把页面 Id 改回去时它会失败。
- **Alt+T 后面板出来了，字却打进了 Chrome**（2026-10-23，用户反馈）：
  - 宿主的 `MainWindow.ShowHwnd` 只调一次 `SetForegroundWindow`，失败了也不补救。Windows 不允许时，面板照样显示在最上层，但没有被激活：收不到按键，也不会因为失焦而收起，字进了原来的前台程序。宿主日志里什么都不记。
  - 触发条件（`verify10c.ps1`，两轮都复现）：上一次离开面板时**点回了别的窗口**（失焦自动收起），下一次 Alt+T 就失败；用 Esc 或 Alt+Space 收起时下一次正常。失败状态会一直保持，直到面板被别的方式激活一次：`verify10.ps1 -TypeAfter` 连续 7 次 Alt+T 都失败，`verify10b.ps1` 从外部激活一次之后又正常了。
  - 从另一个进程直接 `SetForegroundWindow` 也会被拒。先 `SendInput` 一个空鼠标事件（不移动、不按键），让本进程成为最后的输入来源，再调用就能成功（`verify10b.ps1` 用一个没按过键的进程验证；PowerToys Run 对自己的窗口也是这么做的）。
  - 现在由 `Services\PaletteFocus` 处理：顶层页面被打开时（宿主读 `SearchText`），在 1.5 秒内检查；如果面板显示着、前台却是别的程序，就用上面的办法激活面板；前台是面板或其他 CmdPal 窗口时什么都不做。只在 COM 服务器模式下启用，自检不会碰真实的面板。认面板窗口靠：CmdPal 进程、`WinUIDesktopWin32WindowClass`、可见且没有 DWM cloak、启用（toast 是禁用的）、有可调边框（Dock 没有）、工具窗口（设置窗口不是）；找到不止一个就不动。`SendInput` 的 `INPUT` 必须是 40 字节，否则调用被拒，自检检查这一点。
  - PowerToys 0.102 的宿主这段代码没变，升级解决不了。
  - 只有绑定到命令的热键会这样：点回别的窗口之后按 Alt+Space，CmdPal 自己就能拿到焦点（`verify10c.ps1 -SecondAltSpace`，2/2）。
  - 新版实测：点回别的窗口之后按 Alt+T，面板约 75 ms 出现，11 次里每次都被 Lexi 激活，焦点在 `MainSearchBox`，探针窗口一个字也没收到（`verify10c.ps1`，T 按住 60–600 ms）。补救完成的时刻是松开 Alt 后 1–3 ms，或者在一直按住按键时的约 215–240 ms。
  - 宿主处理绑定到命令的热键时，显示窗口前会先在 UI 线程上同步调用扩展（QueryInterface 判断命令类型、`ResetToHome`）。扩展进程被暂停 300–400 ms 时，面板就晚这么久才出现（`verify10.ps1 -SlowMs`），但这本身不会让焦点丢失。
  - 宿主的低级键盘钩子（`CmdPalKeyboardService`）装在 UI 线程上。UI 线程忙着处理热键的那段时间（约到按下后 0.3 秒），所有按键都在钩子里排队，`keybd_event` 也要等到那时才返回。所以脚本记录的按键时间是按键处理完的时间：T 只按 30 ms 时，松开 Alt 被记在约 300 ms。
  - **用 Alt+T 唤出后马上打字，首字母可能被清掉**，这和焦点补救无关：页面加载完成时，宿主的 `SearchBar.Page_PropertyChanged` 用页面的 `SearchText` 覆盖输入框，并按 `InitialSearchText` 全选，在这之前打的字只在宿主的输入框里，扩展收不到。实测：松开 Alt 后 0–20 ms 内按下的首字母在两种状态下都会丢（`ouse`，11/11），50 ms 以后不丢；用 Alt+Space 重新显示已打开的 Lexi 页面（不重新加载）时，松开 Alt 后 2 ms 打字也不丢（4/4）。写在 README「已知限制」里。验证脚本要在松开 Alt 后至少 100 ms 再打字。
  - 10-22 那次失败伴随宿主日志 `Error giving foreground rights: 0x80070005`（`PowerToysRootPageService.SetActiveExtension` 调 `CoAllowSetForegroundWindow`）；10-23 复现的失败没有这条日志，不能靠它判断。
  - UI 自动化（`artifacts\cmdpal-verify\verify10*.ps1`）：用自己的探针窗口代替 Chrome。热键只在探针在前台时按，字母只在探针或 CmdPal 在前台时打。探针窗口必须放在独立的 STA 线程上：在脚本线程里用 `DoEvents` 时，一个落到探针上的 Alt 让它进入系统菜单的模态循环，脚本卡了 4 分钟，直到用户点了别的窗口。
  - 用 `powershell -File` 传数组参数时 `-TypeAfter 0,100,250` 会被当成一个数（100250），要用 `-Command "& 'script' -TypeAfter @(0,100,250)"`。
- **不要用 Windows PowerShell 5.1 的 `Get-Content`/`Set-Content` 改源码**：它默认按 GBK 读写，会把中文变成乱码（曾经损坏过 3 个文件）。改源码用 Edit/Write 工具，或者 Git Bash 的 `sed`（按字节处理，安全）。
- `Lexi.Extension` 必须指定平台（`-p:Platform=x64`）。项目文件里已经把 AnyCPU 映射到 x64。
- `CsWinRTWindowsMetadata` 指向 NuGet 里的 winmd。不设置的话，CsWinRT 会去注册表找 Windows SDK，然后报错。
- 缺少 `Microsoft.Windows.SDK.BuildTools` 会报 APPX3217 `UAP.props`。资源清单默认语言要显式写成 `<DefaultLanguage>en-US</DefaultLanguage>`，否则会取构建机的 UI 语言。
- 资源文件（`Assets\*.png`）要设置 `CopyToOutputDirectory`，构建输出目录才能直接当作布局注册。
- **工具包的 `CommandItem.Title` 为空时会回退到 `Command.Name`**。想隐藏 fallback，必须同时把 `Command` 换回无名的 `NoOpCommand`。
- **列表分组标题要用没有命令的列表项**（工具包的 `Separator(title)`）。宿主 0.100.2 不显示普通项的 `ListItem.Section`（`ListItemViewModel.EvaluateType`），所以首页的「最近查询 / 提示」一直没有标题，直到 2026-10-21 在界面里查中文反查时才发现。现在由 `DictionaryPage.WithSectionHeaders` 在分组变化处插入 `Separator`。
- 刚 Reload 之后（扩展进程刚重启的十几秒内），在主页对 Lexi 按 Enter 可能没有反应，或者页面过几秒才出来。UI 自动化要先确认真的进入了 Lexi 页面，再开始检查：搜索框的 Name 就是占位文字，最多等 4 秒，不行就回主页重试（`verify5.ps1` 的 `Open-LexiChecked`）。
- 宿主同步调用 fallback 的 `UpdateQuery`（专用线程，慢阈值 200 ms），这里只能做低成本工作。主页每次按键都会创建一个 `DictionaryPage`，页面要等第一次 `GetItems()` 才搜索。
- `DictionaryPage.SearchText` 的 getter 带副作用：顶层页面超过 1 秒没有输入时，读取会重置回首页（用来实现「每次打开都从首页开始」）。测试或驱动代码不要随便读它。
- 从 Claude Code 会话里启动子进程 `claude` 时，会继承 `CLAUDECODE`、`CLAUDE_CODE_SIMPLE` 等环境变量，进入 bare 模式后 OAuth 失效。`UserEnvironment.Apply` 会从注册表重建环境变量并移除这些变量。
- PowerShell 5.1 里没有 `ProcessStartInfo.ArgumentList`，只能用 `.Arguments`。
- 刚解压或刚构建的二进制第一次运行很慢（约 600 ms，Defender 扫描 / 首次加载）。性能要看第二次运行。
- Wikimedia 偶尔对单个请求返回瞬时错误。`Pronouncer` 会重试一次，并把原因记录在 `LastDownloadError`。
- **Wikimedia 对不带 `Accept` 头的请求返回 429**（「does not comply with our robot policy」）。HttpClient 默认不带这个头。
  - 前端缓存里已有的文件照常返回 200，所以看起来像偶发故障。2026-10-21 自检里录音全部 429，用 curl 却能下载，对比请求头才发现。
  - 另外，连续第三个未缓存的请求会收到 429，`Retry-After: 1`。
  - `Pronouncer` 现在带 `Accept` 头，一次只下载一个文件，429 后按 `Retry-After` 暂停（`Pronouncer.CommonsPause`），不立即重试。
  - 用 curl 排查时要选没缓存过的文件，否则看不出差别。
- **Kaikki 的 `sounds` 不保证英式在前**：water 先列了十条美式和方言记音，RP 在第 11 条。导入只保留 6 条音标、4 条录音，必须先按口音排序再截取。
  - 很多美式记音只有 `cot-caught-merger` 标签或 `without the cot–caught merger` 备注（注意是 en dash）。
  - 英/美通用的记音同时带 `Received-Pronunciation` 和 `General-American`，记为 `UK/US`。
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
  - 发音：当时用户要求先不播放声音，自检在不出声的情况下验证了录音下载和语音合成。Phase 8 已在界面里出声验证；
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

### Phase 6 — 中文反查（中 → 英/德）✅（2026-10-21 在 CmdPal 界面实测，含分组标题）

- 需求：输入中文，显示对应的英语和德语词。
- 数据（都来自已在用的 ECDICT 和 Wiktionary 文件，许可不变，见 `docs/DATA_SOURCES.md` §2.1）：
  - ECDICT 中文释义；
  - 英文版 Wiktionary 英语词条按义项的汉语译词（新导入到 `Sense.Zh`，词条页显示为「按义项的对应词」）；
  - 德文版 Wiktionary 翻译表；中文版 Wiktionary 的德语释义；
  - 中文版 Wiktionary 中文词条的英/德翻译表。导入时挂到已有词头上，成为 `Kind = "reverse"` 的词条，共 44,010 条对应、27,324 个词头。
- 索引：`zh_index(term, word_id, weight)`，导入最后一步生成（`ZhIndexBuilder`）。完整库有 758,613 个中文词语、1,381,180 条对应关系，生成约 30 秒；整个导入 9.9 分钟。
  - 权重 = 释义中的行/位置，乘以来源系数（`[网络]` 0.55，名字音译 0.3，翻译表 0.95，反向 0.9，`…的` 词干 0.95）；每多一个来源给出同一对应 +6。
  - 带占位符的条目（`吃...的人`）跳过。
- 搜索（`SearchChinese`）：
  - 繁体转简体；整词查找；查不到时找最长的已知子串（`ChinesePart`）；
  - 英、德分组，各占一半名额；
  - 排序：权重 + Zipf×5；单个字母 −30，缩写 −6；
  - 屈折形式的重复项（threats/threat）在原形分数不低于它减 5 时隐藏。
- 界面：
  - `DictionaryPage.Chinese.cs`：「英语 English」「德语 Deutsch」两组；
  - 可选的 AI 项（新任务 `TranslateChinese`）：词语放在最后，句子或部分匹配时放在最前；
  - 旧词典没有中文索引时提示更新；
  - fallback 支持 `dict 房子` →「房子 → house · das Haus」（不加前缀时只对 ≥2 字的词语）；
  - 详情和词条页注明「中文反查：「X」→ word」。
- 验证：
  - 单元测试与导入测试：186 个全部通过（用新库）；
  - `lexi quality`：新库 34/34（新增 19 个中文用例，按所属语言组内名次计）；旧库 15 通过、19 跳过；
  - 自检：Debug 59/59，裁剪后的 Release 包 59/59（`LEXI_DB` 指向新库）；旧库 37 通过、20 个警告（中文用例跳过，并验证了更新提示）；
  - `lexi ai 威胁` 真实调用一次，6.9 秒，英/德分节；
  - 中文查询 0.5–9 ms，fallback 3–6 ms。
- 排序上的已知不足：同样合格的译法里，冷僻但释义只有这一个词的词，可能排在常用词前面。例如 `弱点` 的 weakness 排第 3，`威胁` 的 threat 排第 4，都在验收阈值内。ECDICT 的个别错误数据（doobie = 数据库）会照常出现。
- 新词典在 `artifacts\data\lexi.db`（529 MB，不提交）。
- 界面实测（用户用绝对路径运行 `install.ps1 -SkipBuild -Database …` 并 Reload 之后；`artifacts\cmdpal-verify\verify5.ps1`，截图 40–45）：
  - 防火墙 → firewall 排第一，德语有 der/die Firewall，副标题带「「防火墙」 ·」（ECDICT 摘要里是错字「放火墙」）；
  - 電腦 → computer；
  - 网络安全漏洞：AI 项在最前，cybersecurity 和其他结果带「部分匹配」；句子 `我想学习德语。`：AI 项在最前；
  - Deutsch 筛选下只剩 der Apfel；
  - 词条页标题下方显示「中文反查：「房子」→ house」，三种中文块都在；
  - 主页 `dict 房子`、不加前缀的 `苹果` 都在「回退」组里出现。
- 界面实测发现的问题：结果有正确的先后顺序，但没有「英语 / 德语」分组标题，原因见「踩过的坑」。已修复，Debug 和 Release 包的自检都通过了（59/59，标题为 英语 English / 德语 Deutsch / AI 翻译）。
- 用户再装一次（15:22 的包）后复查：
  - 首页标题「最近查询 / 提示」出现；
  - `房子` 依次是「英语 English」house、bure、dhar，「德语 Deutsch」das Haus、hauseigen，「AI 翻译」；
  - 句子是「AI 翻译」在最前；
  - 截图 40、43、47、48。
- 列表是虚拟化的，第一屏以外的行不在 UIA 树里。英语结果多时（電腦、学习，各 10 条），德语组在第一屏以下，要往下翻（`verify6.ps1` 用 ↓ 键翻页后确认了 der Computer、lernen）。
- 按用户决定，词典页每种语言最多 6 条（AI 项在最前时 4 条，按语言筛选时 12 条），让德语组出现在第一屏。界面里一屏约 8 行：标题约 40 px，结果行 66 px。`lexi quality` 和测试直接调用搜索引擎（Limit 20），不受影响。
- 用户装上 15:32 的包后，在界面里确认（`verify5.ps1` 12/12，不翻页）：
  - 電腦：6 条英语之后，「德语 Deutsch」和 der Computer 在第一屏（截图 48）；
  - 句子：「AI 翻译」、4 条英语之后，「德语 Deutsch」和 lernen 在第一屏（截图 43）。
- 第一次让用户安装时，脚本按当前目录解析 `artifacts\data\lexi.db`；用户不在仓库目录运行，脚本在第一步就停了，什么都没装。`-Database` 现在也会到仓库根目录下找。

### Phase 7 — 翻译：AI 和联网两种方式，两个方向都有 ✅（2026-10-21 在 CmdPal 界面实测）

- 需求（用户 2026-10-21）：既能用 AI 翻译，也能联网翻译。英/德句子恢复 Claude 的翻译项，中文输入加 Google 翻译成英语和德语的项。使用 Google 非官方接口是用户的决定，见「规矩」里的例外。
- 做了什么：
  - `GoogleTranslator.TranslateAsync` 增加目标语言参数（只允许 `zh-CN`、`en`、`de`），缓存键带上目标语言；`WebUrl` 生成对应的 Google 翻译网页地址。
  - `TranslationResultPage` 支持多个目标语言，按顺序请求。中文输入时同一页显示英语和德语两段译文，每种语言各有复制命令（「复制英语译文」「复制德语译文」）和网页链接；某个语言失败时只在它的位置显示错误。只有一个目标语言时，输出格式不变。
  - 中文输入（`DictionaryPage.Chinese.cs`）：「翻译」组里先放「Google 翻译成英语和德语」（在线翻译开关打开时），再放 AI 项（AI 开着时）。词语放在词典结果之后，句子或部分匹配放在最前。
    - 原来的「AI 翻译」组改名为「翻译」。
    - 每个排在前面的翻译项占一行，标题也占一行，英语结果相应减少，保证德语组仍在第一屏：只有 AI 时 4 条，两项都有时 3 条。
  - 英/德句子：词典页和独立的「Lexi 句子翻译」页都先列 Google「翻译这句话」（开关打开时），再列 Claude「AI 翻译这句话」（AI 开着时，`TranslateSentence`）。
  - 设置里的开关改名为「启用 Google 在线翻译」，说明两个方向和非官方接口。保存时的 ID 仍是 `ai.sentenceTranslation`，已有的设置不受影响。
- 验证：
  - 单元测试 188 个全部通过，新增目标语言和非法语言两项；
  - 自检 61/61（新增「Google 和 Claude 并列」一步，英/德句子的两项也有检查）；
  - 加 `-Translation` 的联网自检 63/63：`我想学习德语。` → "I want to learn German." | "Ich möchte Deutsch lernen."（1.6 秒）；英/德 → 中照常。
- 界面实测：用户用 `install.ps1 -SkipBuild` 装上 15:47 的包（已安装的 DLL 与包内哈希一致），在设置里打开在线翻译并 Reload 之后运行 `artifacts\cmdpal-verify\verify7.ps1`，12/12 通过，用时 65 秒，截图 50–58：
  - 首页提示有「在线翻译（Google）」，AI 提示里有「输入句子或中文可让 AI 翻译」；
  - `房子`：英语 house / bure / dhar，德语 das Haus / hauseigen，「翻译」组依次是 Google、「AI 讲解「房子」的英语和德语说法」（截图 51）；
  - `我想学习德语。`：最前面是「翻译」组（Google、AI），之后是英语 study / learn / German，「德语 Deutsch」组（lernen、das Deutsch）仍在第一屏（截图 52）；
  - Google 结果页显示「中文 → 英语」I want to learn German.、「中文 → 德语」Ich möchte Deutsch lernen.；Enter 复制英语译文，Ctrl+Enter 复制德语译文（截图 53）；
  - AI 结果页（Claude Code，用户配置的模型）20 秒，英、德分节并带要点讲解（截图 54）；
  - 英语句子：先「翻译这句话」，再「AI 翻译这句话」（截图 55）；Google 译成「今天天气真好」；AI 用了 4 秒（截图 56、57）；
  - 德语句子和独立的「Lexi 句子翻译」页里，这两项的顺序也一样（截图 58）。

### Phase 8 — 发音：英式优先 ✅（2026-10-21 在 CmdPal 界面实测，出声）

- 需求（用户 2026-10-21）：「可以加入发音功能了，优先英式发音」。之前的「先不播放声音」不再适用。
- 做了什么：
  - `PronunciationPlan`（`Lexi.Dictionary/View`）决定顺序：
    - 英语：英式录音（含英/美通用）→ en-GB 语音 → 其他口音录音 → en-US（或任一 en-*）语音；
    - 显式「英式发音」「美式发音」只用该口音；
    - 德语：DE → AT → CH 录音，然后是德语语音。
  - `Pronouncer` 按顺序执行。提示里写明口音、录音还是合成，以及退而求其次的原因；没装英式语音时附上添加方法。
    - 命令名称：`发音（英式优先）`（Ctrl+P）、`英式发音`、`美式发音`。详情面板底部写「Ctrl+P：发音（英式优先）」。
  - 导入：`KaikkiImporter.EnglishRegion` 依次看标签、备注、文件名，识别出 `UK/US`，`cot–caught` 归为美式。先按口音排序再截取（见「踩过的坑」）。
  - 词条视图：音标和录音英式在前；详情表头为「英 … · 美 …」，英/美通用时只显示一条；地区标签加上 加/新西兰/爱尔兰/苏格兰/印度。
  - 下载遵守 Wikimedia 机器人策略：带 `Accept` 头，一次下载一个文件，429 时按 `Retry-After` 暂停。
- 验证：
  - 单元测试 220 个全部通过。新增 32 个：口音识别 22 例、发音顺序 7 例、音标排序和表头 2 例，以及导入夹具里的 water（按原文顺序 RP 排在第 11 条）；
  - `lexi quality` 34/34；新词典上 water 的表头是「英 /ˈwɔː.tə/ · 美 /ˈwɔ.tɚ/」（原来没有英式），house 是「英/美 /haʊ̯s/」；
  - 自检 Debug 和裁剪后的 Release 包加 `-Network` 都是 64/64（不加 `-Network` 时少下载录音那一步）。不出声的完整 Ctrl+P 流程：
    - house →「🔊 英式真人录音（Wikimedia Commons：En-uk-house.ogg）」；
    - weakness →「🔊 美式真人录音（…；没有英式录音，也未安装英式语音（…添加「英语(英国)」））」；
    - weakness 的「英式发音」→「没有英式发音：…」。
  - 本机只有 en-US、de-DE、zh-CN 语音，没有 en-GB，所以没有英式录音的词会退到美式录音。
- 新词典：`artifacts\data\lexi.db`（20:14 重新导入，用时 11.1 分钟，口音排序后截取，词条数不变）。用户当前装的那一版（14:56）保存为 `lexi-prev.db`。
- 界面实测（`artifacts\cmdpal-verify\verify8.ps1`，9/9，截图 60–66）。用户第二次运行 `install.ps1 -SkipBuild -Database <绝对路径>` 并 Reload 后：已安装的 DLL 与 20:15 的包哈希一致，词典是 20:14 的新库。结果：
  - house：表头只有「英/美 /haʊ̯s/」，底部「Ctrl+P：发音（英式优先）」；Ctrl+P 播放并提示「🔊 英式真人录音（Wikimedia Commons：En-uk-house.ogg）」（截图 60、61）；
  - weakness：表头「英 /ˈwiːk.nɪs/ · 美 /ˈwik.nɪs/」；Ctrl+P 提示「🔊 美式真人录音（…；没有英式录音，也未安装英式语音（…添加「英语(英国)」））」，两行显示完整（截图 62）；
  - Ctrl+K 菜单依次是 查看完整词条、复制释义、发音（英式优先）Ctrl+P、复制单词、英式发音、美式发音、AI 三项、Wiktionary（截图 63）；
  - 选「英式发音」后显示黄色警告「没有英式发音：…」（截图 64）；
  - water：表头「英 /ˈwɔː.tə/ · 美 /ˈwɔ.tɚ/」，Ctrl+P 播放 En-uk-water.ogg（截图 65）；Haus：「🔊 真人录音（…De-Haus.ogg）」（截图 66）；
  - 包的 LocalState\audio-cache 里有 4 个缓存的录音。
- 第一次让用户安装时，界面里什么都没变（DLL 哈希不同、词典还是 14:56 的、最后一次注册在 15:48、扩展进程从 16:33 一直在跑）。原因是脚本没有真正运行。用户在开始菜单打开的 PowerShell 里重新运行后才装上。以后验证前先比对哈希。
- 装上后前两次运行有失败，都不是发音的问题：
  - 第一次：扩展刚重启约 30 秒，进不了 Lexi 页面；
  - 第二次：weakness 那一行没有详情，Ctrl+K 只有主命令（宿主的问题，见「踩过的坑」）。
  - 第三次全部通过。

### 修复：Alt+T 唤出后无法直接输入单词 ✅（2026-10-22 在 CmdPal 界面实测，真实按键）

- 现象（用户）：按自己绑定的 Alt+T 唤出后，不能直接输入单词查询。
- 复现（`verify9.ps1`，真实按键）：Alt+T 后窗口出现、到了前台、焦点也在搜索框，但显示的是主页（占位文字「搜索应用、文件和命令...」），输入的字进了主页搜索。对照组 Alt+Space 正常；从主页列表回车进入 Lexi 后真实输入也正常（`verify9b.ps1`）。
- 原因和修复见「踩过的坑」：fallback 页面和顶层命令共用 Id，热键解析到了重装后残留的旧 fallback。现在 fallback 页面用 `com.lexi.lookup`。另外 `install.ps1` 更新时提示重启 CmdPal。
- 验证：单元测试 220/220；自检 Debug 和裁剪后的 Release 包都是 64/64（新增一步，不加 `-Network`）；页面 Id 临时改回去时新增的这一步失败（63/64）。
- 界面实测（用户 18:46 用 `install.ps1 -SkipBuild` 装上新包，安装目录的 DLL 与包哈希一致；完全退出并重启 CmdPal 后，宿主日志没有 RPC 错误）：
  - `verify9.ps1`：Alt+T 后 0.4、1.5、3 秒再输入，三次都直接进入 Lexi 页面（Lexi 的占位文字、空搜索框），焦点在搜索框，house / water / haus 都能输入并出结果；
  - `verify9b.ps1`：主页搜 lexi，只有顶层「Lexi」一行带「Alt + T」，回退组的 Lexi 结果行不再带；从列表回车进入后真实输入正常；
  - `verify9c.ps1`：先在主页输入 house，让回退条目持有预填页面，收起后按 Alt+T，打开的是空搜索框的 Lexi 首页，输入 water 正常。
- `settings.json` 里仍然没有 `com.lexi.fallback` 和 `com.lexi.lookup`（宿主连接时 fallback 空闲，Id 是已有的哈希）。用户的 Alt+T 绑定（`com.lexi.dictionary`）不变。新 Id 不在 `FallbackRanks` 里，得分是最低的 1，Lexi 的回退结果和以前一样排在「回退」组末尾。

### 修复：Alt+T 后面板没拿到焦点，字打进 Chrome ✅（2026-10-23 在 CmdPal 界面实测，真实按键）

- 现象（用户）：按 Alt+T 后，输入有时不在 PowerToys 里，而是进了别的窗口（比如 Chrome）。
- 原因、触发条件和修复见「踩过的坑」。改动：`Services\PaletteFocus.cs`（新增）；`DictionaryPage.SearchText` 和 `SentenceTranslationPage.SearchText` 的 getter 调用 `PaletteFocus.OnPageOpened()`；`Program` 在 COM 服务器模式下启用；`Lexi.Extension.csproj` 打开 `AllowUnsafeBlocks`（LibraryImport）。
- 复现（当前安装的 10-22 版本，`artifacts\cmdpal-verify\`）：
  - `verify10.ps1`（before、slow-before）：Alt+T 每次都成功（这两轮里面板每次都是用 Alt+Space 收起的），按下 T 后约 3 ms 面板出来，同时拿到前台；
  - `verify10.ps1 -TypeAfter`（type-before）：被上一轮中断后进入失败状态，7 次 Alt+T 面板都显示了但前台一直是探针，`house` 全部打进探针；
  - `verify10b.ps1`（poc）：失败状态下，没按过键的进程直接 `SetForegroundWindow` 失败，`water` 进了探针；先发空鼠标事件再调用成功，焦点在 `MainSearchBox`，`water` 进了 Lexi；
  - `verify10c.ps1`（dismiss-before）：离开面板后再按 Alt+T，Alt+Space 和 Esc 离开 4/4 正常，点回别的窗口离开 2/2 失败。
- 验证：单元测试 236/236，自检 Debug 和裁剪后的 Release 包都是 65/65（新增「hotkey: palette focus check」：自检时检查是关闭的，`INPUT` 40 字节，能找到 CmdPal 进程并判断面板状态）。当时工作区里还有另一批未提交的性能优化（带 16 个测试），所以测试数比 220 多。单独提交前，把暂存区导出到 `artifacts\commit-check`（不含那批优化）重新构建：单元测试 220/220，自检 Debug 65/65。
- 新包：`artifacts\msix\Lexi.Extension_0.1.0.0_x64_Test\Lexi.Extension_0.1.0.0_x64.msix`（15:18，包内 `Lexi.Extension.dll` 的 SHA-256 以 `3DE58743` 开头）。它是用当时的整个工作区构建的，所以也包含那批性能优化（这次没有提交）。
- 界面实测：用户 15:29 用 `install.ps1 -SkipBuild` 装上新包（安装目录的 DLL 与包哈希一致），并完全退出、重启了 CmdPal（宿主日志没有 RPC 错误）。之后：
  - `verify10c.ps1`（dismiss-after）：Alt+Space、Esc、点回别的窗口三种离开方式各 2 轮，再按 Alt+T 并在松开 Alt 后 100 ms 打字，6/6 全部进了 Lexi。点回别的窗口的那 2 次，CmdPal 自己没拿到焦点，前台在约 190–215 ms 时切到面板（Lexi 补救）；
  - hold600-after：T 按住 600 ms，补救在 215 / 237 ms 完成，早于松开 T，不依赖松开按键；
  - altspace-after：点回别的窗口之后按 Alt+Space，CmdPal 自己拿到焦点；
  - fast-after、fast-normal、type20/50-after、fast-altspace：首字母问题见「踩过的坑」；
  - `verify9b.ps1`（list-focusfix）：从主页列表回车进入 Lexi，打 `house` 正常，只有顶层 Lexi 一行带「Alt + T」；
  - 宿主日志在测试期间没有新的错误或警告。

## 待办

1. 用户方便时再验证：设置里 Lexi 详情页的表单和别名（Alias `dict`，Indirect）。
2. Ctrl+I 打开的页面里 Esc 无效是宿主问题。可以考虑向 PowerToys 报告，扩展这边不需要改。
3. （可选）签名发布：`package.ps1 -Sign` 会生成自签名证书。在其他电脑上信任证书需要管理员权限，执行前先征得用户同意。
4. （可选）FreeDict deu-eng 作为用户本机可选的补充来源（GPL/AGPL，不要合并进可分发的数据库）。
5. 以后的改动在用户要求时再提交和推送（署名规则见「规矩」）。代码公开在 https://github.com/HappyOwl-Wen/lexi-cmdpal（MIT，© 2026 Yu Wen；远端 `origin`，分支 `main`，原来的 `master` 和 `feature/lexi-dictionary` 已合并成 `main`）。发布前用 `git filter-branch` 去掉了旧提交里的 `Co-Authored-By` 行，提交哈希因此都和以前不同。
