// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     A host name an installation has been observed running on, with the window it has been seen over.
/// </summary>
/// <param name="Hostname">The host name the replica reported.</param>
/// <param name="FirstSeenAt">When this host name was first observed.</param>
/// <param name="LastSeenAt">When it was last observed.</param>
public sealed record ReplicaHostname(string Hostname, DateTimeOffset FirstSeenAt, DateTimeOffset LastSeenAt);
