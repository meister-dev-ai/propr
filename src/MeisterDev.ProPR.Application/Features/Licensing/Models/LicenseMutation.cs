// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     The outcome of a mutation of the installation's stored license: what it replaced, and when the store
///     carried the change out.
/// </summary>
/// <remarks>
///     The instant comes from the store rather than from the caller's host clock, because it is what orders
///     the history. Replicas mutate the same singleton row under one lock, so the order the store observed is
///     the order the mutations happened in; a caller stamping its own clock afterwards would record an order
///     that the skew between two replicas, and the delay between a mutation and the record of it, could both
///     invert.
/// </remarks>
/// <param name="Previous">The document the mutation displaced, or null when the installation held none.</param>
/// <param name="MutatedAt">The instant the store carried the mutation out.</param>
public sealed record LicenseMutation(StoredLicense? Previous, DateTimeOffset MutatedAt);
