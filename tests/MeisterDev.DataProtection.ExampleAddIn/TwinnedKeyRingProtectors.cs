// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.DataProtection.ExampleAddIn;

/// <summary>
///     One of two protectors of this add-in answering to the name <c>twinned</c>.
/// </summary>
/// <remarks>
///     They sit here so the catalog's behaviour on one add-in claiming a name twice is asserted against an
///     assembly that actually does it. Which of the two a host would run is decided by the order reflection
///     reports the types in, so the lookup for that name has to stop with both named.
/// </remarks>
public sealed class FirstTwinnedKeyRingProtector : IKeyRingProtector
{
    /// <inheritdoc />
    public string Name => "twinned";

    /// <inheritdoc />
    public void Apply(IDataProtectionBuilder builder, IConfiguration configuration)
    {
        throw new InvalidOperationException("A protector claiming a name twice was selected.");
    }
}

/// <summary>The second protector of this add-in answering to the name <c>twinned</c>.</summary>
public sealed class SecondTwinnedKeyRingProtector : IKeyRingProtector
{
    /// <inheritdoc />
    public string Name => "twinned";

    /// <inheritdoc />
    public void Apply(IDataProtectionBuilder builder, IConfiguration configuration)
    {
        throw new InvalidOperationException("A protector claiming a name twice was selected.");
    }
}
