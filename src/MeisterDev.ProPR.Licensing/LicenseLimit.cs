// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

using System.Globalization;

namespace MeisterDev.ProPR.Licensing;

/// <summary>
///     One quantitative limit in a license, in one of three states: absent, unlimited, or a count.
///     <para>
///         The three states are separate because they lead to different decisions. An absent limit means the
///         license does not constrain that dimension at all; an unlimited limit means it was stated and set
///         to no ceiling; a count of zero means the dimension is constrained to nothing. Collapsing any two
///         of them into one value would change what a license grants.
///     </para>
///     <para>The default value of the type is <see cref="Absent" />.</para>
/// </summary>
public readonly struct LicenseLimit : IEquatable<LicenseLimit>
{
    private readonly Kind _kind;
    private readonly long _count;

    private LicenseLimit(Kind kind, long count)
    {
        this._kind = kind;
        this._count = count;
    }

    /// <summary>The license does not state this limit.</summary>
    public static LicenseLimit Absent => default;

    /// <summary>The license states this limit and sets no ceiling.</summary>
    public static LicenseLimit Unlimited => new(Kind.Unlimited, 0);

    /// <summary>Whether the license leaves this limit unstated.</summary>
    public bool IsAbsent => this._kind == Kind.Absent;

    /// <summary>Whether the license states this limit as unlimited.</summary>
    public bool IsUnlimited => this._kind == Kind.Unlimited;

    /// <summary>Whether the license states this limit as a count.</summary>
    public bool HasCount => this._kind == Kind.Count;

    /// <summary>The stated count.</summary>
    /// <exception cref="InvalidOperationException">The limit is absent or unlimited.</exception>
    public long Count => this.HasCount
        ? this._count
        : throw new InvalidOperationException("The limit carries no count. Check HasCount before reading Count.");

    /// <summary>Builds a limit that states a count.</summary>
    /// <param name="count">The ceiling, which may be zero but not negative.</param>
    /// <returns>The limit.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count" /> is negative.</exception>
    public static LicenseLimit Of(long count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        return new LicenseLimit(Kind.Count, count);
    }

    /// <summary>Compares two limits by state and count.</summary>
    /// <param name="left">The first limit.</param>
    /// <param name="right">The second limit.</param>
    /// <returns>Whether the two are the same limit.</returns>
    public static bool operator ==(LicenseLimit left, LicenseLimit right) => left.Equals(right);

    /// <summary>Compares two limits by state and count.</summary>
    /// <param name="left">The first limit.</param>
    /// <param name="right">The second limit.</param>
    /// <returns>Whether the two are different limits.</returns>
    public static bool operator !=(LicenseLimit left, LicenseLimit right) => !left.Equals(right);

    /// <summary>Reads the stated count without throwing when there is none.</summary>
    /// <param name="count">The stated count, or zero when the limit is absent or unlimited.</param>
    /// <returns>Whether the limit states a count.</returns>
    public bool TryGetCount(out long count)
    {
        count = this.HasCount ? this._count : 0;

        return this.HasCount;
    }

    /// <inheritdoc />
    public bool Equals(LicenseLimit other) => this._kind == other._kind && this._count == other._count;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is LicenseLimit other && this.Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(this._kind, this._count);

    /// <inheritdoc />
    public override string ToString() => this._kind switch
    {
        Kind.Unlimited => "unlimited",
        Kind.Count => this._count.ToString(CultureInfo.InvariantCulture),
        _ => "absent",
    };

    private enum Kind : byte
    {
        Absent = 0,
        Unlimited = 1,
        Count = 2,
    }
}
