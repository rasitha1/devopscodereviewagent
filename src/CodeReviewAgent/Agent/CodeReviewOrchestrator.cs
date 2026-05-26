using CodeReviewAgent.Commands;
using CodeReviewAgent.Models;
using CodeReviewAgent.Plugins;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using Spectre.Console;

namespace CodeReviewAgent.Agent;

public sealed class CodeReviewOrchestrator
{
    private readonly ChatClient _chatClient;
    private readonly string _workingDirectory;
    private readonly string _baseBranch;
    private readonly ReviewSettings _settings;

    public CodeReviewOrchestrator(
        ChatClient chatClient,
        string workingDirectory,
        string baseBranch,
        ReviewSettings settings)
    {
        _chatClient = chatClient;
        _workingDirectory = workingDirectory;
        _baseBranch = baseBranch;
        _settings = settings;
    }

    public async Task<ReviewResult> RunAsync(StatusContext statusContext, CancellationToken cancellationToken = default)
    {
        var shellPlugin = new ShellPlugin(_workingDirectory, statusContext);
        var reporterPlugin = new ReviewReporterPlugin(statusContext);

        // Name overrides keep the tool names consistent with the system prompt
        AIFunction[] tools =
        [
            AIFunctionFactory.Create(shellPlugin.RunCommandAsync, "run_command"),
            AIFunctionFactory.Create(reporterPlugin.ReportFinding, "report_finding")
        ];

        var promptBuilder = new SystemPromptBuilder(_workingDirectory, _baseBranch);
        var systemPrompt = await promptBuilder.BuildAsync();

        var agent = _chatClient.AsAIAgent(
            instructions: systemPrompt,
            tools: tools);

        var session = await agent.CreateSessionAsync();

        var userMessage =
            $"Review all code changes in this repository compared to `{_baseBranch}`. " +
            "Discover changed files with git, review each one, report every finding with report_finding, " +
            "then write a brief summary.";

        statusContext.Status("Code review agent running...");

        var runOptions = new ChatClientAgentRunOptions(new ChatOptions { MaxOutputTokens = _settings.MaxTokens });
        var response = await agent.RunAsync(userMessage, session, runOptions, cancellationToken);

        return new ReviewResult(
            Findings: reporterPlugin.Findings,
            Summary: response.Text ?? string.Empty);
    }
}
