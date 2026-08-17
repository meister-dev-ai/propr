// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.ValueObjects;

/// <summary>One repository a mention configuration answers on.</summary>
/// <param name="RepositoryId">
///     The provider-native identifier the configuration stored, and the key every discovered pull request is
///     reported under so the scan can match it back to the claim.
/// </param>
/// <param name="DisplayName">
///     The name the repository was stored under, where guided selection recorded one. Adapters whose API is
///     addressed by path use it to avoid a lookup, and fall back to the identifier when it is absent.
/// </param>
public sealed record ClaimedRepositoryRef(string RepositoryId, string? DisplayName = null);
