// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.ComponentModel.DataAnnotations;

namespace MeisterProPR.Application.Options;

/// <summary>
///     Configures local git-backed review workspaces used to reduce repeated provider repository reads.
/// </summary>
public sealed class ReviewWorkspaceOptions
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
    ///     Gets or sets the maximum number of concurrent workspace preparation operations.
    /// </summary>
    [Range(1, 128)]
    public int MaxConcurrentPreparations { get; set; } = 4;

    /// <summary>
    ///     Gets or sets the fetch depth policy label used by the workspace manager.
    /// </summary>
    [Required]
    public string FetchDepthPolicy { get; set; } = "full";
}
