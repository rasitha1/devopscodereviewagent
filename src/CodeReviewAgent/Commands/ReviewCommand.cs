using Azure.AI.OpenAI;
using Azure.Identity;
using CodeReviewAgent.Agent;
using CodeReviewAgent.DevOps;
using CodeReviewAgent.Diagnostics;
using CodeReviewAgent.Models;
using CodeReviewAgent.Reporting;
using Spectre.Console;
using Spectre.Console.Cli;
using System.ClientModel;

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

        var currentBranch = GetCurrentBranch(workingDir);

        AnsiConsole.Write(new Rule("[blue]AI Code Review[/]").RuleStyle("blue dim"));
        AnsiConsole.MarkupLine($"[grey]Endpoint  :[/] {settings.Endpoint}");
        AnsiConsole.MarkupLine($"[grey]Deployment:[/] {settings.Deployment}");
        AnsiConsole.MarkupLine($"[grey]Branch    :[/] {currentBranch} → {baseBranch}");
        AnsiConsole.MarkupLine($"[grey]Directory :[/] {workingDir}");
        if (devOpsContext is not null)
            AnsiConsole.MarkupLine($"[grey]PR comments:[/] {devOpsContext.Organization}/{devOpsContext.Project} PR#{devOpsContext.PullRequestId}");
        AnsiConsole.WriteLine();

        // DefaultAzureCredential works with AzureCLI task's service connection login
        var credential = new DefaultAzureCredential();

        if (settings.Verbose)
            await AuthDiagnostics.RunAsync(settings.Endpoint, settings.Deployment, credential, cancellationToken);

        var azureClient = new AzureOpenAIClient(new Uri(settings.Endpoint), credential);
        var chatClient = azureClient.GetChatClient(settings.Deployment);

        var orchestrator = new CodeReviewOrchestrator(chatClient, workingDir, baseBranch, settings);

        ReviewResult result;
        try
        {
            result = await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(Style.Parse("blue"))
                .StartAsync("Analyzing repository...", ctx => orchestrator.RunAsync(ctx, cancellationToken));
        }
        catch (ClientResultException ex)
        {
            AnsiConsole.WriteLine();
            return HandleClientError(ex, settings.Verbose);
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Rule("[blue]Findings[/]").RuleStyle("blue dim"));

        var consoleReporter = new ConsoleReporter();
        consoleReporter.Report(result);

        if (settings.DryRun)
        {
            var dryRunDir = Path.GetFullPath(settings.DryRunDir ?? Path.Combine(workingDir, "ai-review-dry-run"));
            AnsiConsole.WriteLine();
            AnsiConsole.Write(new Rule("[blue]Dry Run — DevOps Comment Preview[/]").RuleStyle("blue dim"));
            await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Writing comment files...", _ => DevOpsCommentReporter.DryRunAsync(result, dryRunDir));
            AnsiConsole.MarkupLine($"[green]Written to:[/] {dryRunDir}");
            AnsiConsole.MarkupLine($"[grey]{result.Findings.Count} finding file(s) + summary.md[/]");
        }
        else if (devOpsContext is not null)
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

        var exitCode = ComputeExitCode(result, settings.FailOn);
        if (exitCode != 0)
        {
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[red]Failing build:[/] findings at or above [bold]{settings.FailOn}[/] severity were detected (--fail-on {settings.FailOn}).");
        }
        return exitCode;
    }

    private static int HandleClientError(ClientResultException ex, bool verbose)
    {
        AnsiConsole.MarkupLine($"[red]Azure OpenAI request failed:[/] HTTP {ex.Status}");
        AnsiConsole.WriteLine();

        switch (ex.Status)
        {
            case 401:
                AnsiConsole.MarkupLine("[yellow]The identity does not have permission to call this Azure OpenAI resource.[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("Things to check:");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("  [bold]1. RBAC role[/]");
                AnsiConsole.MarkupLine("     The identity needs [bold]Cognitive Services OpenAI User[/] (or Contributor) on the Azure OpenAI resource.");
                AnsiConsole.MarkupLine("     Note: subscription- or resource-group-level assignments are not enough — the role must be on the resource itself.");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("  [bold]2. Verify the current identity[/]");
                AnsiConsole.WriteLine("     az account show");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("  [bold]3. List role assignments for the identity[/]");
                AnsiConsole.WriteLine("     az role assignment list --assignee <object-id> --scope <resource-id> --query \"[].roleDefinitionName\" -o table");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("  [bold]4. In Azure Pipelines[/]");
                AnsiConsole.MarkupLine("     Grant the service connection's managed identity the role in the Azure OpenAI resource's IAM blade.");
                AnsiConsole.WriteLine();
                if (!verbose)
                    AnsiConsole.MarkupLine("[grey]Tip: rerun with --verbose to see which identity DefaultAzureCredential resolved.[/]");
                break;

            case 403:
                AnsiConsole.MarkupLine("[yellow]Access forbidden (403). The identity is authenticated but blocked by a policy or network rule.[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("Things to check:");
                AnsiConsole.MarkupLine("  - Azure OpenAI resource firewall / virtual network rules");
                AnsiConsole.MarkupLine("  - Azure Policy denying the action");
                AnsiConsole.MarkupLine("  - Content filtering policy rejecting the request");
                break;

            case 404:
                AnsiConsole.MarkupLine("[yellow]Resource or deployment not found (404).[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("Things to check:");
                AnsiConsole.MarkupLine("  - The --endpoint URL matches the Azure OpenAI resource (not a generic Azure endpoint)");
                AnsiConsole.MarkupLine("  - The --deployment name matches a deployed model in that resource");
                AnsiConsole.MarkupLine("  - The deployment is in a 'Succeeded' state in the Azure portal");
                break;

            case 429:
                AnsiConsole.MarkupLine("[yellow]Rate limit or quota exceeded (429).[/]");
                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("Things to check:");
                AnsiConsole.MarkupLine("  - Tokens-per-minute (TPM) quota for the deployment");
                AnsiConsole.MarkupLine("  - Request-per-minute (RPM) limit");
                AnsiConsole.MarkupLine("  - Consider increasing the quota in Azure AI Foundry or using a deployment with higher capacity");
                break;

            default:
                AnsiConsole.MarkupLine("[grey]Details:[/]");
                AnsiConsole.WriteLine(Markup.Escape(ex.Message));
                break;
        }

        AnsiConsole.WriteLine();
        return 1;
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

    private static string GetCurrentBranch(string workingDir)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo("git", "branch --show-current")
            {
                WorkingDirectory = workingDir,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            var output = proc?.StandardOutput.ReadToEnd().Trim();
            proc?.WaitForExit();
            return string.IsNullOrEmpty(output) ? "(detached HEAD)" : output;
        }
        catch
        {
            return "(unknown)";
        }
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
