// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Domain.ValueObjects;

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     One author observed on finished work, with the signals the source row carries about them.
/// </summary>
/// <remarks>
///     The signals travel beside the identity because the exclusion decision is made where the author is
///     recorded. A row the completion path has in hand carries them; a later read of the rollup does not, and
///     the rollup keeps a flag rather than the names it was decided from.
/// </remarks>
/// <param name="Host">The host that issued the identifier.</param>
/// <param name="ExternalUserId">The author's identifier as that host issues it.</param>
/// <param name="Source">Which kind of finished work made the observation.</param>
/// <param name="Login">The author's provider login, where the source row carries one.</param>
/// <param name="DisplayName">The author's display name, where the source row carries one.</param>
/// <param name="ProviderStatesBot">
///     Whether the provider stated the account is a bot. Only <see langword="true" /> is a statement:
///     <see langword="false" /> and <see langword="null" /> both mean nothing usable was stated, because the
///     mention side's column is not nullable and cannot express absence.
/// </param>
public sealed record AuthorActivityObservation(
    ProviderHostRef Host,
    string ExternalUserId,
    AuthorActivitySource Source,
    string? Login = null,
    string? DisplayName = null,
    bool? ProviderStatesBot = null);
