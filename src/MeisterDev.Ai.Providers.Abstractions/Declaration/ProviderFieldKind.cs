// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.Declaration;

/// <summary>
///     The value shapes a declared field can take. Closed on purpose: a host renders and validates what a family
///     declares, and an open vocabulary would make it a form engine before the first family needs one.
/// </summary>
public enum ProviderFieldKind
{
    /// <summary>Free text. Never used as an address, whatever it contains.</summary>
    String = 0,

    /// <summary>Credential material. Stored in the credential envelope, masked where it is entered, never logged.</summary>
    Secret = 1,

    /// <summary>An address. Checked against the host's egress rules before the family's own validator runs.</summary>
    Url = 2,

    /// <summary>A yes or no.</summary>
    Bool = 3,

    /// <summary>A whole number.</summary>
    Int = 4,

    /// <summary>One of a declared set of options.</summary>
    Choice = 5,

    /// <summary>An ordered list of free-text values.</summary>
    StringList = 6,
}
