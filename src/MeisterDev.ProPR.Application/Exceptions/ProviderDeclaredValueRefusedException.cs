// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Exceptions;

/// <summary>
///     Thrown when a connection would store a value the installation refuses in a field its provider family
///     declared.
/// </summary>
/// <remarks>
///     Raised where the value is stored, which is the last point before it becomes configuration. The same rules
///     are applied where the request arrives, so an operator normally reads the refusal on the form; reaching this
///     means a caller took another route to the store, and the value is refused there too rather than trusted
///     because something upstream should have checked it.
/// </remarks>
public sealed class ProviderDeclaredValueRefusedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ProviderDeclaredValueRefusedException" /> class.</summary>
    /// <param name="fieldName">The declared field the refused value was entered into.</param>
    /// <param name="reason">Why it was refused.</param>
    public ProviderDeclaredValueRefusedException(string fieldName, string reason)
        : base($"The connection profile cannot be saved: {reason}")
    {
        this.FieldName = fieldName;
        this.Reason = reason;
    }

    /// <summary>The declared field the refused value was entered into.</summary>
    public string FieldName { get; }

    /// <summary>Why it was refused, worded for the operator who has to change the value.</summary>
    public string Reason { get; }
}
