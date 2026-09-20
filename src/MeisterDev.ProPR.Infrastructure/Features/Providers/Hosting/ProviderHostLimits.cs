// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterDev.Ai.Providers.Declaration;

namespace MeisterDev.ProPR.Infrastructure.Features.Providers.Hosting;

/// <summary>
///     What the host will accept from a provider add-in through the primitives, and how long it will wait.
/// </summary>
/// <remarks>
///     <para>
///         Every primitive is a capability granted to code an operator installed, running with the host's own
///         authority against the host's own database. Nothing here isolates the host from an add-in, and it is
///         not meant to: what it does is keep an add-in that is merely wrong from taking the installation with
///         it. A runaway write loop fills one add-in's share of one table instead of the disk, a lease that is
///         never released lapses, and a caller waiting on a row gives up with a failure a review can retry
///         rather than holding a thread until the process ends.
///     </para>
///     <para>
///         The numbers are chosen to be far above what the flows these exist for need. They are constants rather
///         than settings because an operator asked to tune them would have nothing to tune them against, and a
///         setting is easier to add later than to take away.
///     </para>
/// </remarks>
public static class ProviderHostLimits
{
    /// <summary>The longest key an add-in may address a stored entry by.</summary>
    public const int MaximumEntryKeyLength = 200;

    /// <summary>The longest name an add-in may lease a resource under.</summary>
    public const int MaximumResourceNameLength = 200;

    /// <summary>The longest action identifier an add-in may declare, which the declaration refuses past.</summary>
    public const int MaximumActionIdLength = ProviderDeclaredAction.MaximumIdLength;

    /// <summary>
    ///     The longest message the host keeps from an add-in, applied to an invocation's terminal message and to
    ///     a credential-health cause. Every string an add-in produces is untrusted output.
    /// </summary>
    public const int MaximumMessageLength = 1000;

    /// <summary>
    ///     The longest label, hint, placeholder, choice or default value the host renders from an add-in's
    ///     declaration.
    /// </summary>
    /// <remarks>
    ///     Shorter than a message cap, because these go beside an input rather than into a diagnostic. An add-in
    ///     that declares more than this has written a paragraph where a label belongs, and the operator sees the
    ///     beginning of it rather than a form the rest of it pushed off the screen.
    /// </remarks>
    public const int MaximumFieldTextLength = 200;

    /// <summary>
    ///     The longest name a stored value may be held under, applied to an entry's named values and to a
    ///     credential's fields.
    /// </summary>
    /// <remarks>
    ///     Counted as well as the values themselves, because a name is stored beside the value it names: without
    ///     it the stated per-add-in payload bound holds over the values and not over the payload.
    /// </remarks>
    public const int MaximumValueNameLength = 200;

    /// <summary>The most named values one stored entry may carry.</summary>
    public const int MaximumEntryValueCount = 32;

    /// <summary>The longest single value one stored entry may carry, before the host protects it.</summary>
    public const int MaximumEntryValueLength = 8192;

    /// <summary>The most unconsumed, unexpired entries one add-in may hold at once.</summary>
    /// <remarks>
    ///     The flows this exists for hold one entry per authorization in flight. The cap is far above that and is
    ///     there so an add-in writing in a loop is refused rather than filling the table; expired and consumed
    ///     entries are swept and do not count against it.
    /// </remarks>
    public const int MaximumLiveEntriesPerAddIn = 1000;

    /// <summary>The most resources one add-in may hold leases on at once.</summary>
    public const int MaximumLeasesPerAddIn = 100;

    /// <summary>The most credential fields one add-in may store against a connection.</summary>
    public const int MaximumCredentialFieldCount = 32;

    /// <summary>The longest single credential field the host will store, before it protects it.</summary>
    public const int MaximumCredentialFieldLength = 32768;

    /// <summary>
    ///     How long an entry may be asked to live. An authorization the operator has to complete in a browser is
    ///     minutes; anything longer is a value that outlives what it was written for.
    /// </summary>
    public static TimeSpan MaximumEntryLifetime => TimeSpan.FromHours(1);

    /// <summary>How long a lease may be asked to hold, whether or not the add-in releases it.</summary>
    public static TimeSpan MaximumLeaseLifetime => TimeSpan.FromHours(1);

    /// <summary>The longest an add-in may ask to wait for a lease.</summary>
    public static TimeSpan MaximumLeaseWait => TimeSpan.FromMinutes(1);

    /// <summary>The longest one attempt at a named lease may take, whatever the caller asked to wait for.</summary>
    /// <remarks>
    ///     An attempt is a short transaction behind the family's advisory lock, so one that runs longer is a
    ///     stalled database and not a contended resource. It is a floor as well as a ceiling: a caller that asked
    ///     to wait for nothing still gets one real attempt, and cancelling at zero would report every caller as
    ///     refused without reading the row.
    /// </remarks>
    public static TimeSpan LeaseAttemptTimeout => TimeSpan.FromSeconds(5);

    /// <summary>
    ///     How long a caller waits for another caller's credential row before giving up.
    /// </summary>
    /// <remarks>
    ///     The wait covers a vendor token exchange made by whoever holds the row, so it is stated in tens of
    ///     seconds rather than in milliseconds. A caller that exceeds it is told the wait failed, which the retry
    ///     stage treats as transient.
    /// </remarks>
    public static TimeSpan CredentialLockWait => TimeSpan.FromSeconds(30);

    /// <summary>
    ///     How long a credential session may hold the row before the host ends it.
    /// </summary>
    /// <remarks>
    ///     An add-in that holds the row and never returns would otherwise stop every other caller on that
    ///     connection renewing, for as long as the process runs. Ending the session rolls its transaction back,
    ///     which leaves the credential as it was rather than half written.
    /// </remarks>
    public static TimeSpan CredentialSessionLifetime => TimeSpan.FromMinutes(2);

    /// <summary>The longest window an action invocation may be opened for.</summary>
    public static TimeSpan MaximumInvocationWindow => TimeSpan.FromMinutes(30);

    /// <summary>How long a finished action invocation is kept after it reached its terminal state.</summary>
    /// <remarks>
    ///     The row is the record of what an administrator did to a connection, and it outlives the connection
    ///     itself, so nothing else would ever clear one whose connection is gone. Ninety days covers reading back
    ///     what was done and keeps the table finite. Stated here rather than offered as a setting: it is a bound
    ///     on what the host keeps, like every other number in this type.
    /// </remarks>
    public static TimeSpan InvocationHistoryRetention => TimeSpan.FromDays(90);

    /// <summary>
    ///     How long the call that starts an action waits for the add-in to answer before it returns and leaves
    ///     the invocation open.
    /// </summary>
    /// <remarks>
    ///     Far shorter than the invocation window, because the two bound different things. The window bounds the
    ///     flow, which involves an operator signing in at a vendor and takes minutes. This bounds one HTTP
    ///     request, and an add-in that has not answered within it is either waiting for something that is not
    ///     going to arrive on this call or is not observing its signal. Either way the operator's view learns the
    ///     outcome by reading the invocation, so holding the request longer buys nothing and costs a thread and a
    ///     proxy timeout.
    /// </remarks>
    public static TimeSpan DispatchWait => TimeSpan.FromSeconds(30);
}
