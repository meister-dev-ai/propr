// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Application.Exceptions;

/// <summary>
///     Raised when ProPR cannot reach or authenticate with the extracted ProCursor dependency.
/// </summary>
public sealed class ProCursorDependencyUnavailableException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
}
