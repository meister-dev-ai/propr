// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>
///     Summarizes a single changed file within a pull request, including the type of change.
/// </summary>
/// <param name="Path">Repository-relative path of the changed file.</param>
/// <param name="ChangeType">The type of change applied to the file.</param>
/// <param name="OriginalPath">
///     The path of the file at the base commit when <paramref name="ChangeType" /> is <see cref="ChangeType.Rename" />
///     and the listing contains it; otherwise <see langword="null" />.
/// </param>
public sealed record ChangedFileSummary(string Path, ChangeType ChangeType, string? OriginalPath = null);
