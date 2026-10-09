// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Tests.Architecture;

internal static class JobConstructionReflectionFixtures
{
    internal static class AdditionalProducer
    {
        public static ReviewJob Build(CodeReviewRef review) => new(review, Guid.NewGuid(), Guid.NewGuid(), "https://scm.example", "project", "repo", 42, 7);
    }

    internal static class ReviewOnlyProducer
    {
        public static ReviewJob[] Build(CodeReviewRef review) =>
        [
            new(review, Guid.NewGuid(), Guid.NewGuid(), "https://scm.example", "project", "repo", 42, 7),
            new(review, Guid.NewGuid(), Guid.NewGuid(), "https://scm.example", "project", "repo", 42, 7),
            new(review, Guid.NewGuid(), Guid.NewGuid(), "https://scm.example", "project", "repo", 42, 7),
            new(review, Guid.NewGuid(), Guid.NewGuid(), "https://scm.example", "project", "repo", 42, 7),
            new(review, Guid.NewGuid(), Guid.NewGuid(), "https://scm.example", "project", "repo", 42, 7),
        ];
    }

    internal static class MixedProducer
    {
        public static object[] Build(CodeReviewRef review) =>
        [
            new ReviewJob(review, Guid.NewGuid(), Guid.NewGuid(), "https://scm.example", "project", "repo", 42, 7),
            new ThreadPassJob(review, Guid.NewGuid(), Guid.NewGuid(), "https://scm.example", "project", "repo", 42, 7, "revision", "trigger"),
            new MentionReplyJob(review, Guid.NewGuid(), Guid.NewGuid(), "https://scm.example", "project", "repo", 42, "thread", 1, "question"),
            new ReviewJob(Guid.NewGuid(), Guid.NewGuid(), "https://scm.example", "project", "repo", 42, 7),
        ];
    }
}
