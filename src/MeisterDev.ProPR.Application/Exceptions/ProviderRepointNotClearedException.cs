// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

namespace MeisterDev.ProPR.Application.Exceptions;

/// <summary>
///     Thrown when moving a connection to a different provider family would leave a value the departing family
///     qualified, so the move is refused and nothing is written.
/// </summary>
/// <remarks>
///     A qualified value carries the identity key of the family that declared it. One left behind names a family
///     that no longer owns the connection, and the row comes back reported as unavailable against a family that
///     is present — so the operator repoints a connection to fix a problem and gets the same problem back. Refused
///     rather than cleared on a best effort, because a repoint that half completes leaves the operator with a
///     connection in neither state.
/// </remarks>
public sealed class ProviderRepointNotClearedException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="ProviderRepointNotClearedException" /> class.</summary>
    /// <param name="location">Where the value sits, in the operator's terms.</param>
    /// <param name="departingKey">The identity key of the family being left.</param>
    /// <param name="value">The value that blocked the move.</param>
    public ProviderRepointNotClearedException(string location, string departingKey, string value)
        : base(
            $"The connection cannot be moved to another provider family: {location} still holds '{value}', "
            + $"which belongs to '{departingKey}'. Clear it and try again.")
    {
        this.Location = location;
        this.DepartingKey = departingKey;
        this.Value = value;
    }

    /// <summary>Where the value sits.</summary>
    public string Location { get; }

    /// <summary>The identity key of the family being left.</summary>
    public string DepartingKey { get; }

    /// <summary>The value that blocked the move.</summary>
    public string Value { get; }
}
