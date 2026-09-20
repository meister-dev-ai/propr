// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

namespace MeisterDev.Ai.Providers.AddIns;

/// <summary>Which of the two add-in directories an assembly was found in.</summary>
/// <remarks>
///     The two are kept apart because they have different provenance and a fixed precedence. A volume mounted
///     over a populated directory hides that directory's contents, so one directory for both would let a single
///     mount remove every add-in the image ships.
/// </remarks>
public enum ProviderAddInOrigin
{
    /// <summary>Inside the deployed image, at a path the image fixes. Loads first and wins a duplicate.</summary>
    BuiltIn = 0,

    /// <summary>The directory an operator configured. Loads second.</summary>
    External = 1,
}
