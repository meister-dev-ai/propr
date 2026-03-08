using MeisterProPR.Application.Interfaces;
using MeisterProPR.Domain.Enums;
using MeisterProPR.Domain.ValueObjects;
using Microsoft.Extensions.Logging;

namespace MeisterProPR.Infrastructure.AzureDevOps;

/// <summary>
///     A stub implementation of <see cref="IPullRequestFetcher" /> for local development.
///     Returns a hardcoded fake pull request instead of contacting Azure DevOps.
///     Enable by setting <c>ADO_STUB_PR=true</c> in user secrets / environment variables.
/// </summary>
public sealed class StubPullRequestFetcher(ILogger<StubPullRequestFetcher> logger) : IPullRequestFetcher
{
    public Task<PullRequest> FetchAsync(
        string organizationUrl,
        string projectId,
        string repositoryId,
        int pullRequestId,
        int iterationId,
        CancellationToken cancellationToken = default)
    {
        logger.LogWarning(
            "ADO_STUB_PR is enabled – returning a fake pull request for job PR#{PrId}. " +
            "No real Azure DevOps connection will be made.",
            pullRequestId);

        var changedFiles = new List<ChangedFile>
        {
            new(
                "/src/ExampleService.cs",
                ChangeType.Edit,
                """
                public class ExampleService
                {
                    public string GetGreeting(string name)
                    {
                        // TODO: remove hardcoded string
                        return "Hello, " + name + "!";
                    }

                    public int Divide(int a, int b)
                    {
                        return a / b; // potential divide-by-zero
                    }
                }
                """,
                """
                --- a/src/ExampleService.cs
                +++ b/src/ExampleService.cs
                @@ -1,8 +1,12 @@
                 public class ExampleService
                 {
                -    public string GetGreeting() => "Hello!";
                +    public string GetGreeting(string name)
                +    {
                +        // TODO: remove hardcoded string
                +        return "Hello, " + name + "!";
                +    }
                +
                +    public int Divide(int a, int b)
                +    {
                +        return a / b; // potential divide-by-zero
                +    }
                 }
                """),

            new(
                "/src/NewHelper.cs",
                ChangeType.Add,
                """
                public static class NewHelper
                {
                    public static bool IsNullOrEmpty(string? value) => string.IsNullOrEmpty(value);
                }
                """,
                """
                --- /dev/null
                +++ b/src/NewHelper.cs
                @@ -0,0 +1,4 @@
                +public static class NewHelper
                +{
                +    public static bool IsNullOrEmpty(string? value) => string.IsNullOrEmpty(value);
                +}
                """),
        };

        var pr = new PullRequest(
            organizationUrl,
            projectId,
            repositoryId,
            pullRequestId,
            iterationId,
            "[STUB] Add greeting and helper utilities",
            "This is a fake PR used for local development. It contains intentional issues for the AI to find.",
            "feature/stub-branch",
            "main",
            changedFiles.AsReadOnly());

        return Task.FromResult(pr);
    }
}