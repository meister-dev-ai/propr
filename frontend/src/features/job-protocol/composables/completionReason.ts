// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

/**
 * The explanation for a completed review that ended at the maximum review duration of its tenant, or null when the
 * review did not end there. The review published the findings it had and a summary that lists the files it did not
 * review, so there is nothing to restart.
 */
export function formatTimeLimitMessage(
  status: string | null | undefined,
  completionReason: string | null | undefined,
): string | null {
  if (status !== 'completed' || completionReason !== 'timeLimitReached') {
    return null
  }

  return (
    'This review reached the maximum review duration of its tenant, so it started no further files and finished '
    + 'with a synthesis of what it reviewed. The files it did not review are listed in its summary.'
  )
}
