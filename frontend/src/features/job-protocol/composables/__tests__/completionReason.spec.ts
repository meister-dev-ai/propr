// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.
// This file implements commercial-only functionality. A commercial license is required to activate or use that functionality.

import { describe, expect, it } from 'vitest'
import { formatTimeLimitMessage } from '../completionReason'

describe('formatTimeLimitMessage', () => {
  it('explains a completed review that ended at the time limit of its tenant', () => {
    const message = formatTimeLimitMessage('completed', 'timeLimitReached')

    expect(message).toContain('maximum review duration of its tenant')
    expect(message).toContain('files it did not review')
  })

  it.each([
    ['completed', 'none'],
    ['completed', null],
    ['failed', 'timeLimitReached'],
    [null, null],
  ])('says nothing for status %s with reason %s', (status, reason) => {
    expect(formatTimeLimitMessage(status, reason)).toBeNull()
  })
})
