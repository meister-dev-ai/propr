// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Drivers;

namespace MeisterDev.Ai.Providers.Hosting;

/// <summary>
///     A host primitive refused what a family asked it to do, for a reason the family can report to the operator
///     and cannot fix by asking again.
/// </summary>
/// <remarks>
///     Raised while the values are still in the family's hands, so the message can name the one that was wrong.
///     The same defect detected later gives an operator a connection that behaves oddly and no cause.
/// </remarks>
public sealed class ProviderRequestRejectedException : ArgumentException
{
    /// <summary>Creates the refusal.</summary>
    /// <param name="message">What was wrong, naming the field.</param>
    /// <param name="paramName">The field or argument the refusal is about.</param>
    public ProviderRequestRejectedException(string message, string? paramName = null)
        : base(message, paramName)
    {
    }

    /// <summary>Creates the refusal.</summary>
    public ProviderRequestRejectedException()
    {
    }

    /// <summary>Creates the refusal.</summary>
    /// <param name="message">What was wrong, naming the field.</param>
    /// <param name="innerException">The failure underneath.</param>
    public ProviderRequestRejectedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     The installation is not licensed for the family that asked, so the host will not hand it a credential or
///     dispatch its work.
/// </summary>
/// <remarks>
///     Separate from a refusal about the value, because repeating the call cannot fix it and the remedy is an
///     entitlement rather than a correction. The default runtime classification treats it as permanent, so a
///     review reports it instead of retrying against a licence that is not going to change mid-call.
/// </remarks>
public sealed class ProviderCapabilityUnavailableException : InvalidOperationException
{
    /// <summary>Creates the refusal.</summary>
    /// <param name="message">What the installation is missing, naming the capability.</param>
    public ProviderCapabilityUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the refusal.</summary>
    public ProviderCapabilityUnavailableException()
    {
    }

    /// <summary>Creates the refusal.</summary>
    /// <param name="message">What the installation is missing, naming the capability.</param>
    /// <param name="innerException">The failure underneath.</param>
    public ProviderCapabilityUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     The administrator who started the action no longer holds the role the connection's owner requires, so the
///     host will not store what the action produced.
/// </summary>
/// <remarks>
///     An invocation outlives the call that opened it: an operator signing in at a vendor takes minutes, and a
///     role can be withdrawn in between. The check made when the action started is therefore not a check at the
///     moment a credential is written, and this is what the second check raises. A family cannot make it itself,
///     because it holds no administrator identity.
/// </remarks>
public sealed class ProviderAuthorizationWithdrawnException : InvalidOperationException
{
    /// <summary>Creates the refusal.</summary>
    /// <param name="message">What the administrator no longer holds.</param>
    public ProviderAuthorizationWithdrawnException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the refusal.</summary>
    public ProviderAuthorizationWithdrawnException()
    {
    }

    /// <summary>Creates the refusal.</summary>
    /// <param name="message">What the administrator no longer holds.</param>
    /// <param name="innerException">The failure underneath.</param>
    public ProviderAuthorizationWithdrawnException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
///     A host primitive waited as long as it waits and the resource it wanted was still held by someone else.
/// </summary>
/// <remarks>
///     Derived from <see cref="TimeoutException" /> so that
///     <see cref="DriverFailureMapper.ClassifyRuntimeFailure" /> already classifies it as transient: a credential
///     renewal held up behind another caller's vendor exchange is worth repeating, and a review that reported it
///     as a failure would abandon work over a wait.
/// </remarks>
public sealed class ProviderHostTimeoutException : TimeoutException
{
    /// <summary>Creates the failure.</summary>
    /// <param name="message">What was waited for and for how long.</param>
    public ProviderHostTimeoutException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the failure.</summary>
    public ProviderHostTimeoutException()
    {
    }

    /// <summary>Creates the failure.</summary>
    /// <param name="message">What was waited for and for how long.</param>
    /// <param name="innerException">The failure underneath.</param>
    public ProviderHostTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
