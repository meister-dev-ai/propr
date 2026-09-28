// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.DataProtection.ExampleAddIn;

/// <summary>
///     A protector a directory can hold and a host cannot use. It fails where an add-in missing a dependency,
///     or reading configuration it does not have in its constructor, fails: while the scan is creating it.
/// </summary>
/// <remarks>
///     It sits beside the usable protector so a scan of this add-in meets both, which is what the catalog's
///     behaviour of passing over one unusable type is asserted against.
/// </remarks>
public sealed class UnusableKeyRingProtector : IKeyRingProtector
{
    /// <summary>Initializes a new instance of <see cref="UnusableKeyRingProtector" />, and fails.</summary>
    /// <exception cref="InvalidOperationException">Always.</exception>
    public UnusableKeyRingProtector()
    {
        throw new InvalidOperationException("This protector cannot be created.");
    }

    /// <inheritdoc />
    public string Name => "unusable";

    /// <inheritdoc />
    public void Apply(IDataProtectionBuilder builder, IConfiguration configuration)
    {
        throw new InvalidOperationException("This protector cannot be applied.");
    }
}
