// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.ProPR.Application.Features.Licensing.Models;

/// <summary>
///     What the installation's stored license amounts to. The cases are separate because the operator
///     response differs: an installation with no license activates one, an unreadable row points at the
///     data-protection key material, and a document that does not verify points at the file itself.
/// </summary>
public enum LicenseStateKind
{
    /// <summary>No license has been activated.</summary>
    None = 0,

    /// <summary>A license is on file but its stored value could not be read back out of its protected form.</summary>
    Unreadable = 1,

    /// <summary>A license is on file and readable, but it did not verify.</summary>
    Invalid = 2,

    /// <summary>A license is on file and comes from a signer this build accepts.</summary>
    Verified = 3,
}
