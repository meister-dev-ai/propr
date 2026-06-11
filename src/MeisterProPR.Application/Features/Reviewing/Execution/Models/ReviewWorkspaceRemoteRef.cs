// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Domain.Enums;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Models;

/// <summary>
///     Provider-resolved git remote information used to hydrate a local review workspace.
/// </summary>
public sealed record ReviewWorkspaceRemoteRef(
    ScmProvider Provider,
    string RemoteUrl,
    IReadOnlyList<string> FetchRefSpecs,
    string RepositoryKey,
    string CredentialScopeKey,
    bool SupportsLocalFetch,
    string? ProjectOrNamespace = null,
    string? AuthorizationHeader = null);
