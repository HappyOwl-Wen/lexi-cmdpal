using System.Globalization;
using Microsoft.CommandPalette.Extensions.Toolkit;

namespace Lexi.Extension.Services;

/// <summary>
/// Extension settings, edited on Command Palette's native settings page (Adaptive Card form) and stored as JSON
/// in the package's LocalState folder. No secrets are stored: the AI backend is the user's own Claude Code CLI.
/// </summary>
internal sealed class LexiSettings : JsonSettingsManager
{
    private readonly ToggleSetting _aiEnabled = new(
        "ai.enabled",
        "启用 AI 增强（Claude Code）",
        "仅在你主动触发时调用本机 Claude Code CLI（claude -p）；会消耗你的 Claude Code 用量。关闭后 AI 入口全部隐藏。",
        true);

    // Preserve the original persisted ID; this option is independent of the AI master switch.
    private readonly ToggleSetting _sentenceTranslation = new(
        "ai.sentenceTranslation",
        "启用 Google 在线翻译",
        "英语/德语句子 → 中文，中文 → 英语和德语，并显示独立的「Lexi 句子翻译」命令。按 Enter 才把文字发送到 Google 翻译的非官方免 Key 接口（不是官方 Cloud API，可能限流或失效）。不调用 Claude Code，关闭 AI 增强也可用。默认关闭。",
        false);

    private readonly TextSetting _claudePath = new(
        "ai.claudePath",
        "Claude Code 可执行文件路径",
        "留空自动查找（PATH、%USERPROFILE%\\.local\\bin\\claude.exe、npm 全局目录）。",
        string.Empty);

    private readonly TextSetting _model = new(
        "ai.model",
        "模型（可选）",
        "传给 --model 的别名或模型 ID，例如 sonnet、haiku。留空则使用你在 Claude Code 中的默认配置。",
        string.Empty);

    private readonly ChoiceSetSetting _effort = new(
        "ai.effort",
        "推理强度（可选）",
        "传给 --effort。默认沿用 Claude Code 配置；low 更快。",
        [new("沿用 Claude Code 配置", string.Empty), new("low", "low"), new("medium", "medium"), new("high", "high")]);

    private readonly TextSetting _timeout = new(
        "ai.timeoutSeconds",
        "AI 请求超时（秒）",
        "超时后自动取消请求，不影响本地词典。",
        "90");

    private readonly TextSetting _domain = new(
        "ai.domain",
        "专业语境（可选）",
        "例如 IT Security。用于「专业语境解释」与「易混词辨析」。也可以在搜索框输入 词 @领域。",
        "IT Security");

    private readonly ToggleSetting _fallback = new(
        "search.fallback",
        "在 Command Palette 主页显示查词结果",
        "在主页直接输入单词（或 dict 单词）时，于底部显示 Lexi 的释义；回车进入词典页。",
        true);

    private readonly ToggleSetting _showGerman = new(
        "display.germanDefinitions",
        "详情中显示德语释义（德德）",
        "德语词条除中文/英文外，同时显示 de.wiktionary 的德语释义。",
        true);

    /// <param name="directory">Settings/data folder override (self-test); default is the package LocalState.</param>
    public LexiSettings(string? directory = null)
    {
        directory ??= Utilities.BaseSettingsPath("Lexi");
        Directory.CreateDirectory(directory);
        FilePath = Path.Combine(directory, "settings.json");
        DataDirectory = directory;

        Settings.Add(_sentenceTranslation);
        Settings.Add(_aiEnabled);
        Settings.Add(_claudePath);
        Settings.Add(_model);
        Settings.Add(_effort);
        Settings.Add(_timeout);
        Settings.Add(_domain);
        Settings.Add(_fallback);
        Settings.Add(_showGerman);

        LoadSettings();
        Settings.SettingsChanged += (_, _) =>
        {
            SaveSettings();
            Changed?.Invoke(this, EventArgs.Empty);
        };
    }

    public event EventHandler? Changed;

    /// <summary>Package LocalState folder (or %LOCALAPPDATA%\Lexi when unpackaged).</summary>
    public string DataDirectory { get; }

    public bool AiEnabled => _aiEnabled.Value;

    public bool SentenceTranslationEnabled => _sentenceTranslation.Value;

    public string? ClaudePath => Blank(_claudePath.Value);

    public string? Model => Blank(_model.Value);

    public string? Effort => Blank(_effort.Value);

    public TimeSpan AiTimeout =>
        int.TryParse(_timeout.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s is >= 10 and <= 600
            ? TimeSpan.FromSeconds(s)
            : TimeSpan.FromSeconds(90);

    public string? Domain => Blank(_domain.Value);

    public bool FallbackEnabled => _fallback.Value;

    public bool ShowGermanDefinitions => _showGerman.Value;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
