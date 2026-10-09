// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>A filtered page of persisted canonical repository targets.</summary>
/// <param name="Items">Saved target rows for this page.</param>
/// <param name="TotalCount">Number of targets matching the filters.</param>
/// <param name="Page">One-based page number.</param>
/// <param name="PageSize">Maximum rows in the page.</param>
/// <param name="SnapshotVersion">Opaque version of the entire matched management representation.</param>
public sealed record CrawlConfigurationPageDto(IReadOnlyList<CrawlConfigurationDto> Items, int TotalCount, int Page, int PageSize, string SnapshotVersion = "");
