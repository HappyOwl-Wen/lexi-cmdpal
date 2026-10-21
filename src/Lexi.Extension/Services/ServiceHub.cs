using Lexi.AI;

using Lexi.Translation;

namespace Lexi.Extension.Services;

/// <summary>Process-wide services shared by pages and commands.</summary>
internal sealed partial class ServiceHub : IDisposable
{
    /// <param name="dataDirectory">Override for settings, history and caches (self-test); default is the package LocalState.</param>
    public ServiceHub(string? dataDirectory = null)
    {
        Settings = new LexiSettings(dataDirectory);
        Dictionary = new DictionaryService();
        History = new HistoryStore(Settings.DataDirectory);
        Pronouncer = new Pronouncer(Settings.DataDirectory);
        Ai = new AiService(CreateProvider, Path.Combine(Settings.DataDirectory, "ai-cache"));
        Translation = new GoogleTranslator();
    }

    public LexiSettings Settings { get; }

    public DictionaryService Dictionary { get; }

    public HistoryStore History { get; }

    public Pronouncer Pronouncer { get; }

    public AiService Ai { get; }

    public GoogleTranslator Translation { get; }

    public bool AiEnabled => Settings.AiEnabled;

    public bool SentenceTranslationEnabled => Settings.SentenceTranslationEnabled;

    public void Dispose()
    {
        Dictionary.Dispose();
        Pronouncer.Dispose();
        Translation.Dispose();
    }

    private ClaudeCodeProvider CreateProvider() => new ClaudeCodeProvider(new ClaudeCodeOptions
    {
        ExecutablePath = Settings.ClaudePath,
        Model = Settings.Model,
        Effort = Settings.Effort,
        Timeout = Settings.AiTimeout,
        WorkingDirectory = Path.Combine(Settings.DataDirectory, "ai-workdir"),
    });
}
