/**
 * How a stored memory record is labelled wherever it is listed.
 *
 * A record's value to a later review is its outcome: a rejection tells the reviewer the concern was
 * considered and declined, a claimed fix does not. Both look identical in the summary prose, so the
 * outcome is stated as a badge rather than left for the reader to infer from the text.
 */
export type MemoryOutcomeTone = 'rejected' | 'fixed' | 'dismissed' | 'unknown'

export interface MemoryOutcome {
  /** Short label for the badge. */
  label: string
  /** Longer wording for a title attribute or a detail row. */
  description: string
  tone: MemoryOutcomeTone
}

/**
 * The wire format is a camelCase string, because the API serializes enums with
 * `JsonStringEnumConverter`. Numeric forms are tolerated so a record read from an older payload still
 * renders rather than falling through to "not recorded".
 */
function normalize(value: string | number | null | undefined): string | null {
  if (value === null || value === undefined) {
    return null
  }
  return typeof value === 'number' ? String(value) : value
}

const ADMIN_DISMISSED = new Set(['adminDismissed', '1'])
const ACCEPTED_BY_HUMAN = new Set(['acceptedByHuman', '1'])
const CLAIMS_FIX = new Set(['claimsFix', '2'])
const ACCEPTED_WITHOUT_CHANGE = new Set(['acceptedWithoutChange', '1'])

export function describeMemoryOutcome(
  source: string | number | null | undefined,
  intent: string | number | null | undefined,
  clarity: string | number | null | undefined,
): MemoryOutcome {
  const normalizedSource = normalize(source)
  if (normalizedSource !== null && ADMIN_DISMISSED.has(normalizedSource)) {
    return {
      label: 'Dismissed',
      description: 'An administrator dismissed this pattern.',
      tone: 'dismissed',
    }
  }

  const normalizedIntent = normalize(intent)
  if (normalizedIntent !== null && ACCEPTED_BY_HUMAN.has(normalizedIntent)) {
    const normalizedClarity = normalize(clarity)
    const stated = normalizedClarity !== null && ACCEPTED_WITHOUT_CHANGE.has(normalizedClarity)
    return stated
      ? {
          label: 'Rejected',
          description: 'A reviewer rejected the finding and accepted the code as it stands.',
          tone: 'rejected',
        }
      : {
          label: 'Rejected, unclear',
          description:
            'A reviewer rejected the finding, but the discussion did not say so plainly, so the decision counts as low confidence.',
          tone: 'unknown',
        }
  }

  if (normalizedIntent !== null && CLAIMS_FIX.has(normalizedIntent)) {
    return {
      label: 'Fix claimed',
      description:
        'A reviewer marked the concern fixed and the code changed. This is not a decision to accept the concern.',
      tone: 'fixed',
    }
  }

  return {
    label: 'Not recorded',
    description: 'This record was stored before the resolution outcome was kept.',
    tone: 'unknown',
  }
}
