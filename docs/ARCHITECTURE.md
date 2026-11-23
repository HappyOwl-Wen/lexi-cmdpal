# Lexi 架构说明

## 1. 总览

```
 Command Palette（PowerToys 进程）
        │  COM / WinRT（进程外）
        ▼
 Lexi.Extension.exe（MSIX 包，-RegisterProcessAsComServer）
   LexiExtension ─► LexiCommandsProvider
                      ├─ 顶层命令 "Lexi" ─► DictionaryPage (DynamicListPage)
                      │                      ├─ WordListItem ─► Details（选中时才构建）
                      │                      │                └► EntryPage (ContentPage，完整词条)
                      │                      └─ AiPage (ContentPage，流式 AI 回答)
                      ├─ Fallback "Lexi 查词" ─► LookupFallbackItem（主页一行释义）
                      └─ Settings（JsonSettingsManager → CmdPal 原生设置表单）
   ServiceHub：DictionaryService · HistoryStore · Pronouncer · AiService · LexiSettings
        │
        ├─ Lexi.Dictionary：LexiDatabase（SQLite 只读） · WordViewBuilder · MarkdownRenderer · AiContext
        ├─ Lexi.Core：TextNormalizer · SearchEngine · FuzzyIndex（DAWG） · FormDescriber
        └─ Lexi.AI：AiService（串行、限速、缓存） · ClaudeCodeProvider（claude -p） · PromptTemplates

 lexi（Lexi.Cli）：download / import / query / show / quality / bench / stats / ai
```

| 项目 | 目标框架 | 职责 | 依赖 |
|---|---|---|---|
| `Lexi.Core` | net10.0 | 文本规范化、搜索引擎、模糊索引、词形描述、数据模型 | 无 |
| `Lexi.Dictionary` | net10.0 | SQLite 存储、导入流水线、词条聚合与 Markdown 渲染、验收用例 | Core、Microsoft.Data.Sqlite |
| `Lexi.AI` | net10.0 | AI 请求模型、提示词模板、Claude Code 提供者、缓存与限速 | 无（只调用外部进程） |
| `Lexi.Translation` | net10.0 | 在线翻译（Google 公共端点；英/德 → 中、中 → 英/德）、超时、取消、内存缓存 | 无（HttpClient） |
| `Lexi.Extension` | net10.0-windows10.0.26100.0 | CmdPal 扩展：COM 服务器、页面、命令、设置、发音 | 以上三者、CmdPal SDK、CsWinRT、WinRTServer |
| `Lexi.Cli` | net10.0 | 数据构建与诊断工具 | Core、Dictionary、AI |
| `tests/Lexi.Tests` | net10.0 | xUnit v3 测试 | Core、Dictionary、AI |

`Lexi.Core`、`Lexi.Dictionary`、`Lexi.AI` 和 `Lexi.Translation` 都不依赖 UI，可以在 CLI 和测试中直接使用。扩展只是一层很薄的界面。

## 2. 扩展运行时

### 进程与生命周期

- CmdPal 通过 AppExtension `com.microsoft.commandpalette` 发现扩展。扩展以 `Lexi.Extension.exe -RegisterProcessAsComServer` 启动，COM 类的 CLSID 在 `Package.appxmanifest`（两处）和 `LexiExtension.cs` 中保持一致。宿主模式与官方 ExtensionTemplate 相同。
- CmdPal 启动时就会加载扩展，所以 `LexiCommandsProvider` 的构造必须很轻：不打开数据库，实测约 15 ms。
- `DictionaryService` 在第一次查词时打开数据库。每次使用都会重新设置一个单次计时器，空闲 10 分钟后关闭数据库并清空缓存。整个过程没有轮询。
  - 打开 Lexi 页面时（宿主读取 `SearchText`），会在后台预热模糊索引。
- `Lexi.Extension.exe --self-test` 不经过 COM，在进程内用与宿主相同的调用方式驱动这些对象，详见 §8。

### 线程模型与「新结果不被旧结果覆盖」

宿主在一个串行的后台调度器上设置 `DynamicListPage.SearchText`，然后在后台线程读取 `GetItems()`。`DictionaryPage` 的处理流程：

1. `UpdateSearchText` → `StartSearch`：取消上一次的 `CancellationTokenSource`，递增 `_generation`；
2. 后台任务先等 40 ms（去抖），再执行搜索并构建 `ListItem`；
3. `Publish(gen, items)` 只在 `gen == _generation` 时替换结果并触发 `RaiseItemsChanged`，旧查询即使后完成也会被丢弃；
4. 列表项的 `Details` 包在 `Lazy` 里。宿主只在选中某项时读取它，所以只为看到的词聚合详情，并有 64 项的 LRU 缓存。

自检的「连续输入」用例里，14 次按键只触发了 1 次结果发布，旧结果没有覆盖新结果。

### 主页 fallback

宿主为每个 fallback 开专用线程，**同步**调用 `UpdateQuery`，超过 200 ms 视为慢（见 PowerToys `FallbackUpdateManager.cs`）。因此 `LookupFallbackItem`：

- 不带 `dict`/`lexi`/`查` 前缀时，只做精确词和屈折形式查询（不做模糊搜索），实测约 1 ms；
- 带前缀时做完整搜索，但只取 3 条（实测约 6–7 ms）；
- 用代号丢弃过期的更新；
- 预填查询词的 `DictionaryPage` 不在构造时搜索，而是等宿主第一次 `GetItems()` 时再同步搜索。原因是每次按键都会创建一个新页面，而大多数页面根本不会被打开；
- 清空时同时把 `Command` 换回无名的 `NoOpCommand`。工具包的 `CommandItem.Title` 在标题为空时会回退到 `Command.Name`，否则主页会残留一个「打开词典」条目。这个 bug 是自检发现的。

### 热键唤出后的键盘焦点（`PaletteFocus`）

用绑定到命令的热键（例如用户给 Lexi 设的 `Alt+T`）唤出面板时，宿主先在 UI 线程上跨进程查询命令类型、回到主页，然后显示窗口，并只调用一次 `SetForegroundWindow`。如果上一次是用鼠标点回别的窗口离开面板的，Windows 会拒绝这次前台切换。这时面板显示在最上层，但没有被激活：按键仍然进入原来的程序，面板也不会因为失焦而收起。之后每次按热键都会这样，直到面板被别的方式激活一次。`Alt+Space`（根热键）不受影响。

`Services/PaletteFocus.cs` 的处理：

- 顶层页面（顶层的 `DictionaryPage`、`SentenceTranslationPage`）在宿主读取 `SearchText`，也就是页面被打开时，调用 `OnPageOpened()`，在后台检查 1.5 秒；
- 怎么认面板窗口：属于 CmdPal 进程、类名 `WinUIDesktopWin32WindowClass`、可见且没有被 DWM cloak、处于启用状态（toast 窗口是禁用的）、有可调边框（Dock 没有）、是工具窗口（设置窗口不是）；找到不止一个就不处理；
- 面板显示着、前台却是别的程序时：先用 `SendInput` 发一个空鼠标事件（不移动、不按键），让 Lexi 进程成为最后的输入来源，再调用 `SetForegroundWindow`，最多试 3 次。直接调用 `SetForegroundWindow` 会被拒绝。面板或其他 CmdPal 窗口在前台时什么都不做；
- 只在 COM 服务器模式下启用（`Program`），所以自检不会碰真实的面板。

实测（`artifacts/cmdpal-verify/verify10c.ps1`）：面板在按下 T 后约 75 ms 出现。CmdPal 自己拿不到焦点时，Lexi 会在按键松开前后（约 0.2–0.3 秒）把面板激活。之后打的字都进了 Lexi。

### 已在 PowerToys v0.100.2 源码中核实的宿主行为

| 行为 | 依据（`src/modules/cmdpal/…`） | 对 Lexi 的影响 |
|---|---|---|
| 别名由用户按命令配置；Indirect 别名匹配「别名+空格」，Direct 别名一输入就跳转 | `Microsoft.CmdPal.UI.ViewModels/AliasManager.cs`、`CommandAlias.cs` | Lexi 不能自己注册 `dict` 别名。README 指导用户设置 Indirect 别名；副标题里写上 `dict`，这样主页搜索 dict 也能找到 Lexi |
| fallback 的 `UpdateQuery` 在专用线程同步执行，慢阈值 200 ms | `Microsoft.CmdPal.UI.ViewModels/FallbackUpdateManager.cs` | 只做低成本查询，见上文 |
| `InitialSearchText` 在创建页面的 ViewModel 时读一次 `SearchText` | `Microsoft.CmdPal.UI.ViewModels/ListViewModel.cs` | 顶层页面借此在每次打开时回到首页（超过 1 秒没有输入才重置）；fallback 页面借此带上查询词 |
| 绑定到命令的热键：`SummonOnUiThread` 先在 UI 线程上查命令类型（跨进程 QueryInterface）并 `ResetToHome()`，再显示窗口；`ShowHwnd` 只调用一次 `SetForegroundWindow`，失败了也不补救。根热键是先显示窗口 | `Microsoft.CmdPal.UI/Pages/ShellPage.xaml.cs`、`Microsoft.CmdPal.UI/MainWindow.xaml.cs`（0.102 相同） | 扩展响应慢时面板会晚出现；前台切换被拒时由 `PaletteFocus` 补救，见上文 |
| 面板失焦就收起（`MainWindow_Activated` → `HideWindow`）；从没被激活过的面板收不到失焦，会一直显示在最上层 | `Microsoft.CmdPal.UI/MainWindow.xaml.cs` | 所以失败时面板挂在最上层，字却进了别的程序 |
| 宿主始终装着一个低级键盘钩子（`CmdPalKeyboardService`，在 UI 线程上运行），UI 线程忙的时候所有按键都要等它 | `Microsoft.CmdPal.UI/MainWindow.xaml.cs` | 热键唤出后约 0.3 秒内的按键会被推迟处理 |
| 页面加载完成时，`SearchBar` 用页面的 `SearchText` 覆盖输入框（`Page_PropertyChanged`），并因为 `InitialSearchText` 全选输入框；在这之前打的字只在宿主的输入框里，不会发给扩展 | `Microsoft.CmdPal.UI/Controls/SearchBar.xaml.cs`、`Microsoft.CmdPal.UI.ViewModels/ListViewModel.cs` | 用命令热键唤出后马上打的首字母可能丢失，扩展无法补救（README「已知限制」） |
| `TextToSuggest` 用 → 键接受 | `Microsoft.CmdPal.UI/Controls/SearchBar.xaml.cs` | 拼写纠错和变音建议可以一键填入搜索框 |
| Enter 执行主命令，Ctrl+Enter 执行次要命令（`MoreCommands` 第一项），Ctrl+K 打开命令菜单 | `Microsoft.CmdPal.UI.ViewModels/CommandBarViewModel.cs`、`Microsoft.CmdPal.UI/Pages/ShellPage.xaml.cs` | 「复制释义」放在命令列表第一位 |
| 选中项时才读取 `Details` / `MoreCommands` | `Microsoft.CmdPal.UI.ViewModels/ListItemViewModel.cs` | 详情按需构建 |
| 列表里的分组标题是**没有命令**的列表项：有命令的项一律当普通项画，它的 `Section` 不显示；无命令、`Section` 非空的项画成标题（`EvaluateType`）。工具包的 `Separator(title)` 和 `Section(title, items)` 就是这样实现的 | `Microsoft.CmdPal.UI.ViewModels/ListItemViewModel.cs`、工具包 `Separator.cs`、`Section.cs` | 结果项照常设置 `Section`，由 `DictionaryPage.WithSectionHeaders` 在分组变化处插入 `Separator`（中文反查的「英语 English / 德语 Deutsch / 翻译」、首页的「最近查询 / 提示」）。2026-10-21 在界面里发现只设 `Section` 不出标题，之后改成这种做法 |

## 3. 搜索

### 规范化（`TextNormalizer`）

| 形式 | 说明 | 例 |
|---|---|---|
| Clean | NFKC，统一引号和连字符，去掉控制字符和零宽字符（孤立代理项不会导致异常） | `Ｈäuser` → `Häuser` |
| Key | Clean 后转小写，保留变音 | `häuser` |
| Fold | 去掉变音，`ß→ss`，`æ→ae` 等（覆盖 U+0000–U+024F） | `hauser` |
| Translit | 德语式转写：`ä→ae`、`ö→oe`、`ü→ue`、`ß→ss` | `haeuser` |

数据库按 Fold 建索引，所以 `ae/ä/a` 和 `ss/ß` 的各种写法会落到同一组候选上，最后由分类和评分区分。

### 候选来源（`SearchEngine.Search`）

1. **词头**：Fold 相同的词头，按 Key 分为 Exact、Translit、Fold 三类；
2. **词形**：`forms` 表中 Fold 相同的屈折形式，指向原形，并带上词形标签（复数、Präteritum …）；
3. **反向转写**：`GermanTransliteration.ReverseVariants` 把查询中的 `ae/oe/ue` 组合还原成 `a/o/u`（最多 4 处），得到的只是候选，**还要再用真实的 Translit 校验**。所以 `Uebertragung → Übertragung` 成立，而 `ue` 本来就是字母组合的词（如 Mauer、neue）不会被错误还原；
4. **前缀补全**：查询至少 2 个字符且不像句子时启用。已有强匹配时最多 8 条，否则 24 条，按热度取；
5. **拼写纠错**：查询至少 3 个字符时启用。在模糊索引里找编辑距离 1 的词；Fold 长度大于 5 且距离 1 的结果少于 3 个时，再找距离 2。
   - 如果已有精确词或词形匹配（强匹配），只保留距离为 1、且热度比强匹配高 1.5 个 Zipf 以上的纠错（例如 `teh → the`）。这样 `Haus` 不会被 Maus/Laus/Haut 淹没。

所有规则都是通用的：没有任何针对测试词的特判，验收用例只用来检验结果（`AcceptanceCases.cs`）。

### 模糊索引（`FuzzyIndex`）

- 由所有词头（最多 2 个空格）和常见词形（出现次数 ≥ 30）的 Fold 构成，共 95 万个键；
- 用 Daciuk 增量算法构建**最小确定性无环自动机（DAWG）**，共享前缀和后缀，内存约 11.6 MB；
- 搜索时在自动机上做带上界的 OSA（Damerau，允许相邻字母换位）动态规划，只计算对角线附近最多 `2k + 1` 个单元，并保留换位所需的前两行再剪枝；每层保存紧凑距离带，普通查询使用栈内存，大距离查询使用数组池；
- 序列化为一个 blob（魔数 `LXFZ`）存在数据库里，首次纠错时加载，约 20 ms；
- 测试中与独立的全矩阵 OSA 结果逐一对比（`FuzzyIndexTests`、`OsaReference`）。复杂度分析及优化前后实测见 [PERFORMANCE.md](PERFORMANCE.md)。

### 排序（`SearchEngine.Score`）

```
基础分：Exact 100 · Form 96 · Translit 95 · Fold/FormTranslit 93 · FormFold 91 · Prefix 62
        Fuzzy 58（距离 1）/ 44（距离 2）· FuzzyForm 56 / 42
+ 热度：Zipf(0–8) × 3.2
+ 前缀：比查询每长 1 个字母扣 0.4 分，最多扣 8 分
+ 纠错：首字母相同 +3，不同 −4；长度差 ≤ 1 再 +1
+ 查询含 ä/ö/ü/ß：德语词 +6，英语词 −6
+ 大小写完全一致 +1.5；有中文释义 +1.5；核心词 +2；查单个词时词组 −3；专有名词 −8
```

热度来自 FrequencyWords（字幕语料）。英语词还取 ECDICT 的 COCA/BNC 排名，两者取较大值。原形的热度包含屈折形式的出现次数；一个词形对应多个原形时，按各原形自身的词频比例分摊。

### 中文反查（`SearchEngine.SearchChinese`）

查询里有汉字时走这条路径（`QueryInfo.HasCjk`）。

- **查询键**：`ChineseTerms.QueryKey` 只保留汉字；存储层再用 `LCMapStringEx` 把繁体转成简体（`電腦 → 电脑`）。
- **整词**：在 `zh_index` 里查这个中文词，英语、德语各取前 40 个候选。分开取是为了让德语候选不会被更丰富的英语数据挤掉。
- **部分匹配**：没有整词结果、且查询至少 3 个字时（`Fuzzy = true`，主页 fallback 不做），从长到短查找子串，只查还没被覆盖的位置。例如 `网络安全漏洞` → 网络安全、漏洞。结果标为 `ChinesePart`。
- **排序**：
  ```
  整词：索引权重（1–100，几个来源一致时另加）+ Zipf × 5
  部分：权重 × 0.6 + 匹配长度 × 12 + Zipf × 5        （更长的部分更具体）
  + 核心词 +2；词组 −4；专有名词 −8；单个字母 −30；缩写（全大写、以点结尾）−6
  ```
- **按语言分组**：先英语后德语，各取 `Limit / 2` 个，所以两种语言都能出现在第一屏。词典页每种语言最多显示 6 条（AI 项排在最前时 4 条）：CmdPal 一屏大约放 8 行，这样德语组标题和它的头几条也在第一屏。按语言筛选时只剩一种语言，最多 12 条。
- **去重**：某个结果是另一个结果的屈折形式、且原形分数不低于它减 5 时，隐藏这个结果（`threats` 有 threat 时不显示）。只在排到前面时才查 `forms` 表，代价有限。比原形高很多的词形会保留，例如 `data`（datum 的复数）。
- **不拼接**：德语词只通过来源为这个德语词给出的中文找到，不经过英语中转。

### 反查索引（`ZhIndexBuilder`）

索引在导入的最后一步，从 `entries` 里已有的中文文本生成，没有单独的数据源：

| 来源 | 中文文本 | 系数 |
|---|---|---|
| ECDICT（英语） | 中文释义行，如 `n. 恐吓, 恶兆, 威胁` | 1；`[网络]` 行 0.55；`[人名]`/`[地名]` 等名字音译 0.3 |
| 中文版 Wiktionary（德语） | 中文释义 | 1 |
| 英文版 / 德文版 Wiktionary | 按义项的翻译表（`Sense.Zh`） | 0.95 |
| 中文版 Wiktionary 的中文词条 | 翻译表里的英/德译词（反向词条，见 §5） | 0.9 |

- `ChineseTerms.Extract` 先按 `，；、/` 等切成条目：带占位符的条目（`吃...的人`、`给…房子住`）整条跳过；去掉括号注释和含拉丁字母的片段；`危险的` 另外给出词干 `危险`（系数 0.95）；条目最长 8 个字。
- 权重 = 100 × 行系数（第 1 行 1.0，之后逐行降到 0.78）× 位置系数（第 1 个词 1.0，之后逐个降到 0.75）× 来源系数。同一个 (词, 中文) 取各条目的最大值；每多一个来源给出同样的对应，再加 6 分（`AgreementBonus`）。
- `zh_index(term, word_id, weight)` 是 `WITHOUT ROWID` 表，主键 `(term, word_id)`。完整数据库里有 76 万个中文词语、138 万条对应关系，生成约 30 秒。
- 旧数据库可以用 `lexi zh-index --from 旧库 --out 新库` 在副本上补建，不用重新导入。没有这张表时，扩展在中文输入时提示更新词典。

## 4. 存储（`lexi.db`，SQLite）

| 表 | 内容 |
|---|---|
| `meta` | schema 版本、构建时间、profile（personal / redistributable）、统计 |
| `sources` | 来源代码、名称、许可、网址、版本、署名、是否可再分发 |
| `words` | 一行一个（语言, 词头）：fold、zipf、flags、徽标（der/die/das、n.、v.…）、列表用的一句话释义 |
| `entries` | 按来源分开保存的词条（`WITHOUT ROWID`）。`data` 是 1 字节编码标记 + 长度 + Brotli 压缩的 JSON（`EntryData`，源生成序列化，可以安全裁剪） |
| `forms` | 词形 → 原形（fold、form、lemma_id、标签、来源） |
| `form_tags` | 词形标签字典 |
| `blobs` | 模糊索引 |
| `zh_index` | 中文词语 → 词头的反查索引，带权重（见 §3）。可选：旧数据库没有这张表 |

- 索引：`ix_words_fold(fold, zipf, lang)`；`forms` 的主键是 `(fold, lemma_id, form)`；`ix_forms_lemma(lemma_id)` 用于在详情页列出某个原形的所有词形，加上后详情构建从 76 ms 降到 0.9 ms。
- 只读打开，参数为 `Mode=ReadOnly`、`query_only`、`mmap_size` 256 MB、`cache_size` 16 MB。语句预先准备好，批量查询用 `json_each`。
- 词条**不在导入时合并**，每个来源单独存一行，展示时才聚合，所以界面上每一块都能标出来源。

## 5. 导入流水线（`LexiImporter`）

```
download（RawFiles）→ ECDICT → enwiktionary 英语 → dewiktionary 德语 → enwiktionary 德语 → zhwiktionary 德语
   → zhwiktionary 中文词条的翻译表（反向词条）
   → 解析词形（ResolveForms）→ 写入词头（热度、旗标、摘要）→ 构建模糊索引 → 中文反查索引
   → 建索引 + ANALYZE → VACUUM → 原子替换 lexi.db
```

- 流式读取 gzip 的 JSONL（按行处理字节），每 25,000 次操作提交一次。导入时 `journal_mode=OFF`，结束前切回 `DELETE` 并 VACUUM，所以只读打开时不需要 `-wal`/`-shm` 文件。
- **ECDICT 词形的处理**：
  - 只有「某词的过去式/复数」这类内容的行，只作为词形关系，不单独成条；
  - 只由 ECDICT 提供、且与原形公共前缀太短的关系一律丢弃，因为它的 exchange 字段是工具自动生成的，会产生 `ging → ge` 这样的错误。
- **中文版维基词典**：清理 `〈阴〉`、`pl.` 等语法前缀，把混在释义里的例句拆出来，并用 Windows `LCMapStringEx` 把繁体转成简体。
- **英文版维基词典的翻译表**：英语词条各义项下的汉语（官话）译词存进 `Sense.Zh`。`防火牆 /防火墙` 拆开后转成简体；闽南语、东干语等其他汉语变体不收。少数翻译表没有挂到义项上，按表头（如 human abode）和各义项释义的共同词找到最接近的义项，找不到就挂在第一个义项。
- **反向词条**：中文版维基词典里中文词条的翻译表，列有英语和德语译词（`防火牆` → firewall、Firewall）。导入最后一步把这些对应关系挂到已存在的英/德词头上，作为 `Kind = "reverse"` 的词条：每个义项是一个中文词，标签是它的义项说明。`to walk` 和 `sich …` 会去掉 to / sich 再匹配；词典里没有的译词直接忽略，不会新建词头。
- **德文版维基词典**：
  - 读取名词四格变格表、动词主要形式、助动词、可分前缀、形容词比较级；
  - 用 form_of 页面得到词形 → 原形的关系；
  - 拿不准的形式不写入，因为 `CleanGermanForm` 会去掉人称代词和复合时态。
- 导入是可重复的：输入只有下载下来的原始文件，输出是新的数据库文件。测试中有用小型样例文件跑完整导入的用例（`ImportPipelineTests`）。
- 新增词典来源的步骤见 [DATA_SOURCES.md §7](DATA_SOURCES.md#7-导入其他词典)。

## 6. 词条视图与渲染

`WordViewBuilder` 把一个词在各来源的条目聚合成 `WordView`：

- 来源优先级：英语 wikt-en 高于 ECDICT；德语 wikt-de 高于 wikt-zh 高于 wikt-en；
- 音标只取优先级最高的那个来源，再按口音排序（`PronunciationPlan.AccentRank`）：英语依次是英式（含英/美通用）、美式、未标注、其他地区，德语依次是德国（含未标注）、奥地利、瑞士；同一口音里音位记音 `/…/` 在语音记音 `[…]` 前。录音按同样的顺序排列；
- 详情面板的表头最多两条音标：英式一条、美式一条。第一条是英/美通用（house `/haʊ̯s/`）时只显示这一条；
- 中文、英文、德文释义按块分开，每块注明来源；
- 中文块分三种，依次显示：直接释义（ECDICT、中文版 Wiktionary）、按义项的对应词（翻译表，编号与英文/德文义项一致）、「中文词条的译词表收录此词」（反向词条）。中文反查找到的词，在这三种块里都能看到它对应的中文是从哪来的。

`MarkdownRenderer` 有两种输出：

- `Details`：紧凑格式，用于详情面板，包括表头一行、匹配说明、各语种释义；
- `Full`：完整词条页，包括义项、例句、语法表、词形、近义和易混词、学习者信息、来源与许可。

`AiContext` 把同一个 `WordView` 压缩成发给 AI 的参考事实。扩展和 `lexi ai` 共用它。

## 7. AI 与发音

**翻译的两条通道**：句子和中文输入都同时提供 AI（Claude Code）和在线（Google）两种翻译，各由自己的开关控制，互不依赖。

| 输入 | 在线翻译（`TranslationResultPage`） | AI（`AiPage`） | 位置 |
|---|---|---|---|
| 英语/德语句子 | 目标 `zh-CN` | `TranslateSentence` | 词典页最前（Google 在前）；独立的 `SentenceTranslationPage` 里同样两项 |
| 中文 | 目标 `en`、`de`，同一页依次请求 | `TranslateChinese`，带词典对应词作参考 | 「翻译」组：词语放在两组词典结果之后，句子和部分匹配放在最前 |

中文页的行数预算：CmdPal 大约显示 8 行，英语组最多 6 条。排在最前的每个翻译项各占一行，「翻译」标题也占一行，英语组相应减少（只有 AI 时 4 条，两项都有时 3 条），保证德语标题仍在第一屏。

**在线翻译**（`Lexi.Translation`）：

- 使用 Google 的免 Key 公共翻译端点（`translate_a/single?client=gtx`），不是有稳定性保证的 Cloud Translation API。这是用户的明确决定（见 CLAUDE.md「规矩」里的例外）：在 AI 之外提供一种不需要账号的联网翻译。默认关闭，设置开关（`ai.sentenceTranslation`，显示为「启用 Google 在线翻译」）与 AI 增强互相独立。
- `GoogleTranslator.TranslateAsync(text, target)` 的目标语言只允许 `zh-CN`、`en`、`de`。源语言自动识别。句子通过 HTTPS POST 放在表单正文里，不进 URL；不调用 Claude。
- 单次最多 2,000 字符，15 秒超时。同一时刻只发一个请求，间隔至少 500 ms。按「目标语言 + 原文」缓存最近 128 条，只保存在进程内存中；重试时绕过缓存。
- `SentenceTranslationPage` 只负责输入和预览。打开 `TranslationResultPage` 后才发请求；有多个目标语言时依次请求，某个语言失败只在它的位置显示错误。页面支持取消、按语言复制译文、重试和打开 Google 翻译网页；过期或被取消的请求不能覆盖新结果。错误不会自动转给其他提供方。
- 自检加 `--translation`（脚本 `-Translation`）时，会在禁用 Claude 的状态下通过真实结果页翻译英语、德语示例，以及中文 → 英语和德语的示例。

**AI**（`Lexi.AI`）：

- `AiService` 先检查是否可用，再按「提供者 + 模型 + 模板版本 + 提示词」的 SHA-256 查缓存（内存为最多 128 条的 LRU，磁盘缓存和 30 天有效期保留）；排队取得信号量后再次查缓存，复用前一个相同请求的答案，明确要求重新生成时仍绕过缓存；
- 同一时间只发一个请求（信号量），两次请求至少间隔 1.5 秒；
- `ClaudeCodeProvider` 的工作方式：
  - 启动 `claude -p --output-format stream-json --verbose --include-partial-messages --tools "" --no-session-persistence --strict-mcp-config --system-prompt …`，提示词从 stdin 传入，逐行解析 `text_delta` 实现流式输出；
  - 超时和取消时结束整个进程树；
  - 子进程的环境变量从注册表（Machine + User）重建，并移除 `CLAUDECODE`、`CLAUDE_CODE_ENTRYPOINT*`、`CLAUDE_CODE_SIMPLE`。这样即使 CmdPal 是从 Claude Code 会话里启动的，子进程也不会继承「嵌套会话」或 bare 模式；
  - 错误信息经过清理后才显示，日志里不出现任何凭据。
- `AiPage` 在页面打开时才发请求，每 100 ms 刷新一次正文；页面顶部固定标注「AI 生成内容」，正文不会写回词典。

**发音**：顺序由 `PronunciationPlan`（`Lexi.Dictionary`，纯逻辑，有单元测试）决定，`Pronouncer`（扩展）负责执行。

- 英语默认**英式优先**：
  1. 英式录音（标为英/美通用的录音也算）；
  2. en-GB 系统语音；
  3. 其他口音的录音（美式、未标注、其他地区）；
  4. en-US 语音，没有时用任何一种 en-* 语音。
  - 第 3、4 步标记为「其他口音」，提示里会说明原因：没有英式录音、英式录音下载失败，或没装英式语音（附添加方法）。
- `Ctrl+K` 里的「英式发音」「美式发音」只用该口音的录音和语音，没有就明确说「没有英式发音」，不改用另一种口音。
- 德语：德国（含未标注）、奥地利、瑞士的录音，然后是 de-DE 语音，没有时用任何一种 de-* 语音。
- 录音：用 MD5 路径规则拼出 Commons 转码后的 MP3 地址，下载超时 8 秒，成功后缓存到 LocalState。5xx 或连接错误重试一次。
- Wikimedia 的机器人策略（[Robot policy](https://wikitech.wikimedia.org/wiki/Robot_policy)）：
  - 请求必须带 `Accept` 头。HttpClient 默认不带，Wikimedia 前端会对没缓存的文件回 429「does not comply with our robot policy」；
  - 同一进程同一时刻只下载一个文件；
  - 收到 429 不立即重试，而是按 `Retry-After`（1 秒到 10 分钟，缺省 30 秒）暂停所有 Commons 请求，期间直接跳到下一步，提示「Wikimedia 限流，N 秒后再试」。
- 所有步骤都失败时提示「无可用发音」。提示里写明口音（英式/美式/英/美/澳式…）、真人录音还是合成语音，以及退而求其次的原因。
- `Pronouncer` 可以接收一个输出回调：自检用它代替扬声器，走完整个 Ctrl+P 流程但不出声。

**口音标注**（导入时，`KaikkiImporter.EnglishRegion`）：

- 依次看 wiktextract 的标签、录音者的备注（Lingua Libre 常把出身写在 note 里），最后看 Commons 文件名前缀（`En-uk-`、`en-us-`、`En-au-` …）。
- 同时有英式和美式标记（`Received-Pronunciation` + `General-American`）时记为 `UK/US`。
- `cot–caught merger` 这类只在美式发音里做的区分，在没有其他地区标记时记为美式。
- 每个词条最多保留 6 条音标和 4 条录音。先按口音排序再截取，所以排在后面的英式记音不会被大量地区变体挤掉（water 的 RP 记音排在原文第 11 条）。

## 8. 测试与验证

| 层次 | 工具 | 内容 |
|---|---|---|
| 单元测试 | `tests/Lexi.Tests` | 规范化、编辑距离、反向转写、模糊索引（与暴力算法对比）、搜索引擎（内存存储）、词形描述、词条编解码、导入清洗规则、口音识别（标签/备注/文件名）、发音顺序（英式优先、显式口音、德语）、音标排序与表头、Claude Code 参数与流事件解析、提示词、缺少 CLI 时的错误处理 |
| 导入集成测试 | `ImportPipelineTests` | 用样例原始文件跑完整导入，再验证查询和渲染 |
| 真实数据测试 | `RealDictionaryTests` | 34 个验收用例（15 个英语/德语 + 19 个中文反查，中文用例按所属语言的组内名次计）；德语名词（冠词/复数/IPA）、德语动词（主要形式）、英语（英式 IPA/中文）的抽查；典型查询延迟；没有数据库或没有中文索引时跳过并给出原因 |
| 扩展自检 | `Lexi.Extension.exe --self-test`（`scripts/self-test.ps1`） | 进程内驱动真实的 provider、页面、详情、词条页、fallback、设置表单（渲染并保存）、筛选、AI 入口、TTS、发音顺序与 Ctrl+P 提示（不出声）、热键焦点检查（自检时关闭、`INPUT` 大小、能找到面板窗口）；可选测试录音下载和英式优先的完整流程（`-Network`）和 Claude Code（`-Ai`）。`-Package` 测试裁剪后的 Release 包 |
| 性能 | `lexi bench` | 冷启动、预热后各类查询的分位数、详情构建、内存，生成 `docs/BENCHMARKS.md` |
| 质量 | `lexi quality` | 验收用例的排名与耗时 |

自检使用临时设置目录，不修改剪贴板，也不发出声音。

## 9. 关键决策

| 决策 | 理由 |
|---|---|
| 进程外 COM + MSIX，基于官方模板 | 这是 CmdPal 唯一支持的扩展模型，不需要改 PowerToys 源码 |
| 用 .NET SDK 和 NuGet 构建，把 `CsWinRTWindowsMetadata` 指向 SDK 投影包里的 winmd | 不需要安装 Visual Studio 或 Windows SDK，构建可以复现 |
| 数据库在本机构建，不随包分发 | ECDICT 的授权没有确认；Wiktionary 要求署名和相同方式共享；数据体积也大 |
| 词条按来源分开存储，展示时才聚合 | 每块释义都可以追溯来源；以后加新来源不需要重新导入其他来源 |
| SQLite + Brotli 压缩 JSON + 源生成序列化 | 只读、启动快、体积可控，可以安全裁剪和 AOT |
| 自研 DAWG 模糊索引，不用 SQLite FTS/spellfix | 95 万个键只占约 11.6 MB，距离 2 的查询只要几毫秒，也不需要原生扩展 |
| 不做德→英→中拼接 | 拼接容易产生错误的中文释义。没有直接中文时如实说明，并提供 AI 入口（标注为 AI） |
| 中文反查用导入时生成的倒排索引，不在查询时扫释义 | 释义是压缩的 JSON，无法直接检索；建好的索引让中文查询在几毫秒内完成，权重在导入时算好，可以复现 |
| 反查排序 = 释义中的位置 + 来源一致加分 + 词频，结果按语言分组 | 只看位置时，冷僻词（释义里只有这一个词）会排到常用译法前面；几个独立词典给出同一个对应，比单一来源可靠；分组保证英语和德语都出现在第一屏 |
| AI 走本机 Claude Code CLI（`claude -p`） | 按用户要求，使用用户自己的 Claude Code 配置和授权，Lexi 不保存任何 Key |
| 40 ms 去抖 + 取消 + 代号 | 输入时不卡顿，旧结果也不会覆盖新结果 |
| 详情按需构建 | 宿主只在选中时读取详情，没必要为 20 个结果都聚合 |
