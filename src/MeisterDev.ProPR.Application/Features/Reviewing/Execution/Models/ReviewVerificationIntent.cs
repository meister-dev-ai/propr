// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Statements of intent that the evidence-backed judge weighs against a finding: the pull request title and
///     description, the linked work items or issues that the review already collected, and the unified diff of the
///     reviewed file, from which the judge receives the hunk at the claim's anchor. Any field may be empty. The judge
///     then decides on the code and its comments alone.
/// </summary>
/// <param name="PullRequestTitle">Title of the pull request.</param>
/// <param name="PullRequestDescription">Description of the pull request, or <see langword="null" /> when it has none.</param>
/// <param name="LinkedItems">Linked work items or issues, already bounded by the review. Empty when none were attached.</param>
/// <param name="ReviewedFilePath">Path of the file whose findings are verified.</param>
/// <param name="ReviewedFileDiff">Unified diff of that file, or <see langword="null" /> for a binary file.</param>
public sealed record ReviewVerificationIntent(
    string? PullRequestTitle,
    string? PullRequestDescription,
    IReadOnlyList<LinkedItem> LinkedItems,
    string? ReviewedFilePath,
    string? ReviewedFileDiff);
