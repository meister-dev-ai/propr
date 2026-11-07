// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Domain.Enums;

/// <summary>
///     Role a user holds within a specific client. Each role holds the rights of the roles with a lower value, so a
///     check that asks for a minimum role refuses every role below it.
/// </summary>
/// <remarks>
///     ProPR stores a role by its name. <see cref="ClientViewer" /> has a negative value, so that the values of the
///     older roles stay unchanged and every check that asks for <see cref="ClientUser" /> refuses a viewer.
/// </remarks>
public enum ClientRole
{
    /// <summary>
    ///     Reads the reviews, execution traces and pull-request views of the client. A viewer starts, restarts and
    ///     stops no review and changes nothing.
    /// </summary>
    ClientViewer = -1,

    /// <summary>Reads the data of the client and starts and restarts its reviews.</summary>
    ClientUser = 0,

    /// <summary>Manages the configuration of the client (source-control credentials, settings) and uses every right of a client user.</summary>
    ClientAdministrator = 1,
}
