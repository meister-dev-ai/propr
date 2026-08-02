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
        blastRadius: formatBlastRadius(details.fanOutKind, details.fanOutCount ?? 0),
    }
}

function formatBlastRadius(kind: string | null | undefined, count: number = 0): string {
    switch (kind) {
        case 'Measured': {
            return `${count} caller${count === 1 ? '' : 's'}`
        }
        case 'Truncated':
            return 'many callers (truncated)'
        default:
            // Unavailable / unknown — absence is explicit and never reported as zero callers.
            return 'no data'
    }
}
