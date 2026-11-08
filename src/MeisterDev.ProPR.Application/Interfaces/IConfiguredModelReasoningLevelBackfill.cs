// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Interfaces;

/// <summary>
///     Fills the reasoning levels of configured models that no source has filled yet, and refreshes the levels that
///     came from the model catalog or an add-in.
/// </summary>
/// <remarks>
///     It runs at startup, after the bundled catalog snapshot is imported, so that models saved before configured models
///     carried their levels get them, and a newer snapshot or add-in reaches the models it describes. Levels that an
///     operator entered or that the provider reported stay as they are. Running it again changes nothing that is
///     already current.
/// </remarks>
public interface IConfiguredModelReasoningLevelBackfill
{
    /// <summary>Fills and refreshes the levels of every chat model. Returns the number of models changed.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<int> BackfillAllAsync(CancellationToken ct);
}
