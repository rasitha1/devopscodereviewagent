using System.ComponentModel;
using CodeReviewAgent.Models;
using Microsoft.SemanticKernel;
using Spectre.Console;

namespace CodeReviewAgent.Plugins;

public sealed class ReviewReporterPlugin
{
    private readonly List<Finding> _findings = [];
    private readonly StatusContext? _statusContext;

    public ReviewReporterPlugin(StatusContext? statusContext)
    {
        _statusContext = statusContext;
    }

    public IReadOnlyList<Finding> Findings => _findings.AsReadOnly();

    [KernelFunction("report_finding")]
    [Description("Record a code review finding. Call once per distinct issue found.")]
    public string ReportFinding(
        [Description("Severity: critical, high, medium, low, or info")]
        string severity,
        [Description("Category: security, correctness, performance, patterns, or breaking")]
        string category,
        [Description("File path relative to repo root (e.g. src/MyApp/Services/OrderService.cs)")]
        string file,
        [Description("Line number of the issue. Use 0 for file-level findings with no specific line.")]
        int line,
        [Description("Short, specific title (e.g. 'Unparameterized SQL in BuildOrderQuery')")]
        string title,
        [Description("Detailed explanation of why this is a problem")]
        string description,
        [Description("Concrete, actionable suggestion for how to fix the issue")]
        string suggestion)
    {
        var finding = new Finding(
            Severity: ParseSeverity(severity),
            Category: ParseCategory(category),
            File: file,
            Line: line > 0 ? line : null,
            Title: title,
            Description: description,
            Suggestion: suggestion);

        _findings.Add(finding);
        _statusContext?.Status($"[grey]Finding ({finding.Severity}): {Markup.Escape(Truncate(title, 55))}[/]");

        return $"Recorded [{finding.Severity}] {title}";
    }

    private static Severity ParseSeverity(string s) => s.Trim().ToLowerInvariant() switch
    {
        "critical" => Severity.Critical,
        "high"     => Severity.High,
        "medium"   => Severity.Medium,
        "low"      => Severity.Low,
        _          => Severity.Info
    };

    private static FindingCategory ParseCategory(string s) => s.Trim().ToLowerInvariant() switch
    {
        "security"    => FindingCategory.Security,
        "correctness" => FindingCategory.Correctness,
        "performance" => FindingCategory.Performance,
        "patterns"    => FindingCategory.Patterns,
        "breaking"    => FindingCategory.Breaking,
        _             => FindingCategory.General
    };

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";
}
