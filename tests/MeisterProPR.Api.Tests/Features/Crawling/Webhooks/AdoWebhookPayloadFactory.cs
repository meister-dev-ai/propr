// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Text.Json;

namespace MeisterProPR.Api.Tests.Features.Crawling.Webhooks;

/// <summary>Factory helpers for Azure DevOps webhook payload fixtures.</summary>
public static class AdoWebhookPayloadFactory
{
    public static JsonDocument PullRequestCreated(int pullRequestId = 42, string repositoryId = "repo-1")
    {
        return Create("git.pullrequest.created", pullRequestId, repositoryId, "active");
    }

    public static JsonDocument PullRequestUpdated(
        int pullRequestId = 42,
        string repositoryId = "repo-1",
        string status = "active")
    {
        return Create("git.pullrequest.updated", pullRequestId, repositoryId, status);
    }

    public static JsonDocument PullRequestCommented(int pullRequestId = 42, string repositoryId = "repo-1")
    {
        return Create("ms.vss-code.git-pullrequest-comment-event", pullRequestId, repositoryId, "active");
    }

    public static JsonDocument PullRequestAbandoned(int pullRequestId = 42, string repositoryId = "repo-1")
    {
        return Create("git.pullrequest.updated", pullRequestId, repositoryId, "abandoned");
    }

    private static JsonDocument Create(string eventType, int pullRequestId, string repositoryId, string status)
    {
        return JsonDocument.Parse(
            $$"""
              {
                "eventType": "{{eventType}}",
                "resource": {
                  "pullRequestId": {{pullRequestId}},
                  "repository": {
                    "id": "{{repositoryId}}",
                    "name": "Sample Repository"
                  },
                  "sourceRefName": "refs/heads/feature/test",
                  "targetRefName": "refs/heads/main",
                  "status": "{{status}}"
                }
              }
              """);
    }
}
