using System.ComponentModel;
using Spectre.Console.Cli;

namespace CodeReviewAgent.Commands;

public sealed class ReviewSettings : CommandSettings
{
    [CommandOption("--endpoint <ENDPOINT>")]
    [Description("Azure OpenAI endpoint URL (e.g. https://my-resource.openai.azure.com/)")]
    public required string Endpoint { get; init; }

    [CommandOption("--deployment <DEPLOYMENT>")]
    [Description("Azure OpenAI deployment name (e.g. gpt-4o)")]
    public required string Deployment { get; init; }

    [CommandOption("--base-branch <BRANCH>")]
    [Description("Base branch to diff against. Auto-detected from SYSTEM_PULLREQUEST_TARGETBRANCH pipeline var if not set.")]
    public string? BaseBranch { get; init; }

    [CommandOption("--working-dir <DIR>")]
    [Description("Repository root (defaults to current directory)")]
    public string? WorkingDirectory { get; init; }

    [CommandOption("--devops-comment")]
    [Description("Post findings as inline threads on the Azure DevOps PR")]
    [DefaultValue(false)]
    public bool PostToDevOps { get; init; }

    [CommandOption("--org <ORG>")]
    [Description("Azure DevOps organization name. Auto-detected from SYSTEM_TEAMFOUNDATIONCOLLECTIONURI.")]
    public string? Organization { get; init; }

    [CommandOption("--project <PROJECT>")]
    [Description("Azure DevOps project name. Auto-detected from SYSTEM_TEAMPROJECT.")]
    public string? Project { get; init; }

    [CommandOption("--repository <REPO>")]
    [Description("Azure DevOps repository name or ID. Auto-detected from BUILD_REPOSITORY_NAME.")]
    public string? Repository { get; init; }

    [CommandOption("--pr-id <ID>")]
    [Description("Pull request ID. Auto-detected from SYSTEM_PULLREQUEST_PULLREQUESTID.")]
    public int? PullRequestId { get; init; }

    [CommandOption("--fail-on <SEVERITY>")]
    [Description("Exit with code 1 when any finding meets this severity: critical|high|medium|low|none")]
    [DefaultValue("none")]
    public string FailOn { get; init; } = "none";

    [CommandOption("--verbose|-v")]
    [Description("Print each tool call name as the agent works")]
    [DefaultValue(false)]
    public bool Verbose { get; init; }

    [CommandOption("--max-tokens <N>")]
    [Description("Maximum tokens per LLM response")]
    [DefaultValue(4096)]
    public int MaxTokens { get; init; } = 4096;
}
