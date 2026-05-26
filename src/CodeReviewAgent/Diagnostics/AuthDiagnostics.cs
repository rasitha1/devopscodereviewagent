using System.Text;
using System.Text.Json.Nodes;
using Azure.Core;
using Spectre.Console;

namespace CodeReviewAgent.Diagnostics;

public static class AuthDiagnostics
{
    // The scope required to call Azure OpenAI
    private const string AzureOpenAIScope = "https://cognitiveservices.azure.com/.default";

    /// <summary>
    /// Acquires a token using the supplied credential, decodes the JWT claims, and
    /// prints the endpoint, principal identity, tenant, and audience to the console.
    /// Useful for confirming which identity DefaultAzureCredential resolved and
    /// whether that identity has access to the target resource.
    /// </summary>
    public static async Task RunAsync(string endpoint, string deployment, TokenCredential credential, CancellationToken cancellationToken = default)
    {
        AnsiConsole.Write(new Rule("[grey]Auth Diagnostics[/]").RuleStyle("grey dim"));

        AnsiConsole.MarkupLine($"[grey]Endpoint  :[/] {Markup.Escape(endpoint)}");
        AnsiConsole.MarkupLine($"[grey]Deployment:[/] {Markup.Escape(deployment)}");
        AnsiConsole.MarkupLine($"[grey]Scope     :[/] {AzureOpenAIScope}");
        AnsiConsole.WriteLine();

        try
        {
            var ctx = new TokenRequestContext([AzureOpenAIScope]);
            var token = await credential.GetTokenAsync(ctx, cancellationToken);

            AnsiConsole.MarkupLine("[green]Token acquired successfully[/]");
            AnsiConsole.MarkupLine($"[grey]Expires   :[/] {token.ExpiresOn:u}");

            var claims = DecodeJwtPayload(token.Token);
            if (claims is not null)
            {
                // User accounts have "upn" or "unique_name"; service principals have "appid" + "oid"
                var principal =
                    GetString(claims, "upn") ??
                    GetString(claims, "unique_name") ??
                    GetString(claims, "preferred_username") ??
                    $"appid:{GetString(claims, "appid") ?? "unknown"}";

                var oid      = GetString(claims, "oid")  ?? "n/a";
                var tid      = GetString(claims, "tid")  ?? "n/a";
                var audience = GetAudience(claims);
                var credType = GetString(claims, "idtyp") ?? InferCredentialType(claims);

                AnsiConsole.MarkupLine($"[grey]Principal :[/] {Markup.Escape(principal)}");
                AnsiConsole.MarkupLine($"[grey]Object ID :[/] {Markup.Escape(oid)}");
                AnsiConsole.MarkupLine($"[grey]Tenant ID :[/] {Markup.Escape(tid)}");
                AnsiConsole.MarkupLine($"[grey]Audience  :[/] {Markup.Escape(audience)}");
                AnsiConsole.MarkupLine($"[grey]Cred type :[/] {Markup.Escape(credType)}");

                AnsiConsole.WriteLine();

                var audienceOk = audience.Contains("cognitiveservices.azure.com", StringComparison.OrdinalIgnoreCase);
                if (!audienceOk)
                {
                    AnsiConsole.MarkupLine("[yellow]Warning:[/] Token audience does not include cognitiveservices.azure.com.");
                    AnsiConsole.MarkupLine("[grey]The token may not be accepted by Azure OpenAI.[/]");
                }
                else
                {
                    AnsiConsole.MarkupLine("[grey]Audience looks correct for Azure OpenAI.[/]");
                }

                AnsiConsole.WriteLine();
                AnsiConsole.MarkupLine("[grey]To verify the RBAC role for this principal, run:[/]");
                AnsiConsole.MarkupLine($"[grey]  az role assignment list --assignee {Markup.Escape(oid)} --query \"[].roleDefinitionName\" -o table[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Token acquisition failed:[/] {Markup.Escape(ex.Message)}");
            AnsiConsole.MarkupLine("[grey]Make sure you are logged in: az login[/]");
        }

        AnsiConsole.Write(new Rule().RuleStyle("grey dim"));
        AnsiConsole.WriteLine();
    }

    private static JsonObject? DecodeJwtPayload(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;

        // JWT uses base64url — restore standard base64 padding
        var base64 = parts[1].Replace('-', '+').Replace('_', '/');
        base64 = (base64.Length % 4) switch
        {
            2 => base64 + "==",
            3 => base64 + "=",
            _ => base64
        };

        var json = Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        return JsonNode.Parse(json) as JsonObject;
    }

    private static string? GetString(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) ? node?.GetValue<string>() : null;

    private static string GetAudience(JsonObject claims)
    {
        if (!claims.TryGetPropertyValue("aud", out var aud) || aud is null)
            return "n/a";

        // aud can be a string or an array of strings
        return aud is JsonArray arr
            ? string.Join(", ", arr.Select(a => a?.GetValue<string>() ?? ""))
            : aud.GetValue<string>();
    }

    private static string InferCredentialType(JsonObject claims)
    {
        // If there's an "appid" but no "upn", it's likely a service principal / managed identity
        var hasUpn   = claims.ContainsKey("upn") || claims.ContainsKey("unique_name");
        var hasAppId = claims.ContainsKey("appid");

        if (hasUpn)   return "user";
        if (hasAppId) return "service principal / managed identity";
        return "unknown";
    }
}
