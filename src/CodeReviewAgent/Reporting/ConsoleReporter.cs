using CodeReviewAgent.Models;
using Spectre.Console;

namespace CodeReviewAgent.Reporting;

public sealed class ConsoleReporter
{
    public void Report(ReviewResult result)
    {
        if (result.Findings.Count == 0)
        {
            AnsiConsole.MarkupLine("[green]No issues found.[/]");
        }
        else
        {
            foreach (var group in result.Findings
                .OrderByDescending(f => f.Severity)
                .GroupBy(f => f.Severity))
            {
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine($"[{SeverityStyle(group.Key)}] {group.Key.ToString().ToUpperInvariant()} — {group.Count()} finding(s)[/]");

                foreach (var finding in group)
                    RenderFinding(finding);
            }

            AnsiConsole.WriteLine();
            RenderSummaryTable(result.Findings);
        }

        if (!string.IsNullOrWhiteSpace(result.Summary))
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[grey]Agent Summary[/]").RuleStyle("grey dim"));
            AnsiConsole.MarkupLine($"[grey]{Markup.Escape(result.Summary)}[/]");
        }
    }

    private static void RenderFinding(Finding f)
    {
        var location = f.Line.HasValue
            ? $"[grey]{Markup.Escape(f.File)}:{f.Line}[/]"
            : $"[grey]{Markup.Escape(f.File)}[/]";

        var body =
            $"[bold]{Markup.Escape(f.Title)}[/]\n" +
            $"{location}  [dim]{f.Category}[/]\n\n" +
            $"{Markup.Escape(f.Description)}\n\n" +
            $"[{SeverityStyle(f.Severity)}]Fix:[/] {Markup.Escape(f.Suggestion)}";

        AnsiConsole.Write(new Panel(body)
        {
            Border = BoxBorder.Rounded,
            BorderStyle = Style.Parse(SeverityStyle(f.Severity)),
            Padding = new Padding(1, 0, 1, 0)
        });
    }

    private static void RenderSummaryTable(IReadOnlyList<Finding> findings)
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Severity")
            .AddColumn(new TableColumn("Count").RightAligned());

        foreach (var g in findings.GroupBy(f => f.Severity).OrderByDescending(g => g.Key))
        {
            table.AddRow(
                $"[{SeverityStyle(g.Key)}]{g.Key}[/]",
                $"[bold]{g.Count()}[/]");
        }

        table.AddRow("[grey]Total[/]", $"[bold]{findings.Count}[/]");
        AnsiConsole.Write(table);
    }

    private static string SeverityStyle(Severity s) => s switch
    {
        Severity.Critical => "red bold",
        Severity.High     => "darkorange",
        Severity.Medium   => "yellow",
        Severity.Low      => "blue",
        _                 => "grey"
    };
}
