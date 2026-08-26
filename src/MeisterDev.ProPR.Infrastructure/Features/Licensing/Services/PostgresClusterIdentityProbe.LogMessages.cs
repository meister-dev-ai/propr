// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using Microsoft.Extensions.Logging;

namespace MeisterDev.ProPR.Infrastructure.Features.Licensing.Services;

/// <summary>
///     Log messages for the cluster identity probe.
///     <para>
///         Both are at debug level. The cluster system identifier is an optional component, so neither
///         condition needs an operator: the profile records the component as absent and goes on. They are
///         logged so that a profile carrying no identifier can be explained.
///     </para>
/// </summary>
public sealed partial class PostgresClusterIdentityProbe
{
    [LoggerMessage(
        Level = LogLevel.Debug,
        Message =
            "The cluster system identifier was not read (SQL state {SqlState}: {FailureDetail}). The profile records it as absent.")]
    private static partial void LogClusterIdentifierNotRead(ILogger logger, string sqlState, string failureDetail);

    [LoggerMessage(
        Level = LogLevel.Debug,
        Message =
            "The cluster system identifier came back as {ValueType} rather than a 64-bit integer. The profile records it as absent.")]
    private static partial void LogClusterIdentifierUnexpectedShape(ILogger logger, string valueType);
}
