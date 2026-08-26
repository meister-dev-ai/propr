// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements license key functionality. License logic may not be moved, changed, disabled or circumvented.

namespace MeisterDev.ProPR.Licensing;

/// <summary>
///     The member names of the wire format, named once so the writer and the reader cannot drift apart.
/// </summary>
internal static class LicenseJsonNames
{
    public const string Algorithm = "alg";
    public const string Type = "typ";
    public const string CertificateChain = "x5c";

    public const string SchemaVersion = "schemaVersion";
    public const string LicenseId = "jti";
    public const string Licensee = "licensee";
    public const string IssuedAt = "iat";
    public const string NotBefore = "nbf";
    public const string ExpiresAt = "exp";
    public const string Capabilities = "capabilities";
    public const string Limits = "limits";

    public const string AuthorsPerMonth = "authorsPerMonth";
    public const string Clients = "clients";
    public const string Runners = "runners";
    public const string ConcurrentReviews = "concurrentReviews";

    public const string Unlimited = "unlimited";
}
