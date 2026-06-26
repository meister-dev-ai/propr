// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

using MeisterProPR.Application.ValueObjects;

namespace MeisterProPR.Application.Features.Reviewing.Execution.Services;

/// <summary>
///     The deterministic, escalate-only security floor. A file is in the security set when ANY leg
///     fires — a security-sensitive PATH (the language-independent, reliable leg, which also covers non-code
///     config), a confirmed security MARKER (added-code content, best-effort), or the triage MODEL's
///     escalate signal. Legs only ADD; nothing here can clear a flag, and the ABSENCE of all legs is never a
///     positive "safe" signal (a file with no flag still receives the universal baseline review).
/// </summary>
public static class SecurityFloor
{
    // Path-segment terms that mark a security-sensitive location. Substring match on the lowercased path.
    // Over-inclusion is acceptable: the floor is escalate-only (a false positive costs a little extra
    // review, never safety) and non-authoritative. The concrete list is tunable.
    private static readonly string[] SensitivePathTerms =
    [
        "auth", "oauth", "openid", "saml", "jwt", "login", "logout", "password", "passwd",
        "secret", "credential", "crypto", "encrypt", "decrypt", "signing", "security",
        "permission", "authoriz", "session", "cookie", "cors", "csrf", "/identity", "token",
    ];

    /// <summary>The language-independent path leg of the security floor (also covers non-code config paths).</summary>
    public static bool IsSecuritySensitivePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var normalized = path.Replace('\\', '/').ToLowerInvariant();
        return SensitivePathTerms.Any(term => normalized.Contains(term, StringComparison.Ordinal));
    }

    /// <summary>
    ///     The union of the (available) escalate-only legs. Pass <paramref name="modelSecurityEscalate" />
    ///     <see langword="false" /> where the triage verdict isn't available (the model leg simply doesn't
    ///     contribute there; it never lowers the result).
    /// </summary>
    public static bool IsFlagged(string? path, FileRiskMarkers markers, bool modelSecurityEscalate)
    {
        return modelSecurityEscalate
               || markers.HasSecurityMarkers
               || IsSecuritySensitivePath(path);
    }
}
