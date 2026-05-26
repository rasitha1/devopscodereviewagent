using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Spectre.Console;

namespace CodeReviewAgent.Plugins;

public sealed class ShellPlugin
{
    private const int MaxOutputChars = 15_000;
    private const int TimeoutSeconds = 30;

    private readonly string _workingDirectory;
    private readonly StatusContext? _statusContext;

    public ShellPlugin(string workingDirectory, StatusContext? statusContext)
    {
        _workingDirectory = workingDirectory;
        _statusContext = statusContext;
    }

    [Description(
        "Run an OS command to explore the repository. Use git, cat/type, find/dir, grep, dotnet etc. " +
        "Returns stdout and stderr. Output is capped at 15 000 characters. " +
        "Prefer targeted commands (git diff for one file) over broad ones (cat on a huge file).")]
    public async Task<string> RunCommandAsync(
        [Description("The shell command to execute. Read-only operations only (git diff, type/cat, find/dir, grep).")]
        string command)
    {
        _statusContext?.Status($"[grey]$ {Markup.Escape(Truncate(command, 70))}[/]");

        var psi = BuildStartInfo(command);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(TimeoutSeconds));

        try
        {
            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("Process did not start.");

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);

            await process.WaitForExitAsync(cts.Token);

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            var sb = new StringBuilder(stdout);
            if (!string.IsNullOrWhiteSpace(stderr))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("[stderr]: ").AppendLine(stderr);
            }

            var output = sb.ToString().Trim();
            return string.IsNullOrEmpty(output)
                ? "(no output)"
                : Truncate(output, MaxOutputChars, $"\n... [truncated — {output.Length - MaxOutputChars} chars omitted]");
        }
        catch (OperationCanceledException)
        {
            return $"[Timed out after {TimeoutSeconds}s: {command}]";
        }
        catch (Exception ex)
        {
            return $"[Error: {ex.Message}]";
        }
    }

    private ProcessStartInfo BuildStartInfo(string command)
    {
        var psi = new ProcessStartInfo
        {
            WorkingDirectory = _workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            psi.FileName = "cmd.exe";
            psi.Arguments = $"/c {command}";
        }
        else
        {
            // Single-quote the command so bash receives it verbatim;
            // escape any single quotes already in the command string.
            psi.FileName = "/bin/sh";
            psi.Arguments = $"-c '{command.Replace("'", "'\\''")}'";
        }

        return psi;
    }

    private static string Truncate(string s, int max, string? suffix = "...")
    {
        if (s.Length <= max) return s;
        return s[..max] + (suffix ?? string.Empty);
    }
}
