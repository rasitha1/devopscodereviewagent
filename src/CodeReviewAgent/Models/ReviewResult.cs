namespace CodeReviewAgent.Models;

public sealed record ReviewResult(
    IReadOnlyList<Finding> Findings,
    string Summary);
