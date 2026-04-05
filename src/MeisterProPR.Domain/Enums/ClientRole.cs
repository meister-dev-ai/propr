// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterProPR.Domain.Enums;

/// <summary>Role a user holds within a specific client.</summary>
public enum ClientRole
{
    /// <summary>Can view jobs and protocol history for the assigned client.</summary>
    ClientUser = 0,

    /// <summary>Can manage client configuration (ADO credentials, settings) and view all client data.</summary>
    ClientAdministrator = 1,
}
