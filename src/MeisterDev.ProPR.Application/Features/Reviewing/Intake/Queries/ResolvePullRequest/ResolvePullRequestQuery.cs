// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Reviewing.Intake.Queries.ResolvePullRequest;

/// <summary>
///     Asks which client covers a pull request, and under which canonical coordinates, given only what a
///     pull request's web address reveals: a host, an owner or organization segment, a repository name, and
///     a number.
/// </summary>
/// <param name="AccessibleClientIds">
///     Clients the caller may see. <see langword="null" /> means every client, which is how a platform
///     administrator resolves. An empty collection resolves nothing.
/// </param>
/// <param name="HostBaseUrl">
///     The host as it appears in the address, for example <c>https://dev.azure.com</c> or
///     <c>http://localhost:8091</c>. Only the scheme and authority are significant.
/// </param>
/// <param name="ScopePath">
///     The owner, namespace, or organization segment from the address, for example <c>local_admin</c> or
///     <c>meister-dev</c>. Matched against both the configured scope path's trailing segment and the
///     configured project key, because providers split that identity differently.
/// </param>
/// <param name="RepositoryName">The repository name as it appears in the address.</param>
/// <param name="PullRequestNumber">The pull request number as it appears in the address.</param>
public sealed record ResolvePullRequestQuery(
    IReadOnlyCollection<Guid>? AccessibleClientIds,
    string HostBaseUrl,
    string ScopePath,
    string RepositoryName,
    int PullRequestNumber);
