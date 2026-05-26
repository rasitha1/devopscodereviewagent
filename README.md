# ai-code-review

An AI-powered .NET code review agent that runs as a global CLI tool inside Azure Pipelines. It uses Azure OpenAI to autonomously explore a pull request, identify real issues, and post findings as inline comments on the PR.

## How it works

The tool runs an agentic loop powered by Semantic Kernel. Given an Azure OpenAI endpoint, it:

1. Uses `git diff` to discover what changed in the PR
2. Reads any repository review guidelines (`AGENTS.MD`, `CLAUDE.MD`, `.cursorrules`, etc.) and honours them
3. Reviews each changed file using targeted git and shell commands — it does not load entire files into context
4. Calls `report_finding` for every issue it finds (bugs, security vulnerabilities, performance problems, .NET anti-patterns)
5. Posts findings as inline threads on the Azure DevOps PR, grouped by file, plus a top-level summary comment

Authentication uses `DefaultAzureCredential` throughout — no API keys or PATs are needed.

## Installation

```bash
dotnet tool install --global rasitha.DevOpsCodeReviewAgent --version 1.0.0
```

To install from a private feed:

```bash
dotnet tool install --global rasitha.DevOpsCodeReviewAgent --version 1.0.0 \
  --add-source https://pkgs.dev.azure.com/<org>/<project>/_packaging/<feed>/nuget/v3/index.json
```

## Usage

```
ai-code-review [OPTIONS]

Options:
  --endpoint <URL>        Azure OpenAI endpoint URL                  (required)
  --deployment <NAME>     Azure OpenAI deployment name, e.g. gpt-4o  (required)
  --base-branch <BRANCH>  Branch to diff against (default: main, or auto-detected from pipeline)
  --working-dir <PATH>    Repository root (default: current directory)
  --devops-comment        Post findings as Azure DevOps PR comments
  --org <ORG>             Azure DevOps organization (auto-detected in pipeline)
  --project <PROJECT>     Azure DevOps project      (auto-detected in pipeline)
  --repository <REPO>     Repository name or ID     (auto-detected in pipeline)
  --pr-id <ID>            Pull request ID           (auto-detected in pipeline)
  --fail-on <SEVERITY>    Exit code 1 when findings reach this level: critical|high|medium|low|none (default: none)
  --verbose, -v           Print each tool call as the agent works, plus auth diagnostics
  --max-tokens <N>        Max tokens per LLM response (default: 4096)
```

## Local usage

Make sure you are logged in to Azure CLI with an identity that has the **Cognitive Services OpenAI User** role on your Azure OpenAI resource:

```bash
az login
```

Then run against any local git repository:

```bash
ai-code-review \
  --endpoint "https://my-resource.openai.azure.com/" \
  --deployment "gpt-4o" \
  --working-dir "/path/to/repo" \
  --base-branch "main" \
  --verbose
```

Use `--verbose` to print auth diagnostics (which identity was resolved, token audience, object ID) and each tool call the agent makes. Useful for diagnosing 401 errors.

## Azure Pipelines

Add to your PR validation pipeline. The `AzureCLI@2` task logs in with the service connection before the script runs, so `DefaultAzureCredential` and `AzureCliCredential` both resolve automatically — no secrets or PATs required.

```yaml
- task: AzureCLI@2
  displayName: Run AI Code Review
  inputs:
    azureSubscription: your-service-connection-name
    scriptType: bash
    scriptLocation: inlineScript
    inlineScript: |
      ai-code-review \
        --endpoint   "$(AZURE_OPENAI_ENDPOINT)" \
        --deployment "$(AZURE_OPENAI_DEPLOYMENT)" \
        --devops-comment \
        --fail-on high
  env:
    AZURE_OPENAI_ENDPOINT:   $(AZURE_OPENAI_ENDPOINT)
    AZURE_OPENAI_DEPLOYMENT: $(AZURE_OPENAI_DEPLOYMENT)
```

The tool auto-detects the PR context (base branch, PR ID, org, project, repository) from the Azure Pipelines environment variables that the platform sets on PR builds.

See [`azure-pipelines.yml`](azure-pipelines.yml) for a complete pipeline example with setup notes.

## Azure setup

### Azure OpenAI

The identity running the tool (your personal account locally, the service connection's service principal in pipelines) needs:

| Role | Scope |
|------|-------|
| **Cognitive Services OpenAI User** | The Azure OpenAI resource |

```bash
az role assignment create \
  --role "Cognitive Services OpenAI User" \
  --assignee "<object-id-or-upn>" \
  --scope "/subscriptions/<SUB>/resourceGroups/<RG>/providers/Microsoft.CognitiveServices/accounts/<RESOURCE>"
```

### Azure DevOps PR comments

For `--devops-comment` to work, the same service principal must also be added to the Azure DevOps organisation and granted **Contribute to pull requests** on the repository:

1. **Project Settings → Permissions** — add the service principal as a member
2. **Project Settings → Repos → \<repository\> → Security** — grant *Contribute to pull requests*

RBAC changes can take up to 5 minutes to propagate.

## Repository review guidelines

If the repository contains any of the following files, their contents are automatically injected into the agent's system prompt before the review starts:

| File | Purpose |
|------|---------|
| `AGENTS.MD` | Agent-specific instructions |
| `CLAUDE.MD` | Claude / AI coding guidelines |
| `.cursorrules` | Cursor IDE rules |
| `.github/copilot-instructions.md` | GitHub Copilot instructions |
| `REVIEW.MD` | Custom review focus areas |
| `CODEREVIEW.MD` | Custom review rules |

Use these files to tell the agent which patterns matter most in your codebase, areas to focus on or skip, or project-specific conventions.

## What the agent reviews

Issues are ranked by severity and reported with a specific file, line number, description, and suggested fix.

| Severity | Examples |
|----------|---------|
| **Critical** | SQL injection, hardcoded secrets, insecure deserialization |
| **High** | Missing authorization, async void, breaking API changes, N+1 queries |
| **Medium** | IDisposable not disposed, missing CancellationToken, wrong DI lifetime |
| **Low** | Missing ConfigureAwait, DateTime.Now vs DateTimeOffset.UtcNow |
| **Info** | General observations |

The agent deliberately skips: formatting, whitespace, naming conventions, auto-generated files (`*.g.cs`, `*.Designer.cs`, EF migrations), and issues in unchanged code.

## Building from source

```bash
git clone <repo-url>
cd CodeReviewAgent

dotnet build src/CodeReviewAgent/CodeReviewAgent.csproj
dotnet run --project src/CodeReviewAgent/CodeReviewAgent.csproj -- --help
```

To pack and install locally:

```bash
dotnet pack src/CodeReviewAgent/CodeReviewAgent.csproj -c Release -o ./nupkg
dotnet tool install --global --add-source ./nupkg rasitha.DevOpsCodeReviewAgent
```

## Tech stack

| Component | Technology |
|-----------|-----------|
| Runtime | .NET 10 |
| CLI | Spectre.Console.Cli |
| Agent loop | Semantic Kernel 1.76 with `FunctionChoiceBehavior.Auto()` |
| Auth | `DefaultAzureCredential` (Azure.Identity) |
| ADO comments | Azure DevOps REST API 7.1 via `AzureCliCredential` |
