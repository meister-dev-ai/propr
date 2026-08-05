// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Entities;
using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Domain.Tests.Entities;

/// <summary>
///     The thread identity carries this record's deduplication key, so validating it is not tidiness. Records
///     missing one all collide on the same key within a pull request, and the store keeps the first and drops
///     the rest as duplicates of each other.
/// </summary>
public class PostedFindingRecordTests
{
    private static PostedFindingRecord CreateRecord(string providerThreadId = "17")
    {
        return new PostedFindingRecord
        {
            Id = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            RepositoryId = "repo-1",
            PullRequestId = 42,
            ProviderThreadId = providerThreadId,
            ReviewJobId = Guid.NewGuid(),
            IterationId = 1,
            Severity = CommentSeverity.Warning,
            FindingMessage = "The cast is unchecked.",
            EmbeddingVector = [0.1f, 0.2f],
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public void Validate_WithEveryFieldPresent_Passes()
    {
        CreateRecord().Validate();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_WithoutAThreadIdentity_IsRejected(string providerThreadId)
    {
        var record = CreateRecord(providerThreadId);

        var error = Assert.Throws<ArgumentException>(record.Validate);
        Assert.Contains("ProviderThreadId", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The stored column is bounded. A value the database would refuse is refused here, so the failure names
    ///     the field rather than arriving as a truncation error from the insert.
    /// </summary>
    [Fact]
    public void Validate_WithAThreadIdentityLongerThanTheColumn_IsRejected()
    {
        var record = CreateRecord(new string('t', 257));

        var error = Assert.Throws<ArgumentException>(record.Validate);
        Assert.Contains("ProviderThreadId", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WithAThreadIdentityExactlyAtTheColumnBound_Passes()
    {
        CreateRecord(new string('t', 256)).Validate();
    }

    /// <summary>
    ///     A provider thread identity is whatever the provider writes: Azure DevOps numbers them, GitHub and
    ///     GitLab use opaque strings. Nothing here may assume a numeric shape, which the previous invariant did.
    /// </summary>
    [Theory]
    [InlineData("17")]
    [InlineData("PRRT_kwDOAbCdEf4Ag1Hj")]
    [InlineData("a1b2c3d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0")]
    public void Validate_WithAnyProviderShapeOfThreadIdentity_Passes(string providerThreadId)
    {
        CreateRecord(providerThreadId).Validate();
    }
}
