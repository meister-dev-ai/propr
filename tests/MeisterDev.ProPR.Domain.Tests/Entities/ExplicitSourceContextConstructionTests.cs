// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Domain.Tests.Entities;

public sealed class ExplicitSourceContextConstructionTests
{
    [Theory]
    [InlineData("review")]
    [InlineData("mention")]
    [InlineData("thread")]
    public void CapturedContextDoesNotBypassSavedScopeValidation(string jobKind)
    {
        var review = new CodeReviewRef(
            new RepositoryRef(
                new ProviderHostRef(
                    (ScmProvider)792,
                    "https://synthetic.example"), "repo", "owner", "owner/project"),
            CodeReviewPlatformKind.PullRequest, "42", 42);
        var client = Guid.NewGuid();

        Assert.Throws<ArgumentException>(() =>
        {
            switch (jobKind)
            {
                case "review":
                    _ = new ReviewJob(review, Guid.NewGuid(), client, "not-an-absolute-url", "project", "repo", 42, 1);
                    break;
                case "mention":
                    _ = new MentionReplyJob(review, Guid.NewGuid(), client, "not-an-absolute-url", "project", "repo", 42, "thread", 1, "question");
                    break;
                case "thread":
                    _ = new ThreadPassJob(review, Guid.NewGuid(), client, "not-an-absolute-url", "project", "repo", 42, 1, "revision", "trigger");
                    break;
            }
        });
    }
}
