using Azure.Identity;
using CodeReviewAgent.Agent;
using CodeReviewAgent.DevOps;
using CodeReviewAgent.Diagnostics;
using CodeReviewAgent.Models;
using CodeReviewAgent.Reporting;
using Microsoft.SemanticKernel;
using Spectre.Console;
using Spectre.Console.Cli;

namespace CodeReviewAgent.Commands;

public sealed class ReviewCommand : AsyncCommand<ReviewSettings>
{
    protected override async Task<int> ExecuteAsync(CommandContext context, ReviewSettings settings, CancellationToken cancellationToken)
    {
        var workingDir = settings.WorkingDirectory ?? Directory.GetCurrentDirectory();

        if (!Directory.Exists(workingDir))
        {
            AnsiConsole.MarkupLine($"[red]Error:[/] Directory not found: {workingDir}");
            return 2;
        }

        var baseBranch = ResolveBaseBranch(settings.BaseBranch);
        var devOpsContext = ResolveDevOpsContext(settings);

        AnsiConsole.Write(new Rule("[blue]AI Code Review[/]").RuleStyle("blue dim"));
        AnsiConsole.MarkupLine($"[grey]Endpoint  :[/] {settings.Endpoint}");
        AnsiConsole.MarkupLine($"[grey]Deployment:[/] {settings.Deployment}");
        AnsiConsole.MarkupLine($"[grey]Base branch:[/] {baseBranch}");
        AnsiConsole.MarkupLine($"[grey]Directory :[/] {workingDir}");
        if (devOpsContext is not null)
            AnsiConsole.MarkupLine($"[grey]PR comments:[/] {devOpsContext.Organization}/{devOpsContext.Project} PR#{devOpsContext.PullRequestId}");
        AnsiConsole.WriteLine();

        // DefaultAzureCredential works with AzureCLI task's service connection login
        var credential = new DefaultAzureCredential();

        if (settings.Verbose)
            await AuthDiagnostics.RunAsync(settings.Endpoint, settings.Deployment, credential, cancellationToken);

        var kernel = Kernel.CreateBuilder()
            .AddAzureOpenAIChatCompletion(settings.Deployment, settings.Endpoint, credential)
            .Build();

        var orchestrator = new CodeReviewOrchestrator(kernel, workingDir, baseBranch, settings);

        ReviewResult result = await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("blue"))
            .StartAsync("Analyzing repository...", ctx => orchestrator.RunAsync(ctx, cancellationToken));

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]Findings[/]").RuleStyle("blue dim"));

        var consoleReporter = new ConsoleReporter();
        consoleReporter.Report(result);

        if (devOpsContext is not null)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[blue]Posting to Azure DevOps[/]").RuleStyle("blue dim"));
            using var devOpsClient = new AzureDevOpsClient(devOpsContext);
            var devOpsReporter = new DevOpsCommentReporter(devOpsClient);
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Posting comments...", _ => devOpsReporter.ReportAsync(result, cancellationToken));
            AnsiConsole.MarkupLine("[green]Comments posted.[/]");
        }

        return ComputeExitCode(result, settings.FailOn);
    }

    private static string ResolveBaseBranch(string? explicitValue)
    {
        if (!string.IsNullOrEmpty(explicitValue)) return explicitValue;

        // Azure Pipelines sets SYSTEM_PULLREQUEST_TARGETBRANCH as "refs/heads/main"
        var pipelineVar = Environment.GetEnvironmentVariable("SYSTEM_PULLREQUEST_TARGETBRANCH");
        if (!string.IsNullOrEmpty(pipelineVar))
            return pipelineVar.Replace("refs/heads/", "").Trim();

        return "main";
    }

    private static DevOpsContext? ResolveDevOpsContext(ReviewSettings settings)
    {
        if (!settings.PostToDevOps) return null;

        var collectionUri = Environment.GetEnvironmentVariable("SYSTEM_TEAMFOUNDATIONCOLLECTIONURI") ?? "";
        var org = settings.Organization ?? ExtractOrgFromUri(collectionUri) ?? "";
        var project = settings.Project ?? Environment.GetEnvironmentVariable("SYSTEM_TEAMPROJECT") ?? "";
        var repo = settings.Repository ?? Environment.GetEnvironmentVariable("BUILD_REPOSITORY_NAME") ?? "";
        var prIdStr = Environment.GetEnvironmentVariable("SYSTEM_PULLREQUEST_PULLREQUESTID");
        var prId = settings.PullRequestId ?? (int.TryParse(prIdStr, out var p) ? p : 0);

        if (string.IsNullOrEmpty(org) || string.IsNullOrEmpty(project) ||
            string.IsNullOrEmpty(repo) || prId == 0)
        {
            AnsiConsole.MarkupLine("[yellow]Warning:[/] Incomplete DevOps context; skipping comment posting.");
            AnsiConsole.MarkupLine("[grey]Provide --org, --project, --repository, --pr-id or run inside Azure Pipelines.[/]");
            return null;
        }

        return new DevOpsContext(org, project, repo, prId);
    }

    // https://dev.azure.com/{org}/ → org
    private static string? ExtractOrgFromUri(string uri)
    {
        if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) return null;
        var segments = parsed.AbsolutePath.Trim('/').Split('/');
        return segments.Length > 0 && !string.IsNullOrEmpty(segments[0]) ? segments[0] : null;
    }

    private static int ComputeExitCode(ReviewResult result, string failOn)
    {
        if (failOn.Equals("none", StringComparison.OrdinalIgnoreCase)) return 0;

        var threshold = failOn.ToLowerInvariant() switch
        {
            "critical" => Severity.Critical,
            "high"     => Severity.High,
            "medium"   => Severity.Medium,
            "low"      => Severity.Low,
            _          => Severity.Critical
        };

        return result.Findings.Any(f => f.Severity >= threshold) ? 1 : 0;
    }
}
