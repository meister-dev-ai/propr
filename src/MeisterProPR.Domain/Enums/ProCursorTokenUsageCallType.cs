// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Categorizes one ProCursor-owned AI call for reporting and drill-down views.
/// </summary>
public enum ProCursorTokenUsageCallType
{
    /// <summary>Embedding generation for indexed or query-time knowledge retrieval.</summary>
    Embedding = 0,

    /// <summary>Model response generation owned by ProCursor.</summary>
    Response = 1,

    /// <summary>Any other ProCursor-owned AI call that does not fit the standard categories.</summary>
    Other = 2,
}
