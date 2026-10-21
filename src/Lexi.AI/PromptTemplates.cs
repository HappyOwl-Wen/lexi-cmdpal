using System.Text;

namespace Lexi.AI;

/// <summary>
/// Prompt templates. Output is concise Simplified Chinese Markdown. Local dictionary facts are passed as
/// reference so the model does not need to (and is told not to) invent IPA, forms or genders.
/// </summary>
public static class PromptTemplates
{
    /// <summary>Bump when templates change so cached answers are not reused across versions.</summary>
    public const int Version = 1;

    public static string Build(AiRequest r)
    {
        var lang = r.LanguageName ?? "英语或德语";
        var sb = new StringBuilder();
        switch (r.Kind)
        {
            case AiTaskKind.ExplainWord:
                sb.AppendLine($"请用简洁的简体中文解释{lang}单词「{r.Input}」的常见含义和用法。");
                AppendContext(sb, r);
                sb.AppendLine("要求：");
                sb.AppendLine("- 先用 1–2 句话给出核心含义；");
                sb.AppendLine($"- 列出 2–4 个最常见的义项或用法，每项给一个简短例句（{lang}原文 + 中文翻译）；");
                sb.AppendLine("- 指出常见搭配、语域（口语/书面/专业）或易错点；");
                break;

            case AiTaskKind.ExplainGrammar:
                sb.AppendLine($"请用简洁的简体中文讲解德语单词「{r.Input}」的语法要点，面向中文母语的德语学习者。");
                AppendContext(sb, r);
                sb.AppendLine("要求：");
                sb.AppendLine("- 名词：冠词/性、复数、需要注意的格变化；动词：变位规律、过去时、第二分词、助动词 haben/sein、是否可分；形容词：比较级与变格要点（只讲适用的部分）；");
                sb.AppendLine("- 用 2–3 个短例句说明（德语原文 + 中文翻译）；");
                sb.AppendLine("- 如果词典信息与你的判断不一致，请指出，不要静默覆盖；");
                break;

            case AiTaskKind.CompareWords:
                var words = new[] { r.Input }.Concat(r.CompareWith ?? []).Distinct().ToList();
                sb.AppendLine($"请用简洁的简体中文比较以下容易混淆的{lang}词：{string.Join("、", words.Select(w => $"「{w}」"))}。");
                if (!string.IsNullOrWhiteSpace(r.Domain))
                {
                    sb.AppendLine($"请重点说明它们在「{r.Domain}」领域中的区别。");
                }

                AppendContext(sb, r);
                sb.AppendLine("要求：");
                sb.AppendLine("- 先用一句话概括核心区别；");
                sb.AppendLine("- 用一个 Markdown 表格对比：词、核心含义、典型用法/搭配、例句（原文 + 中文）；");
                sb.AppendLine("- 最后给出一个容易用错的场景；");
                break;

            case AiTaskKind.TranslateSentence:
                sb.AppendLine("请把下面的句子翻译成自然、准确的简体中文，然后简要说明其中 1–3 个关键词或语法点。");
                sb.AppendLine();
                sb.AppendLine("句子：");
                sb.AppendLine(r.Input);
                sb.AppendLine();
                sb.AppendLine("要求：");
                sb.AppendLine("- 第一行只给译文；");
                break;

            case AiTaskKind.ExplainInContext:
                var domain = string.IsNullOrWhiteSpace(r.Domain) ? "专业" : r.Domain;
                sb.AppendLine($"请用简洁的简体中文解释{lang}术语「{r.Input}」在「{domain}」语境中的含义。");
                AppendContext(sb, r);
                sb.AppendLine("要求：");
                sb.AppendLine("- 给出该语境下的准确中文译法（可给 1–3 个常见译法）和定义；");
                sb.AppendLine("- 说明与日常含义的区别，并给一个该领域的例句（原文 + 中文）；");
                sb.AppendLine("- 列出 2–3 个相关术语；");
                break;

            case AiTaskKind.TranslateChinese:
                sb.AppendLine("请把下面的中文翻译成英语和德语，面向中文母语的英语/德语学习者，讲解用简体中文。");
                sb.AppendLine();
                sb.AppendLine("中文：");
                sb.AppendLine(r.Input);
                AppendContext(sb, r);
                sb.AppendLine("要求：");
                sb.AppendLine("- 先英语、后德语，各用一个小标题；德语名词带冠词（der/die/das）和复数；");
                sb.AppendLine("- 如果是词语：每种语言给 1–3 个最常用的对应词，用一句话说明它们的区别或适用场景；");
                sb.AppendLine("- 如果是句子：给出自然的英语译文和德语译文，再说明 1–2 个关键词的译法；");
                sb.AppendLine("- 每种语言给一个短例句（原文 + 中文）；");
                break;
        }

        sb.AppendLine("- 不要编造音标、词形或词典来源；不确定时明确说明；");
        sb.AppendLine("- 总长度不超过 300 字，使用 Markdown，不要寒暄，不要重复问题。");
        return sb.ToString();
    }

    private static void AppendContext(StringBuilder sb, AiRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.DictionaryContext))
        {
            return;
        }

        sb.AppendLine();
        sb.AppendLine("本地词典中的已知信息（可能不完整，仅供参考）：");
        sb.AppendLine(r.DictionaryContext.Trim());
        sb.AppendLine();
    }
}
