// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Clients.Models;

/// <summary>Contains candidate and persisted username coordinates prepared by local policy.</summary>
public sealed record ScmUserNamePatchMetadata(string? CandidateUserName, string? PersistedUserName);
