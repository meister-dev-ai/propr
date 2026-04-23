// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>
///     Supported AI operation categories.
/// </summary>
public enum AiOperationKind
{
    /// <summary>Chat or response generation.</summary>
    Chat = 0,

    /// <summary>Embedding generation.</summary>
    Embedding = 1,
}
