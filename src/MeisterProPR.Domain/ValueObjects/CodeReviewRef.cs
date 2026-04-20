// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>Stable provider-neutral code-review identity.</summary>
public sealed record CodeReviewRef
{
    /// <summary>
    /// Initializes a new instance of the <see cref="CodeReviewRef"/> class.
    /// </summary>
    /// <param name="repository">The repository reference.</param>
    /// <param name="platform">The code review platform kind.</param>
    /// <param name="externalReviewId">The external review identifier.</param>
    /// <param name="number">The review number.</param>
    public CodeReviewRef(RepositoryRef repository, CodeReviewPlatformKind platform, string externalReviewId, int number)
    {
        this.Repository = repository ?? throw new ArgumentNullException(nameof(repository));
        ArgumentException.ThrowIfNullOrWhiteSpace(externalReviewId);
        ArgumentOutOfRangeException.ThrowIfLessThan(number, 1);

        this.Platform = platform;
        this.ExternalReviewId = externalReviewId.Trim();
        this.Number = number;
    }

    /// <summary>Gets the repository reference.</summary>
    public RepositoryRef Repository { get; }

    /// <summary>Gets the code review platform kind.</summary>
    public CodeReviewPlatformKind Platform { get; }

    /// <summary>Gets the external review identifier.</summary>
    public string ExternalReviewId { get; }

    /// <summary>Gets the review number.</summary>
    public int Number { get; }
}
