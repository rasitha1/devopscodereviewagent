namespace CodeReviewAgent.DevOps;

public sealed record DevOpsContext(
    string Organization,
    string Project,
    string Repository,
    int PullRequestId);
