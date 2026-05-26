namespace CodeReviewAgent.Models;

public sealed record Finding(
    Severity Severity,
    FindingCategory Category,
    string File,
    int? Line,
    string Title,
    string Description,
    string Suggestion);
