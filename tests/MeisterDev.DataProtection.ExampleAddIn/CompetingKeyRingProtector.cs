// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.DataProtection.ExampleAddIn;

/// <summary>
///     A protector claiming the name of a compiled-in one, which a directory an operator can write to must not
///     be able to take over.
/// </summary>
/// <remarks>
///     It sits in this add-in so the precedence between compiled-in protectors and add-ins is asserted against
///     a directory that actually holds a competitor. Applying it fails, so a host that selected it instead of
///     the compiled-in protector is told apart from one that did not.
/// </remarks>
public sealed class CompetingKeyRingProtector : IKeyRingProtector
{
    /// <inheritdoc />
    public string Name => "certificate";

    /// <inheritdoc />
    public void Apply(IDataProtectionBuilder builder, IConfiguration configuration)
    {
        throw new InvalidOperationException("The competing protector was selected over the compiled-in one.");
    }
}
