// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

// Single source of truth for the pass taxonomy shown in the trace selector,
// breadcrumb, and origin badges. The UI must never surface raw enum identifiers
// (e.g. "ProRVAugmentation").

/** Labels for the PR-level (non-file) passes, derived from the protocol label. */
const PR_LEVEL_LABELS: Record<string, string> = {
    synthesis: 'Synthesis',
    'pr-wide-review': 'PR-wide review',
    finalization: 'Finalization',
    posting: 'Posting',
}

/**
 * Derives the "Pass N" label for a multi-pass union pass from its protocol `reason`.
 * The backend records each additional union pass with a reason of the form
 * `"multi-pass union {arm} pass #{index}"`, where the index is the 1-based pass position
 * (the tier baseline is pass 1, so the first additional pass is `#2`). When the index is
 * absent (older/legacy rows), we fall back to a generic label rather than inventing a number.
 */
function multiPassUnionLabel(reason: string | null | undefined): string {
    const match = /pass #(\d+)/i.exec(reason ?? '')
    return match ? `Pass ${match[1]}` : 'Additional pass'
}

/**
 * Human-readable label for a pass, derived from its `passKind` (preferred) with
 * the protocol `label` as a fallback for PR-level / legacy passes. For multi-pass
 * union passes the `reason` carries the pass index used to render "Pass N".
 */
export function passKindLabel(
    passKind: string | null | undefined,
    label: string | null | undefined,
    reason?: string | null,
): string {
    switch (passKind) {
        case 'Baseline':
            return 'Initial review'
        case 'ProRVAugmentation':
            return 'ProRV verification'
        case 'MultiPassUnion':
            return multiPassUnionLabel(reason)
        case 'Synthesis':
            return 'Synthesis'
        default:
            break
    }

    const normalizedLabel = (label ?? '').trim().toLowerCase()
    if (normalizedLabel && PR_LEVEL_LABELS[normalizedLabel]) {
        return PR_LEVEL_LABELS[normalizedLabel]
    }

    // File passes with no recorded kind are the initial (baseline) review.
    return 'Initial review'
}

/** Title-cased display name for a review-pass lens (e.g. "security" -> "Security"). */
function lensLabel(lens: string): string {
    return lens.charAt(0).toUpperCase() + lens.slice(1)
}

/**
 * Provenance label for a finding's origin pass. Baseline vs ProRVAugmentation vs
 * MultiPassUnion vs Synthesis. When a multi-pass union finding carries its 1-based
 * pass index (the tier baseline is pass 1, so additional passes are 2..k) the badge
 * renders "Pass N"; without an index it falls back to the generic "Additional pass".
 * A specialist lens (e.g. security) is appended as "Pass N · Security". Returns null
 * when the origin is unknown so callers render no badge.
 */
export function originLabel(
    originPassKind: string | null | undefined,
    originPassIndex?: number | null,
    originPassLens?: string | null,
): string | null {
    switch (originPassKind) {
        case 'Baseline':
            return 'Initial review'
        case 'ProRVAugmentation':
            return 'ProRV verification'
        case 'MultiPassUnion': {
            const base = typeof originPassIndex === 'number' ? `Pass ${originPassIndex}` : 'Additional pass'
            return originPassLens ? `${base} · ${lensLabel(originPassLens)}` : base
        }
        case 'Synthesis':
            return 'Synthesis'
        default:
            return null
    }
}
