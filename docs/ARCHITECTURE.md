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
| `Lexi.Extension` | net10.0-windows10.0.26100.0 | CmdPal 扩展：COM 服务器、页面、命令、设置、发音 | 以上三者、CmdPal SDK、CsWinRT、WinRTServer |
| `Lexi.Cli` | net10.0 | 数据构建与诊断工具 | Core、Dictionary、AI |
| `tests/Lexi.Tests` | net10.0 | xUnit v3 测试 | Core、Dictionary、AI |

`Lexi.Core`、`Lexi.Dictionary` 和 `Lexi.AI` 都不依赖 UI，可以在 CLI 和测试中直接使用。扩展只是一层很薄的界面。

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

### 已在 PowerToys v0.100.2 源码中核实的宿主行为

| 行为 | 依据（`src/modules/cmdpal/…`） | 对 Lexi 的影响 |
|---|---|---|
| 别名由用户按命令配置；Indirect 别名匹配「别名+空格」，Direct 别名一输入就跳转 | `Microsoft.CmdPal.UI.ViewModels/AliasManager.cs`、`CommandAlias.cs` | Lexi 不能自己注册 `dict` 别名。README 指导用户设置 Indirect 别名；副标题里写上 `dict`，这样主页搜索 dict 也能找到 Lexi |
| fallback 的 `UpdateQuery` 在专用线程同步执行，慢阈值 200 ms | `Microsoft.CmdPal.UI.ViewModels/FallbackUpdateManager.cs` | 只做低成本查询，见上文 |
| `InitialSearchText` 在创建页面的 ViewModel 时读一次 `SearchText` | `Microsoft.CmdPal.UI.ViewModels/ListViewModel.cs` | 顶层页面借此在每次打开时回到首页（超过 1 秒没有输入才重置）；fallback 页面借此带上查询词 |
| `TextToSuggest` 用 → 键接受 | `Microsoft.CmdPal.UI/Controls/SearchBar.xaml.cs` | 拼写纠错和变音建议可以一键填入搜索框 |
| Enter 执行主命令，Ctrl+Enter 执行次要命令（`MoreCommands` 第一项），Ctrl+K 打开命令菜单 | `Microsoft.CmdPal.UI.ViewModels/CommandBarViewModel.cs`、`Microsoft.CmdPal.UI/Pages/ShellPage.xaml.cs` | 「复制释义」放在命令列表第一位 |
| 选中项时才读取 `Details` / `MoreCommands` | `Microsoft.CmdPal.UI.ViewModels/ListItemViewModel.cs` | 详情按需构建 |

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
- 搜索时在自动机上做带上界的 OSA（Damerau，允许相邻字母换位）动态规划，整行都超过上界时剪枝；
- 序列化为一个 blob（魔数 `LXFZ`）存在数据库里，首次纠错时加载，约 20 ms；
- 测试中与暴力 OSA 的结果逐一对比（`FuzzyIndexTests`）。

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

- 索引：`ix_words_fold(fold, zipf, lang)`；`forms` 的主键是 `(fold, lemma_id, form)`；`ix_forms_lemma(lemma_id)` 用于在详情页列出某个原形的所有词形，加上后详情构建从 76 ms 降到 0.9 ms。
- 只读打开，参数为 `Mode=ReadOnly`、`query_only`、`mmap_size` 256 MB、`cache_size` 16 MB。语句预先准备好，批量查询用 `json_each`。
- 词条**不在导入时合并**，每个来源单独存一行，展示时才聚合，所以界面上每一块都能标出来源。

## 5. 导入流水线（`LexiImporter`）

```
download（RawFiles）→ ECDICT → enwiktionary 英语 → dewiktionary 德语 → enwiktionary 德语 → zhwiktionary 德语
   → 解析词形（ResolveForms）→ 写入词头（热度、旗标、摘要）→ 构建模糊索引 → 建索引 + ANALYZE → VACUUM
   → 原子替换 lexi.db
```

- 流式读取 gzip 的 JSONL（按行处理字节），每 25,000 次操作提交一次。导入时 `journal_mode=OFF`，结束前切回 `DELETE` 并 VACUUM，所以只读打开时不需要 `-wal`/`-shm` 文件。
- **ECDICT 词形的处理**：
  - 只有「某词的过去式/复数」这类内容的行，只作为词形关系，不单独成条；
  - 只由 ECDICT 提供、且与原形公共前缀太短的关系一律丢弃，因为它的 exchange 字段是工具自动生成的，会产生 `ging → ge` 这样的错误。
- **中文版维基词典**：清理 `〈阴〉`、`pl.` 等语法前缀，把混在释义里的例句拆出来，并用 Windows `LCMapStringEx` 把繁体转成简体。
- **德文版维基词典**：
  - 读取名词四格变格表、动词主要形式、助动词、可分前缀、形容词比较级；
  - 用 form_of 页面得到词形 → 原形的关系；
  - 拿不准的形式不写入，因为 `CleanGermanForm` 会去掉人称代词和复合时态。
- 导入是可重复的：输入只有下载下来的原始文件，输出是新的数据库文件。测试中有用小型样例文件跑完整导入的用例（`ImportPipelineTests`）。
- 新增词典来源的步骤见 [DATA_SOURCES.md §7](DATA_SOURCES.md#7-导入其他词典)。

## 6. 词条视图与渲染

`WordViewBuilder` 把一个词在各来源的条目聚合成 `WordView`：

- 来源优先级：英语 wikt-en 高于 ECDICT；德语 wikt-de 高于 wikt-zh 高于 wikt-en；
- 音标只取优先级最高的那个来源；
- 中文、英文、德文释义按块分开，每块注明来源。

`MarkdownRenderer` 有两种输出：

- `Details`：紧凑格式，用于详情面板，包括表头一行、匹配说明、各语种释义；
- `Full`：完整词条页，包括义项、例句、语法表、词形、近义和易混词、学习者信息、来源与许可。

`AiContext` 把同一个 `WordView` 压缩成发给 AI 的参考事实。扩展和 `lexi ai` 共用它。

## 7. AI 与发音

**AI**（`Lexi.AI`）：

- `AiService` 先检查是否可用，再按「提供者 + 模型 + 模板版本 + 提示词」的 SHA-256 查缓存（内存加磁盘，30 天）；
- 同一时间只发一个请求（信号量），两次请求至少间隔 1.5 秒；
- `ClaudeCodeProvider` 的工作方式：
  - 启动 `claude -p --output-format stream-json --verbose --include-partial-messages --tools "" --no-session-persistence --strict-mcp-config --system-prompt …`，提示词从 stdin 传入，逐行解析 `text_delta` 实现流式输出；
  - 超时和取消时结束整个进程树；
  - 子进程的环境变量从注册表（Machine + User）重建，并移除 `CLAUDECODE`、`CLAUDE_CODE_ENTRYPOINT*`、`CLAUDE_CODE_SIMPLE`。这样即使 CmdPal 是从 Claude Code 会话里启动的，子进程也不会继承「嵌套会话」或 bare 模式；
  - 错误信息经过清理后才显示，日志里不出现任何凭据。
- `AiPage` 在页面打开时才发请求，每 100 ms 刷新一次正文；页面顶部固定标注「AI 生成内容」，正文不会写回词典。

**发音**（`Pronouncer`）：

- 依次尝试该词的 Commons 录音，用 MD5 路径规则拼出转码后的 MP3 地址。下载 8 秒超时，遇到 429/5xx 或连接错误重试一次，成功后缓存到 LocalState；
- 都失败时用 `SpeechSynthesizer` 合成，按 en-GB/en-US/de-DE/de-AT/de-CH 选择语音；
- 最后才提示「无可用发音」。提示里说明播放的是真人录音还是合成语音，以及原因（例如「词典中没有录音」「录音下载失败：HTTP 429」）。

## 8. 测试与验证

| 层次 | 工具 | 内容 |
|---|---|---|
| 单元测试 | `tests/Lexi.Tests` | 规范化、编辑距离、反向转写、模糊索引（与暴力算法对比）、搜索引擎（内存存储）、词形描述、词条编解码、导入清洗规则、Claude Code 参数与流事件解析、提示词、缺少 CLI 时的错误处理 |
| 导入集成测试 | `ImportPipelineTests` | 用样例原始文件跑完整导入，再验证查询和渲染 |
| 真实数据测试 | `RealDictionaryTests` | 15 个验收用例；德语名词（冠词/复数/IPA）、德语动词（主要形式）、英语（英式 IPA/中文）的抽查；典型查询延迟；没有数据库时跳过并给出原因 |
| 扩展自检 | `Lexi.Extension.exe --self-test`（`scripts/self-test.ps1`） | 进程内驱动真实的 provider、页面、详情、词条页、fallback、设置表单（渲染并保存）、筛选、AI 入口、TTS；可选测试录音下载（`-Network`）和 Claude Code（`-Ai`）。`-Package` 测试裁剪后的 Release 包 |
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
| AI 走本机 Claude Code CLI（`claude -p`） | 按用户要求，使用用户自己的 Claude Code 配置和授权，Lexi 不保存任何 Key |
| 40 ms 去抖 + 取消 + 代号 | 输入时不卡顿，旧结果也不会覆盖新结果 |
| 详情按需构建 | 宿主只在选中时读取详情，没必要为 20 个结果都聚合 |
