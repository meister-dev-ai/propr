// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.ProPR.Domain.Enums;

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>
///     What one mention configuration asks its provider for on a scan tick.
/// </summary>
/// <remarks>
///     The provider travels with the question rather than being inferred from the scope path, because the
///     configuration already names it and two providers can be reached at the same host.
///     The question is asked about the repositories the configuration claims rather than about a project.
///     A project is an Azure DevOps shape that GitHub and Forgejo have no equivalent for, listing one enumerates
///     repositories nobody claimed, and any cap the provider puts on that listing falls on the whole project
///     rather than on the claim.
/// </remarks>
/// <param name="Provider">The provider family the configuration answers on.</param>
/// <param name="ScopePath">The host or organization the configuration lives under.</param>
/// <param name="Repositories">The repositories the configuration claims. Nothing is asked for an empty list.</param>
/// <param name="UpdatedAfter">The watermark. Pull requests untouched since it are of no interest.</param>
/// <param name="ClientId">The client whose credentials the call is made with.</param>
public sealed record ActivePullRequestQuery(
    ScmProvider Provider,
    string ScopePath,
    IReadOnlyList<ClaimedRepositoryRef> Repositories,
    DateTimeOffset UpdatedAfter,
    Guid ClientId);
