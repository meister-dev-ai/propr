// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     Where the reasoning levels of a configured model come from.
/// </summary>
/// <remarks>
///     ProPR fills the levels of a model from the provider, the model catalog or the provider family's add-in, in that
///     order, and refreshes a filled list when one of these sources changes. A list that an operator entered stays as
///     the operator left it.
/// </remarks>
public enum AiReasoningLevelSource
{
    /// <summary>The provider reported the levels during model discovery.</summary>
    Provider = 0,

    /// <summary>The model catalog lists the levels.</summary>
    Catalog = 1,

    /// <summary>The add-in of the provider family reports the levels from its own knowledge of the model.</summary>
    AddIn = 2,

    /// <summary>An operator entered the levels.</summary>
    Operator = 3,
}
