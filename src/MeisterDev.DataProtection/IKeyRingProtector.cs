// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;

namespace MeisterDev.DataProtection;

/// <summary>
///     Encrypts the data-protection key ring, so the key files alone do not read the secrets they protect.
/// </summary>
/// <remarks>
///     An installation selects one protector by name. A protector is either compiled into this assembly or
///     supplied by an add-in, and both are found the same way, so a deployment can add a key service without a
///     product release. The contract is a name and one call against the builder, because the mechanisms the
///     protectors use are the ones ASP.NET Core Data Protection already exposes.
/// </remarks>
public interface IKeyRingProtector
{
    /// <summary>
    ///     The name an operator selects this protector by, matched with
    ///     <see cref="StringComparer.OrdinalIgnoreCase" />.
    /// </summary>
    /// <remarks>
    ///     A protector states a non-empty name: selection compares the configured name, which is non-empty,
    ///     against this one, so a protector naming nothing can never be selected. A name a compiled-in
    ///     protector already states cannot be taken over by an add-in, because the compiled-in protectors
    ///     answer before the add-in directory is read. Two add-ins stating one name stop start-up with both
    ///     files named, because which of them a host would run is otherwise decided by where the files sit.
    /// </remarks>
    string Name { get; }

    /// <summary>Applies this protector to the key ring being composed.</summary>
    /// <param name="builder">The data-protection builder the host is composing.</param>
    /// <param name="configuration">The configuration the host was started with.</param>
    /// <remarks>
    ///     A protector reads its own settings from <paramref name="configuration" />. A setting it needs and
    ///     does not find is reported by throwing <see cref="InvalidOperationException" /> with the variable
    ///     named, which fails the start-up that composed it.
    /// </remarks>
    void Apply(IDataProtectionBuilder builder, IConfiguration configuration);
}
