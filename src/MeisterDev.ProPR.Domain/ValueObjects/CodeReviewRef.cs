// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>Provider-neutral code-review identity with an external identifier and a native review number.</summary>
/// <remarks>Installed provider adapters require a positive native pull request or merge request number. The external identifier does not enable opaque-only review operations.</remarks>
public sealed record CodeReviewRef
{
    /// <summary>
    ///     Initializes a new instance of the <see cref="CodeReviewRef" /> class.
    /// </summary>
    /// <param name="repository">The repository reference.</param>
    /// <param name="platform">The code review platform kind.</param>
    /// <param name="externalReviewId">The external review identifier.</param>
    /// <param name="number">The positive provider-native pull request or merge request number.</param>
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

    /// <summary>Gets the positive provider-native pull request or merge request number.</summary>
    public int Number { get; }
}
