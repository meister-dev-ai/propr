// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, expect, it } from 'vitest'
import { clientDeletionStep } from '../useClientDetailViewModel'

function answer(status: number, error?: unknown, retryAfter?: string) {
  return { error, response: { status, headers: new Headers(retryAfter ? { 'Retry-After': retryAfter } : {}) } }
}

// While ProPR stops the work of a client, the deletion answers 409 with the seconds to wait, and the page requests
// the deletion again after that time.
describe('clientDeletionStep', () => {
  it('treats a deleted client and a client that no longer exists as deleted', () => {
    expect(clientDeletionStep(answer(204))).toEqual({ kind: 'deleted' })
    expect(clientDeletionStep(answer(404))).toEqual({ kind: 'deleted' })
  })

  it('waits the seconds that the answer names', () => {
    expect(clientDeletionStep(answer(409, { error: 'client_work_running', retryAfterSeconds: 42 }, '42')))
      .toEqual({ kind: 'waiting', seconds: 42 })
  })

  it('reads the Retry-After header when the body names no wait, and waits a minute without either', () => {
    expect(clientDeletionStep(answer(409, undefined, '17'))).toEqual({ kind: 'waiting', seconds: 17 })
    expect(clientDeletionStep(answer(409))).toEqual({ kind: 'waiting', seconds: 60 })
  })

  it('ignores a wait in the body that is not a positive number of seconds', () => {
    expect(clientDeletionStep(answer(409, { retryAfterSeconds: Number.NaN }, '17'))).toEqual({ kind: 'waiting', seconds: 17 })
    expect(clientDeletionStep(answer(409, { retryAfterSeconds: 0 }))).toEqual({ kind: 'waiting', seconds: 60 })
    expect(clientDeletionStep(answer(409, { retryAfterSeconds: '30' }))).toEqual({ kind: 'waiting', seconds: 60 })
  })

  it('reports any other answer as a failure', () => {
    expect(clientDeletionStep(answer(500)).kind).toBe('failed')
    expect(clientDeletionStep({}).kind).toBe('failed')
  })
})
