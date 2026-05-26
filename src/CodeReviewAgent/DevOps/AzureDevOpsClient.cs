using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Azure.Identity;

namespace CodeReviewAgent.DevOps;

/// <summary>
/// Posts pull request thread comments to Azure DevOps using the Azure CLI credential
/// (the same service connection identity that is active inside an AzureCLI pipeline task).
/// </summary>
public sealed class AzureDevOpsClient : IDisposable
{
    // Well-known resource ID for the Azure DevOps REST API
    private const string AzureDevOpsResource = "499b84ac-1321-427f-aa17-267ca6975798";

    private readonly HttpClient _http = new();
    private readonly AzureCliCredential _credential = new();
    private readonly string _baseUrl;

    public AzureDevOpsClient(DevOpsContext ctx)
    {
        var org = Uri.EscapeDataString(ctx.Organization);
        var project = Uri.EscapeDataString(ctx.Project);
        var repo = Uri.EscapeDataString(ctx.Repository);
        _baseUrl =
            $"https://dev.azure.com/{org}/{project}/_apis/git/repositories/{repo}" +
            $"/pullRequests/{ctx.PullRequestId}/threads?api-version=7.1";
    }

    /// <summary>
    /// Posts a markdown comment.
    /// When filePath is provided the thread is anchored to that file in the diff view.
    /// When line is also provided the thread is anchored to that specific line.
    /// </summary>
    public async Task PostCommentAsync(string markdownContent, string? filePath = null, int? line = null, CancellationToken cancellationToken = default)
    {
        var token = await GetTokenAsync(cancellationToken);

        var body = BuildBody(markdownContent, filePath, line);

        using var request = new HttpRequestMessage(HttpMethod.Post, _baseUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(
            body.ToJsonString(),
            Encoding.UTF8,
            "application/json");

        using var response = await _http.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        var ctx = new TokenRequestContext([$"{AzureDevOpsResource}/.default"]);
        var result = await _credential.GetTokenAsync(ctx, cancellationToken);
        return result.Token;
    }

    private static JsonObject BuildBody(string content, string? filePath, int? line)
    {
        var comment = new JsonObject
        {
            ["parentCommentId"] = 0,
            ["content"] = content,
            ["commentType"] = 1
        };

        var body = new JsonObject
        {
            ["comments"] = new JsonArray { comment },
            ["status"] = 1   // Active
        };

        if (string.IsNullOrEmpty(filePath)) return body;

        // ADO expects paths with a leading slash and forward slashes
        var normalised = "/" + filePath.Replace('\\', '/').TrimStart('/');

        var threadContext = new JsonObject { ["filePath"] = normalised };

        if (line.HasValue)
        {
            threadContext["rightFileStart"] = new JsonObject { ["line"] = line.Value, ["offset"] = 1 };
            threadContext["rightFileEnd"] = new JsonObject { ["line"] = line.Value, ["offset"] = 1 };
        }

        body["threadContext"] = threadContext;
        return body;
    }

    public void Dispose() => _http.Dispose();
}
