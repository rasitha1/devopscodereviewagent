using System.Text;
using CodeReviewAgent.DevOps;
using CodeReviewAgent.Models;

namespace CodeReviewAgent.Reporting;

public sealed class DevOpsCommentReporter
{
    private readonly AzureDevOpsClient _client;

    public DevOpsCommentReporter(AzureDevOpsClient client) => _client = client;

    public async Task ReportAsync(ReviewResult result)
    {
        // Per-file inline threads (one thread per file, listing all findings for that file)
        var byFile = result.Findings
            .Where(f => !string.IsNullOrWhiteSpace(f.File))
            .GroupBy(f => f.File, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => g.Key);

        foreach (var fileGroup in byFile)
        {
            var findings = fileGroup.OrderByDescending(f => f.Severity).ToList();
            var content = BuildFileComment(fileGroup.Key, findings);
            // Anchor thread to the line of the highest-severity finding
            var anchorLine = findings.FirstOrDefault(f => f.Line.HasValue)?.Line;
            await _client.PostCommentAsync(content, fileGroup.Key, anchorLine);
        }

        // Top-level PR summary thread
        var summaryContent = BuildSummaryComment(result);
        await _client.PostCommentAsync(summaryContent);
    }

    private static string BuildFileComment(string file, IList<Finding> findings)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"### AI Code Review — `{file}`");
        sb.AppendLine();

        // Quick table
        sb.AppendLine("| Severity | Line | Category | Issue |");
        sb.AppendLine("|----------|------|----------|-------|");
        foreach (var f in findings)
            sb.AppendLine($"| **{f.Severity}** | {f.Line?.ToString() ?? "—"} | {f.Category} | {EscapeMd(f.Title)} |");

        sb.AppendLine();

        // Full detail for each finding
        foreach (var f in findings)
        {
            var loc = f.Line.HasValue ? $" (line {f.Line})" : string.Empty;
            sb.AppendLine($"#### {f.Severity}: {EscapeMd(f.Title)}{loc}");
            sb.AppendLine();
            sb.AppendLine(f.Description);
            sb.AppendLine();
            sb.AppendLine($"> **Suggested fix:** {f.Suggestion}");
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.Append("*Posted by AI Code Review Agent*");
        return sb.ToString();
    }

    private static string BuildSummaryComment(ReviewResult result)
    {
        var sb = new StringBuilder();
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

    // Escape pipe characters so they don't break markdown tables
    private static string EscapeMd(string s) => s.Replace("|", "\\|");
}
