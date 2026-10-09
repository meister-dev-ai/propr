// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using System.Globalization;

namespace MeisterDev.ProPR.Application.Features.Clients.Models;

/// <summary>Contains a numeric installation identifier supplied at the provider boundary.</summary>
public readonly record struct ScmInstallationId(long Value)
{
    public static implicit operator ScmInstallationId(long value) => new(value);
    public static implicit operator long(ScmInstallationId value) => value.Value;
    public override string ToString() => this.Value.ToString(CultureInfo.InvariantCulture);
    public string ToString(IFormatProvider? provider) => this.Value.ToString(provider);
}
