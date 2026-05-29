using System.Text;
using System.Text.RegularExpressions;
using CodeReviewAgent.DevOps;
using CodeReviewAgent.Models;

namespace CodeReviewAgent.Reporting;

public sealed partial class DevOpsCommentReporter
{
    // Common prefix used to identify any thread posted by this agent.
    private const string MarkerPrefix = "<!-- ai-code-review-agent";

    // Marker for the top-level summary thread.
    private const string SummaryMarker = "<!-- ai-code-review-agent summary -->";

    // Per-finding marker embeds file and title so we can fingerprint it on re-runs.
    // title replaces " with ' to avoid breaking the HTML attribute.
    private static string FindingMarker(string file, string title) =>
        $"<!-- ai-code-review-agent file=\"{file.Replace('\\', '/')}\" title=\"{title.Replace('"', '\'')}\" -->";

    private readonly AzureDevOpsClient _client;

    public DevOpsCommentReporter(AzureDevOpsClient client) => _client = client;

    public static async Task DryRunAsync(ReviewResult result, string outputDir)
    {
        Directory.CreateDirectory(outputDir);
        foreach (var f in Directory.GetFiles(outputDir, "*.md"))
            File.Delete(f);

        var findings = result.Findings.Where(f => !string.IsNullOrWhiteSpace(f.File)).ToList();
        for (var i = 0; i < findings.Count; i++)
        {
            var finding = findings[i];
            var anchor = finding.Line.HasValue ? $":{finding.Line}" : "";
            var header = $"<!-- dry-run: would post inline thread on {finding.File}{anchor} -->\n\n";
            await File.WriteAllTextAsync(Path.Combine(outputDir, $"finding-{i + 1:000}.md"), header + BuildFindingComment(finding));
        }

        await File.WriteAllTextAsync(Path.Combine(outputDir, "summary.md"), BuildSummaryComment(result, 0));
    }

    public async Task ReportAsync(ReviewResult result, CancellationToken cancellationToken = default)
    {
        var existingThreads = await _client.GetThreadsAsync(cancellationToken);

        // Build suppression set from threads the developer explicitly resolved as won't-fix/by-design,
        // and close everything else so fresh results replace them.
        var suppressed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, status, content) in existingThreads)
        {
            if (!content.Contains(MarkerPrefix)) continue;

            if (status is "wontFix" or "byDesign")
            {
                // Developer decision — extract fingerprint and honour it, leave the thread alone
                var fp = ParseFindingMarker(content);
                if (fp.HasValue)
                    suppressed.Add(FingerprintKey(fp.Value.File, fp.Value.Title));
            }
            else
            {
                // Active / fixed / closed / pending — close so we can repost fresh results
                await _client.ResolveThreadAsync(id, cancellationToken);
            }
        }

        // Post one thread per finding, anchored to its file and line (skip suppressed ones)
        foreach (var finding in result.Findings.Where(f => !string.IsNullOrWhiteSpace(f.File)))
        {
            if (suppressed.Contains(FingerprintKey(finding.File, finding.Title))) continue;
            await _client.PostCommentAsync(BuildFindingComment(finding), finding.File, finding.Line, cancellationToken);
        }

        // Summary thread — always refreshed
        await _client.PostCommentAsync(BuildSummaryComment(result, suppressed.Count), cancellationToken: cancellationToken);
    }

    private static string BuildFindingComment(Finding f)
    {
        var sb = new StringBuilder();
        sb.AppendLine(FindingMarker(f.File, f.Title));
        var loc = f.Line.HasValue ? $" (line {f.Line})" : string.Empty;
        sb.AppendLine($"### {f.Severity}: {EscapeMd(f.Title)}{loc}");
        sb.AppendLine();
        sb.AppendLine($"**Category:** {f.Category}  |  **File:** `{f.File}`");
        sb.AppendLine();
        sb.AppendLine(f.Description);
        sb.AppendLine();
        sb.AppendLine("**Suggested fix:**");
        sb.AppendLine(f.Suggestion);
        sb.AppendLine();
        sb.AppendLine("---");
        sb.Append("*Posted by AI Code Review Agent*");
        return sb.ToString();
    }

    private static string BuildSummaryComment(ReviewResult result, int suppressedCount)
    {
        var sb = new StringBuilder();
        sb.AppendLine(SummaryMarker);
        sb.AppendLine("## AI Code Review Summary");
        sb.AppendLine();

        if (result.Findings.Count > 0)
        {
            sb.AppendLine("| Severity | Count |");
            sb.AppendLine("|----------|-------|");
            foreach (var g in result.Findings
                .GroupBy(f => f.Severity)
                .OrderByDescending(g => g.Key))
            {
                sb.AppendLine($"| **{g.Key}** | {g.Count()} |");
            }

            sb.AppendLine();

            var fileCount = result.Findings.Select(f => f.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            sb.AppendLine($"**{result.Findings.Count} finding(s)** across **{fileCount} file(s)**.");
            sb.AppendLine();

            sb.AppendLine("### Files with findings");
            foreach (var grp in result.Findings
                .GroupBy(f => f.File, StringComparer.OrdinalIgnoreCase)
                .OrderByDescending(g => g.Max(f => f.Severity)))
            {
                sb.AppendLine($"- `{grp.Key}` — {grp.Count()} finding(s)");
            }

            sb.AppendLine();
        }
        else
        {
            sb.AppendLine("No issues found.");
            sb.AppendLine();
        }

        if (suppressedCount > 0)
        {
            sb.AppendLine($"> **{suppressedCount} finding(s) suppressed** — marked WontFix or ByDesign in a previous run and not re-posted.");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(result.Summary))
        {
            sb.AppendLine("### Agent Notes");
            sb.AppendLine(result.Summary.Trim());
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.Append("*Posted by AI Code Review Agent*");
        return sb.ToString();
    }

    private static (string File, string Title)? ParseFindingMarker(string content)
    {
        var m = FindingMarkerRegex().Match(content);
        if (!m.Success) return null;
        return (m.Groups[1].Value, m.Groups[2].Value);
    }

    // Fingerprint is file (normalised, lowercase) + title (lowercase) — robust to line number drift
    private static string FingerprintKey(string file, string title) =>
        $"{file.Replace('\\', '/').ToLowerInvariant()}|{title.ToLowerInvariant()}";

    [GeneratedRegex(@"<!-- ai-code-review-agent file=""([^""]*)"" title=""([^""]*)"" -->")]
    private static partial Regex FindingMarkerRegex();

    private static string EscapeMd(string s) => s.Replace("|", "\\|");
}
