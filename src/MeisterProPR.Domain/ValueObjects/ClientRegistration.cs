// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.ValueObjects;

/// <summary>
///     Represents a registered client that can submit review jobs.
/// </summary>
/// <param name="Key">Unique key identifying the client.</param>
public sealed record ClientRegistration(string Key);
