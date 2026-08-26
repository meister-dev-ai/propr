// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Services;

/// <summary>
///     Log messages for the licensing clock.
///     <para>
///         A host clock behind the recorded instant is a warning because the recorded instant still determines
///         the result. One far ahead of it is a warning because the recorded instant is held to the time this
///         process has observed passing, so the reading is not written as it stands. A required observed-time
///         write that fails is an error because no fresh license decision proceeds.
///     </para>
/// </summary>
public sealed partial class RatchetedLicensingClock
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "The host clock reads {HostInstant}, which is behind the {RecordedInstant} this installation has already recorded. License terms are judged against the recorded instant. Correct the host clock or its time synchronization.")]
    private static partial void LogHostClockBehindRecordedInstant(
        ILogger logger,
        DateTimeOffset hostInstant,
        DateTimeOffset recordedInstant);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "The host clock reads {HostInstant}, which is further ahead than the recorded timeline allows past the {RecordedInstant} this installation has recorded. {BoundedInstant} was recorded instead. A recorded instant only rises, so accepting the reading would hold every replica at it and read a license in force as expired. Correct the host clock or its time synchronization.")]
    private static partial void LogHostClockFarAheadOfRecordedInstant(
        ILogger logger,
        DateTimeOffset hostInstant,
        DateTimeOffset recordedInstant,
        DateTimeOffset boundedInstant);

    [LoggerMessage(
        Level = LogLevel.Error,
        Message =
            "The instant {HostInstant} could not be recorded as observed. No fresh license decision was made because the observed-time ratchet could not advance.")]
    private static partial void LogObservedInstantNotRecorded(
        ILogger logger,
        DateTimeOffset hostInstant,
        Exception exception);
}
