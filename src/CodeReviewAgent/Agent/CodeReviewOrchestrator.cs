using CodeReviewAgent.Commands;
using CodeReviewAgent.Models;
using CodeReviewAgent.Plugins;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.AzureOpenAI;
using Spectre.Console;

namespace CodeReviewAgent.Agent;

public sealed class CodeReviewOrchestrator
{
    private readonly Kernel _kernel;
    private readonly string _workingDirectory;
    private readonly string _baseBranch;
    private readonly ReviewSettings _settings;

    public CodeReviewOrchestrator(
        Kernel kernel,
        string workingDirectory,
        string baseBranch,
        ReviewSettings settings)
    {
        _kernel = kernel;
        _workingDirectory = workingDirectory;
        _baseBranch = baseBranch;
        _settings = settings;
    }

    public async Task<ReviewResult> RunAsync(StatusContext statusContext, CancellationToken cancellationToken = default)
    {
        // Register plugins on this kernel instance
        var shellPlugin = new ShellPlugin(_workingDirectory, statusContext);
        var reporterPlugin = new ReviewReporterPlugin(statusContext);

        _kernel.ImportPluginFromObject(shellPlugin, "Shell");
        _kernel.ImportPluginFromObject(reporterPlugin, "Review");

        if (_settings.Verbose)
            _kernel.FunctionInvocationFilters.Add(new VerboseFilter(statusContext));

        // Build system prompt (pre-injects AGENTS.MD / CLAUDE.MD content if present)
        var promptBuilder = new SystemPromptBuilder(_workingDirectory, _baseBranch);
        var systemPrompt = await promptBuilder.BuildAsync();

        var chatHistory = new ChatHistory(systemPrompt);
        chatHistory.AddUserMessage(
            $"Review all code changes in this repository compared to `{_baseBranch}`. " +
            "Discover changed files with git, review each one, report every finding with report_finding, " +
            "then write a brief summary.");

        // FunctionChoiceBehavior.Auto() → kernel auto-invokes all registered plugins
        // until the model stops requesting tool calls.
        var executionSettings = new AzureOpenAIPromptExecutionSettings
        {
            FunctionChoiceBehavior = FunctionChoiceBehavior.Auto(),
            MaxTokens = _settings.MaxTokens
        };

        statusContext.Status("Code review agent running...");

        var chatService = _kernel.GetRequiredService<IChatCompletionService>();
        var response = await chatService.GetChatMessageContentAsync(
            chatHistory, executionSettings, _kernel, cancellationToken);

        return new ReviewResult(
            Findings: reporterPlugin.Findings,
            Summary: response.Content ?? string.Empty);
    }

    // Logs the name of each tool call to the spinner status line
    private sealed class VerboseFilter : IFunctionInvocationFilter
    {
        private readonly StatusContext _ctx;
        public VerboseFilter(StatusContext ctx) => _ctx = ctx;

        public async Task OnFunctionInvocationAsync(
            FunctionInvocationContext context,
            Func<FunctionInvocationContext, Task> next)
        {
            _ctx.Status($"[grey]{context.Function.PluginName}.{context.Function.Name}[/]");
            await next(context);
        }
    }
}
