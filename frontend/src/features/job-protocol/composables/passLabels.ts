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
 * Human-readable label for a pass, derived from its `passKind` (preferred) with
 * the protocol `label` as a fallback for PR-level / legacy passes.
 */
export function passKindLabel(
    passKind: string | null | undefined,
    label: string | null | undefined,
): string {
    switch (passKind) {
        case 'Baseline':
            return 'Initial review'
        case 'ProRVAugmentation':
            return 'ProRV verification'
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

/**
 * Coarse provenance label for an aggregate finding's origin pass. Finding
 * provenance stays Baseline vs ProRVAugmentation, so the badge taxonomy is
 * intentionally coarser than the per-pass tab labels. Returns null when the
 * origin is unknown so callers render no badge.
 */
export function originLabel(originPassKind: string | null | undefined): string | null {
    switch (originPassKind) {
        case 'Baseline':
            return 'Initial review'
        case 'ProRVAugmentation':
            return 'ProRV verification'
        case 'Synthesis':
            return 'Synthesis'
        default:
            return null
    }
}
