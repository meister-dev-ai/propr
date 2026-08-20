// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;

namespace MeisterDev.ProPR.Application.Options;

/// <summary>
///     How much history and content a mirror fetch brings down.
/// </summary>
public static class ReviewWorkspaceFetchDepthPolicies
{
    /// <summary>Fetch commits, trees and file contents in full.</summary>
    public const string Full = "full";

    /// <summary>
    ///     Fetch commits and trees but leave file contents on the server, to be downloaded on demand.
    ///     Requires a server that advertises the <c>filter</c> capability, which Azure DevOps and GitHub both
    ///     do.
    /// </summary>
    /// <remarks>
    ///     What this saves is the file contents of the revisions no review checks out. The head revision is
    ///     checked out by every review, and checking out a partial clone downloads the contents of the files
    ///     in that revision, so the saving comes from the history behind a repository rather than from its
    ///     size at one commit. The checkout is what the repository search and the head-side file reads work
    ///     against, so it is not avoidable while those read the filesystem.
    /// </remarks>
    public const string Blobless = "blobless";

    /// <summary>
    ///     Fetch a bounded number of commits. The depth has to exceed the divergence of the pull requests
    ///     being reviewed, because a merge base outside the fetched history cannot be resolved, which is why
    ///     <see cref="Blobless" /> is the better way to keep a mirror small.
    /// </summary>
    public const string Shallow = "shallow";

    /// <summary>Every accepted value. Read-only: the validation contract is not a mutable list.</summary>
    public static readonly IReadOnlyList<string> All = [Full, Blobless, Shallow];
}

/// <summary>
///     Configures local git-backed review workspaces used to reduce repeated provider repository reads.
/// </summary>
public sealed class ReviewWorkspaceOptions : IValidatableObject
{
    /// <summary>
    ///     Configuration section name.
    /// </summary>
    public const string SectionName = "ReviewWorkspace";

    /// <summary>
    ///     Gets or sets the writable filesystem root that stores mirrors and per-review workspaces.
    /// </summary>
    [Required]
    public string RootPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify),
        "meisterpropr",
        "review-workspaces");

    /// <summary>
    ///     Gets or sets how long released workspaces are retained before cleanup is allowed.
    /// </summary>
    [Range(1, 7 * 24 * 60)]
    public int RetentionMinutes { get; set; } = 180;

    /// <summary>
    ///     Gets or sets the maximum cache size for the workspace root in megabytes.
    /// </summary>
    [Range(128, 1024 * 1024)]
    public int MaxCacheSizeMegabytes { get; set; } = 4096;

    /// <summary>
    ///     Gets or sets the maximum number of concurrent workspace preparation operations. Each preparation
    ///     fetches into a mirror and writes a full checkout, so this bounds how much of the workspace disk is
    ///     being written at once.
    /// </summary>
    [Range(1, 128)]
    public int MaxConcurrentPreparations { get; set; } = 4;

    /// <summary>
    ///     Gets or sets what a mirror fetch brings down, as one of
    ///     <see cref="ReviewWorkspaceFetchDepthPolicies.All" />.
    /// </summary>
    [Required]
    public string FetchDepthPolicy { get; set; } = ReviewWorkspaceFetchDepthPolicies.Full;

    /// <summary>
    ///     Gets or sets how many commits a fetch brings down under the
    ///     <see cref="ReviewWorkspaceFetchDepthPolicies.Shallow" /> policy. Ignored by the other policies, and
    ///     the range is checked only under that one, so a deployment that shares one configuration across
    ///     policies is not refused for a value nothing reads. A depth shorter than a reviewed pull request's
    ///     divergence from its target leaves the merge base outside the fetched history, and the review then
    ///     fails to prepare.
    /// </summary>
    public int FetchDepth { get; set; } = 200;

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        // An unrecognised policy used to bind, validate and deploy without changing anything the workspace
        // manager did, and without reporting an error, so a policy name that was never implemented looked
        // like an applied mitigation. Refusing it at startup reports it while the value can still be
        // corrected.
        if (!ReviewWorkspaceFetchDepthPolicies.All.Contains(this.FetchDepthPolicy, StringComparer.OrdinalIgnoreCase))
        {
            yield return new ValidationResult(
                "FetchDepthPolicy (REVIEW_WORKSPACE_FETCH_DEPTH_POLICY) must be one of: "
                + $"{string.Join(", ", ReviewWorkspaceFetchDepthPolicies.All)}.",
                [nameof(this.FetchDepthPolicy)]);
        }

        if (string.Equals(this.FetchDepthPolicy, ReviewWorkspaceFetchDepthPolicies.Shallow, StringComparison.OrdinalIgnoreCase)
            && this.FetchDepth is < 1 or > 100_000)
        {
            yield return new ValidationResult(
                "FetchDepth (REVIEW_WORKSPACE_FETCH_DEPTH) must be between 1 and 100000 under the "
                + $"{ReviewWorkspaceFetchDepthPolicies.Shallow} policy.",
                [nameof(this.FetchDepth)]);
        }
    }
}
