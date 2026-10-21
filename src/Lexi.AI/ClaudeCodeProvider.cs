using System.Collections;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Lexi.AI;

public sealed class ClaudeCodeOptions
{
    /// <summary>Path to claude.exe / claude.cmd; null or empty = auto-detect.</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>Model alias or id passed as --model; null/empty = whatever the user's Claude Code is configured to use.</summary>
    public string? Model { get; init; }

    /// <summary>--effort (low, medium, high …); null = Claude Code default.</summary>
    public string? Effort { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Empty working directory so no project CLAUDE.md or settings are picked up.</summary>
    public string WorkingDirectory { get; init; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Lexi", "ai-workdir");
}

/// <summary>
/// Uses the locally installed Claude Code CLI in headless mode (`claude -p`), so requests run under the
/// user's own Claude Code login/configuration and count against their own plan or API usage.
/// Lexi never reads or stores Claude Code credentials: the prompt goes in via stdin, tools are disabled
/// (`--tools ""`), and sessions are not persisted.
/// </summary>
public sealed class ClaudeCodeProvider(ClaudeCodeOptions options) : IAiProvider
{
    private const string SystemPrompt = "You are Lexi, a precise bilingual dictionary assistant. Follow the user's formatting instructions exactly.";

    public string Name => "Claude Code";

    public ClaudeCodeOptions Options => options;

    public string? CheckAvailability() =>
        Locate(options.ExecutablePath) is null
            ? "未找到 Claude Code CLI（claude.exe）。请安装 Claude Code，或在 Lexi 设置中填写 claude 的路径。"
            : null;

    /// <summary>Finds the CLI: explicit path → PATH → native installer location → npm global shim.</summary>
    public static string? Locate(string? explicitPath)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return File.Exists(explicitPath) ? explicitPath : null;
        }

        var candidates = new List<string>();
        var path = UserEnvironment.Get("PATH") ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            candidates.Add(Path.Combine(dir, "claude.exe"));
            candidates.Add(Path.Combine(dir, "claude.cmd"));
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        candidates.Add(Path.Combine(home, ".local", "bin", "claude.exe"));
        candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "npm", "claude.cmd"));
        return candidates.FirstOrDefault(File.Exists);
    }

    public async Task<AiResult> CompleteAsync(string prompt, IProgress<string>? partial, CancellationToken cancellationToken)
    {
        var exe = Locate(options.ExecutablePath) ?? throw new AiException(CheckAvailability()!);
        Directory.CreateDirectory(options.WorkingDirectory);

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            WorkingDirectory = options.WorkingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var arg in BuildArguments(options))
        {
            psi.ArgumentList.Add(arg);
        }

        UserEnvironment.Apply(psi.Environment);

        var sw = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(options.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        using var process = new Process { StartInfo = psi };
        try
        {
            if (!process.Start())
            {
                throw new AiException("无法启动 Claude Code CLI。");
            }
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new AiException($"无法启动 Claude Code CLI：{ex.Message}", ex);
        }

        using var killOnCancel = linked.Token.Register(() => TryKill(process));
        var stderrTask = process.StandardError.ReadToEndAsync(CancellationToken.None);

        // Prompt via stdin (UTF-8, no BOM): no command-line quoting issues, nothing visible in process lists.
        await using (var stdin = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false)))
        {
            await stdin.WriteAsync(prompt.AsMemory(), linked.Token).ConfigureAwait(false);
        }

        var text = new StringBuilder();
        string? model = null;
        string? finalText = null;
        string? error = null;
        try
        {
            while (await process.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0 || line[0] != '{')
                {
                    continue;
                }

                var ev = StreamEvent.Parse(line);
                switch (ev.Type)
                {
                    case "init":
                        model ??= ev.Model;
                        break;
                    case "delta":
                        text.Append(ev.Text);
                        partial?.Report(text.ToString());
                        break;
                    case "result":
                        finalText = ev.Text;
                        if (ev.IsError)
                        {
                            error = ev.Text;
                        }

                        break;
                }
            }

            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new AiException($"Claude Code 在 {options.Timeout.TotalSeconds:F0} 秒内没有完成，已取消。可在设置中调大超时时间。");
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        if (error is not null)
        {
            throw new AiException("Claude Code 返回错误：" + Sanitize(error));
        }

        var result = finalText ?? text.ToString();
        if (string.IsNullOrWhiteSpace(result))
        {
            var detail = Sanitize(stderr);
            throw new AiException(process.ExitCode != 0
                ? $"Claude Code 退出码 {process.ExitCode}。{detail}"
                : "Claude Code 没有返回内容。" + detail);
        }

        return new AiResult(result.Trim(), model, sw.Elapsed, FromCache: false, DateTimeOffset.Now);
    }

    internal static IEnumerable<string> BuildArguments(ClaudeCodeOptions options)
    {
        yield return "-p";
        yield return "--output-format";
        yield return "stream-json";
        yield return "--verbose";
        yield return "--include-partial-messages";
        yield return "--tools";
        yield return string.Empty;
        yield return "--no-session-persistence";
        yield return "--strict-mcp-config";
        yield return "--system-prompt";
        yield return SystemPrompt;
        if (!string.IsNullOrWhiteSpace(options.Model))
        {
            yield return "--model";
            yield return options.Model.Trim();
        }

        if (!string.IsNullOrWhiteSpace(options.Effort))
        {
            yield return "--effort";
            yield return options.Effort.Trim();
        }
    }

    private static void TryKill(Process p)
    {
        try
        {
            if (!p.HasExited)
            {
                p.Kill(entireProcessTree: true);
            }
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    /// <summary>Keeps error text short and single-line; Claude Code does not print credentials, but never echo more than needed.</summary>
    private static string Sanitize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s))
        {
            return string.Empty;
        }

        var line = string.Join(" ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(3));
        return line.Length > 300 ? line[..300] + "…" : line;
    }

    /// <summary>One line of `--output-format stream-json`.</summary>
    internal readonly record struct StreamEvent(string Type, string? Text, string? Model, bool IsError)
    {
        public static StreamEvent Parse(string line)
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = Get(root, "type");
                if (type == "system" && Get(root, "subtype") == "init")
                {
                    return new("init", null, Get(root, "model"), false);
                }

                if (type == "stream_event" && root.TryGetProperty("event", out var ev) && Get(ev, "type") == "content_block_delta" &&
                    ev.TryGetProperty("delta", out var delta) && Get(delta, "type") == "text_delta")
                {
                    return new("delta", Get(delta, "text"), null, false);
                }

                if (type == "result")
                {
                    var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
                    return new("result", Get(root, "result"), null, isError);
                }
            }
            catch (JsonException)
            {
            }

            return new("other", null, null, false);
        }

        private static string? Get(JsonElement e, string name) =>
            e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }
}

/// <summary>
/// The environment a freshly logged-in user would have (machine + user variables from the registry), so the CLI
/// sees the same Claude Code configuration as in a terminal, even if Command Palette was started earlier or from
/// another tool. Variables that mark a running Claude Code session are removed.
/// </summary>
internal static class UserEnvironment
{
    private static readonly string[] Inherited =
    [
        "USERPROFILE", "APPDATA", "LOCALAPPDATA", "HOMEDRIVE", "HOMEPATH", "USERNAME", "USERDOMAIN", "COMPUTERNAME",
        "SystemRoot", "SystemDrive", "windir", "ProgramData", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
        "CommonProgramFiles", "PUBLIC", "TEMP", "TMP", "NUMBER_OF_PROCESSORS", "PROCESSOR_ARCHITECTURE", "OS", "PATHEXT", "ComSpec",
    ];

    public static string? Get(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Environment.GetEnvironmentVariable(name);
        }

        if (name.Equals("PATH", StringComparison.OrdinalIgnoreCase))
        {
            var machine = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine);
            var user = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User);
            return Environment.ExpandEnvironmentVariables(string.Join(';', new[] { machine, user }.Where(s => !string.IsNullOrEmpty(s))));
        }

        return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Machine)
            ?? Environment.GetEnvironmentVariable(name);
    }

    public static void Apply(IDictionary<string, string?> env)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        env.Clear();
        foreach (var target in new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User })
        {
            foreach (DictionaryEntry kv in Environment.GetEnvironmentVariables(target))
            {
                if (kv.Key is string k && kv.Value is string v)
                {
                    env[k] = Environment.ExpandEnvironmentVariables(v);
                }
            }
        }

        env["PATH"] = Get("PATH");
        foreach (var k in Inherited)
        {
            var v = Environment.GetEnvironmentVariable(k);
            if (!string.IsNullOrEmpty(v))
            {
                env[k] = v;
            }
        }

        foreach (var k in env.Keys.Where(k => k.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase) || k.StartsWith("CLAUDE_CODE_ENTRYPOINT", StringComparison.OrdinalIgnoreCase) || k.Equals("CLAUDE_CODE_SIMPLE", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            env.Remove(k);
        }
    }
}
