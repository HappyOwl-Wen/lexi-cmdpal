# 词典数据源：调研、取舍与许可

本文记录 Lexi 实际下载、检查并导入的每个数据源。数字来自 2026-10-20 在本机的一次完整导入（`lexi import` 日志），不是估算。

## 结论速览

| 数据源 | 用途 | 内容许可 | 是否随安装包分发 |
|---|---|---|---|
| ECDICT（ecdict.csv 基础版） | 英→中释义、学习者标签（考试大纲/柯林斯/牛津3000）、BNC/COCA 词频、英语词形 | 仓库 LICENSE 为 MIT，但 README 说明内容汇编自多个第三方来源，**逐条授权未说明** | **否**。只在用户本机构建时导入 |
| Wiktionary 英文版（Kaikki/wiktextract） | 英语：英/美 IPA、英文释义、词形、录音文件名、按义项的汉语译词（翻译表）；德语词条的英文释义 | CC BY-SA 4.0（及 GFDL） | 可以（需署名、相同方式共享），默认仍不打包 |
| Wiktionary 德文版（Kaikki/wiktextract） | 德语：冠词/性、格变化、动词主要形式、助动词、可分前缀、比较级、IPA、德语释义、按义项对齐的中/英翻译、词形→原形 | CC BY-SA 4.0 | 同上 |
| Wiktionary 中文版（Kaikki/wiktextract） | 德语词条的**直接中文释义**；中文词条翻译表里的英/德译词（用于中文反查） | CC BY-SA 4.0 | 同上 |
| FrequencyWords（OpenSubtitles 2018） | 仅用于排序的词频，不展示 | 内容 CC BY-SA 4.0，代码 MIT | 同上 |
| Wikimedia Commons 录音 | 真人发音 | 每个文件单独授权（多为 CC BY-SA / CC0） | 否。首次播放时按需下载并缓存在本机 |
| FreeDict deu-eng / eng-deu（Ding） | 调研后 **v1 未导入** | GPLv3 + AGPLv3（Ding 为 GPLv2+） | — |

**代码与数据分开授权**：Lexi 源代码采用 MIT。仓库里不提交任何词典数据（见 `.gitignore`）。数据库由 `lexi import` 在用户本机从官方下载地址构建，数据库中的 `sources` 表记录每个来源的名称、许可、网址、版本和署名。

## 1. ECDICT

- 地址：https://github.com/skywind3000/ECDICT
- 下载：`ecdict.csv`（66 MB，770,612 行，即"基础版"；仓库另有 `stardict.7z` 完整版，未使用）
- 字段：`word, phonetic, definition（英文，近似 WordNet）, translation（中文）, pos, collins, oxford, tag, bnc, frq, exchange, detail, audio`
- 实测：735,000 条词条导入，另有 35,470 行只是"某词的过去式/复数"等指针，导入时只作为词形关系，不单独成条。
- 质量：
  - 中文释义覆盖面极广，包括 `[计]` `[医]` 等学科标注，可以追溯来源。
  - 不少冷僻词只有 `[网络]` 释义，生成列表摘要时会优先选非网络释义。
  - 音标以英式为主，部分是爬取所得，格式不完全规范（如 `ɒ:θenti'keiʃәn`）。所以界面优先显示维基词典的 IPA，ECDICT 音标只作后备并标明来源。
  - `exchange` 词形由 NodeBox/WordNet 工具**自动生成**（README 原文说明），会产生垃圾关系，例如把 `ging` 当成 `ge` 的现在分词。导入时的处理是：仅由 ECDICT 提供、且与原形没有足够长公共前缀的词形关系一律丢弃；Wiktionary 人工整理的不规则形式（went→go）不受影响。
- **许可分析**：仓库 LICENSE 是 MIT（Copyright 2025 Linwei），但 README 自述数据来自
  - EDictAZ.txt（他人提供的文本文件）、四六级到 GRE 词汇表
  - "写了个爬虫从各种资料里面把音标给爬下来"
  - Linux 下的 cdict-1.0-1.rpm 开源词典、网友贡献

  这些上游来源的授权条款并未逐一列明。按项目要求，**不能仅凭仓库 LICENSE 推断全部词条可再分发**，因此标记为 `redistributable = false`：只在用户本机构建的 personal 数据库中使用，不打进安装包。`scripts/import-data.ps1 -NoEcdict` 可以构建不含 ECDICT 的可分发数据库。

## 2. Kaikki.org / wiktextract（Wiktionary）

- 项目：https://kaikki.org/ （Tatu Ylonen 的 wiktextract 抽取的结构化 Wiktionary 数据）
- 许可：Wiktionary 文本为 CC BY-SA 4.0（及 GFDL），抽取数据随之采用相同许可。音频不在数据里，只有 Commons 文件名和 URL。
- 本次使用的文件（dump 日期 2026-09/10，下载于 2026-10-20）：

| 文件 | 大小（gz） | 记录 | 导入的词条 |
|---|---:|---:|---:|
| `kaikki.org-dictionary-English.jsonl.gz`（英文版·英语） | 523 MB | 1,491,815 | 258,189 |
| `kaikki.org-dictionary-German.jsonl.gz`（英文版·德语） | 98 MB | 371,162 | 84,274 |
| `downloads/de/de-extract.jsonl.gz`（德文版原始抽取，全语种） | 309 MB | 1,010,254（德语） | 167,603 |
| `downloads/zh/zh-extract.jsonl.gz`（中文版原始抽取，全语种） | 234 MB | 174,267（德语） | 77,060 |

注意：Kaikki 的"按语言后处理"导出已被标为 deprecated。导入器同时支持原始全语种 dump（按 `lang_code` 过滤），下载地址如果失效，可以改用 `raw-wiktextract-data.jsonl.gz`。

- 字段与质量（实际抽样核对过 Schwachstelle、gehen、Haus、Häuser、ging、anfangen、gut、authentication、vulnerability 等）：
  - **德文版**：语法信息最完整。
    - `Deutsch Substantiv Übersicht`：四格 × 单复数 + 冠词。
    - `Deutsch Verb Übersicht`：现在时三种人称、Präteritum、Konjunktiv II、命令式、Partizip II、Hilfsverb。
    - 另有完整的 `Flexion:` 变位/变格表、IPA（含奥地利变体注释）、Commons 录音。
    - 按义项编号对齐的翻译表：英语覆盖好，**中文覆盖稀疏**。例如 Schwachstelle 有英/法/日等译文，但没有中文。
    - 变格/变位形式各有独立页面（"Konjugierte Form"、"Deklinierte Form"），给出 form_of → 原形，这是 `ging→gehen`、`Häuser→Haus` 的数据来源。
  - **中文版**：德语词条有直接中文释义。例如 Schwachstelle 是"〈阴〉 pl.Schwachstellen 缺点，弱点"，gehen 有 10 个义项。
    - 一部分是机器导入格式：语法前缀需要清理，例句混在 gloss 里（以全角空格分隔）。
    - 繁简混用：导入时用 Windows `LCMapStringEx(LCMAP_SIMPLIFIED_CHINESE)` 逐字转换为简体。这是字形转换，不改变释义。
  - **英文版**：英语词条的 IPA 带地区标签（Received-Pronunciation / General-American 等）、义项带领域标签（computing 等）、例句、词形，录音分英/美。德语词条的英文释义比德文版翻译表的单词对应更完整（如 Schwachstelle：weakness, weak point; vulnerability）。

**中文释义的原则**：只展示来源中**直接给出**的中文（ECDICT、zh.wiktionary 释义、de.wiktionary 和 en.wiktionary 按义项给出的中文对应词、zh.wiktionary 中文词条翻译表的对应关系），每块都标明来源。**不做**"德→英→中"的多级拼接。德语词条没有中文时如实显示"暂无可靠的直接中文释义"，并提供 AI 解释入口；AI 结果会明确标注为 AI 生成。

### 2.1 中文反查用到的翻译表（2026-10-21 加入）

中文 → 英/德的反查索引不引入新的数据源，只从上面这些文件里取中文文本（见 ARCHITECTURE.md §3）。其中有两类是专门为反查加入导入的，都来自已经在用的同一批 Wiktionary 文件，许可不变（CC BY-SA 4.0）：

| 内容 | 文件 | 实测（2026-10-21 完整导入） | 质量 |
|---|---|---|---|
| 英语词条按义项的汉语（官话）译词 | `kaikki.org-dictionary-English.jsonl.gz` 的 `senses[].translations`，以及少量挂在词条顶层的 `translations` | 原始数据中 50,502 个英语词条带汉语译词，共 87,862 行（`cmn` 83,483、`zh` 4,227 等，少量是闽南语、东干语，导入时排除）。抽查：firewall → 防火墙、authentication → 认证/鉴权、loophole（第 2 义项）→ 漏洞、threat → 威胁、learn → 学习 | 人工编写，按义项对齐。ECDICT 缺的现代词义在这里通常有（ECDICT 的 firewall 是错字「放火墙」，loophole 只有「枪眼」） |
| 中文词条翻译表里的英/德译词 | `zh-extract.jsonl.gz` 里 `lang_code = zh` 的词条 | 177,384 个中文词条带翻译表。其中能对上词典里英/德词头的：44,010 条对应关系，涉及 27,324 个词头。抽查：防火牆 → firewall / Brandmauer / Firewall、威脅 → threaten / menace / intimidate / drohen / bedrohen、加密 → encrypt / verschlüsseln | 方向正好是中 → 英/德。德语覆盖明显补强 |

- 汉语译词只收官话。翻译表里的闽南语（如 house → 厝）、东干语（西里尔字母）、粤语等其他变体不收。繁体统一转成简体（`防火牆 /防火墙` → 防火墙）。
- 中文词条的译词只挂到词典里**已有**的英/德词头上，不新建词头。展示时单独成块，注明「中文词条的译词表收录此词」以及原始记录（如 `kaikki:zhwiktionary:防火牆#noun`）。
- 德文版 Wiktionary 也有约 494 个中文词条（德文释义），数量太少，未使用。
- ECDICT 中文释义的整理规则（同样只影响反查索引）：`[网络]` 行降权；`[人名]` `[地名]` 等名字音译行大幅降权；带占位符的条目（`吃...的人`、`给…房子住`）不当作译词；括号里的注释（`chink的名词复数`）去掉。

## 3. FreeDict（调研，v1 未导入）

- 地址：https://freedict.org/downloads/ ，元数据 `freedict-database.json`
- `deu-eng 1.9-fd1`：517,534 个词头；`eng-deu 1.9-fd1`：460,315 个。TEI XML，来自 TU Chemnitz 的 Ding 词典。
- 字段：德→英对译、名词性别、词性、用法标签（fig. 等）、同义交叉引用。**没有 IPA，没有中文**。
- 许可：TEI 头部写明本组合作品同时受 GPLv3 和 AGPLv3 约束（Ding 原数据为 GPLv2+，转换程序为 AGPLv3+）。
- 未导入的原因：
  1. 没有中文和音标，而 Lexi 的核心是中文释义；
  2. 德语语法与英文释义已由两个 Wiktionary 版本覆盖；
  3. copyleft 会传染到合并后的数据库分发。

  Ding 的技术术语很丰富（Schwachstellenanalyse 等），适合将来作为**用户本机可选导入**的补充源。导入器的接口已经按来源解耦。
- 另：FreeDict 有 `eng-zho`（26,660 词头，来自 Wiktionary/WikDict），与已导入数据重复，未使用。

## 4. FrequencyWords

- 地址：https://github.com/hermitdave/FrequencyWords （OpenSubtitles 2018）
- `en_full.txt` 1,656,996 词；`de_full.txt` 1,157,685 词。内容 CC BY-SA 4.0，代码 MIT。
- 只用于排序，换算为 Zipf 尺度：log10(每十亿词出现次数)。
  - 原形的热度包含其屈折形式的出现次数（ging 计入 gehen）；
  - 一个词形对应多个原形时，按各原形自身词频比例分摊，避免罕见同形词"借"到热度；
  - 字幕语料偏口语，所以英语还用 ECDICT 的 COCA/BNC 排名取较大值，避免低估 authentication 这类书面/技术词。

## 5. 发音音频

- Wiktionary 的 `sounds` 字段给出 Commons 文件名（如 `De-Schwachstelle.ogg`）以及 MP3 转码地址。
- 播放时才从 `upload.wikimedia.org` 下载 MP3，缓存在扩展的 LocalState 目录，之后离线可用。界面显示文件名，可追溯授权。
- 没有录音或离线时，回退到 Windows 语音合成（界面标明"系统合成语音"）；两者都不可用时明确提示"无可用发音"。

## 6. 署名与追溯机制

- 数据库 `sources` 表：code、名称、许可、网址、版本（下载日期）、署名、是否可再分发。
- `entries` 表每行带 `source_id`，并保存原始记录引用，如 `kaikki:dewiktionary:Schwachstelle#noun`、`ecdict:authentication`。
- 界面上每一块释义都写明来源；完整词条页列出所有来源、许可和原始数据引用。
- 第三方声明见 `THIRD-PARTY-NOTICES.md`。

## 7. 导入其他词典

导入器按来源解耦：`EcdictImporter`、`KaikkiImporter`（en/de/zh 三种版本映射），共用 `ImportContext`（词头登记、词条写入、词形暂存）。新增来源（例如 FreeDict TEI 或个人词表）的做法：

1. 实现一个把源格式映射为 `EntryData` 的导入器；
2. 在 `KnownSources` 登记许可信息；
3. 在 `LexiImporter.Run` 中调用。

数据库 schema 不需要改动。
