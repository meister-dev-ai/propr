// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using MeisterDev.ProPR.Licensing;
using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Services;

/// <summary>
///     Log messages for the license state provider.
///     <para>
///         Both are warnings because the installation has a license on file that it cannot act on, which needs
///         an operator to look at it. Neither carries the stored document: the refusal detail is bounded and
///         repeats no text from the license.
///     </para>
/// </summary>
public sealed partial class CachedLicenseStateProvider
{
    [LoggerMessage(
        Level = LogLevel.Warning,
        Message =
            "The stored license could not be read back. Re-activate the license file, or restore the data protection keys this installation was activated with.")]
    private static partial void LogStoredLicenseUnreadable(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "The stored license did not verify ({FailureReason}) and the installation is running as Community. {FailureDetail}")]
    private static partial void LogStoredLicenseNotVerified(
        ILogger logger,
        LicenseFailureReason failureReason,
        string? failureDetail);
}
