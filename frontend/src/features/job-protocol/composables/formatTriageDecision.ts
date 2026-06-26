// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import type { TriageDecisionEventDetails, TriageDecisionPresentation } from '@/features/job-protocol/types'

/**
 * Formats a parsed `triage_decision` event into a display-ready rationale.
 * Absence is explicit: an Unavailable blast-radius reads "no data", never "0 callers" — a measured
 * zero is distinct from no measurement.
 */
export function formatTriageDecision(details: TriageDecisionEventDetails): TriageDecisionPresentation {
    return {
        tier: (details.tier ?? '').trim() || 'Unknown',
        why: (details.why ?? '').trim() || '—',
        security: details.securityFlagged ? 'Security-flagged' : 'Not flagged',
        blastRadius: formatBlastRadius(details.fanOutKind, details.fanOutCount),
    }
}

function formatBlastRadius(kind: string | null | undefined, count: number | null | undefined): string {
    switch (kind) {
        case 'Measured': {
            const n = count ?? 0
            return `${n} caller${n === 1 ? '' : 's'}`
        }
        case 'Truncated':
            return 'many callers (truncated)'
        default:
            // Unavailable / unknown — absence is explicit and never reported as zero callers.
            return 'no data'
    }
}
