// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.DTOs;

/// <summary>
///     A position on a connection profile that holds a stored vocabulary value.
/// </summary>
/// <remarks>
///     One member per axis rather than one per stored column: the same protocol-mode vocabulary is stored on a
///     configured model and on a purpose binding, and an operator reading the report needs to know which
///     vocabulary failed, not which row repeated it.
/// </remarks>
public enum AiConnectionVocabularyField
{
    /// <summary>The authentication mode stored on the profile.</summary>
    AuthMode = 0,

    /// <summary>The discovery mode stored on the profile.</summary>
    DiscoveryMode = 1,

    /// <summary>An operation kind stored on a configured model.</summary>
    OperationKind = 2,

    /// <summary>A protocol mode stored on a configured model or a purpose binding.</summary>
    ProtocolMode = 3,

    /// <summary>The source stored on a configured model.</summary>
    ConfiguredModelSource = 4,

    /// <summary>The purpose stored on a purpose binding.</summary>
    Purpose = 5,

    /// <summary>The status stored on the verification snapshot.</summary>
    VerificationStatus = 6,

    /// <summary>The failure category stored on the verification snapshot.</summary>
    VerificationFailureCategory = 7,
}

/// <summary>One stored value on a connection profile that names nothing this build recognises.</summary>
/// <param name="Field">The position the value was read from.</param>
/// <param name="Value">The value as it is stored.</param>
/// <remarks>
///     The stored value is reported as it is, because an operator fixing the row has to match it against what is
///     in the column, and the remedy differs by what the value actually says.
/// </remarks>
public sealed record AiUnresolvedValueDto(AiConnectionVocabularyField Field, string Value);
