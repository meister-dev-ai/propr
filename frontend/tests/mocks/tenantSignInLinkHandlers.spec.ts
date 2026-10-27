// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { setupServer } from 'msw/node'
import { afterAll, afterEach, beforeAll, describe, expect, it, vi } from 'vitest'

import { handlers } from '@/mocks/handlers'
import { API_BASE_URL } from '@/services/apiBase'

const server = setupServer(...handlers)
const confirmationPage = 'http://localhost/settings/sign-in-link'

function startLink(returnUrl: string | null): Promise<Response> {
  const query = new URLSearchParams({ mode: 'link' })
  if (returnUrl) {
    query.set('returnUrl', returnUrl)
  }

  return fetch(`${API_BASE_URL}/auth/external/challenge/acme/provider-1?${query.toString()}`, { redirect: 'manual' })
}

// The mock runtime answers the sign-in link endpoints as the API does, so the pages behave the same in both runtimes.
describe('tenant sign-in link mock handlers', () => {
  beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
  afterEach(() => vi.useRealTimers())
  afterAll(() => server.close())

  it('refuses a sign-in that links an account without the page it returns to', async () => {
    const response = await startLink(null)

    expect(response.status).toBe(400)
    expect((await response.json() as { error: string }).error).toBe('return_url_required')
  })

  it('stops offering the waiting sign-in 15 minutes after it started', async () => {
    vi.useFakeTimers({ toFake: ['Date'] })
    vi.setSystemTime(new Date('2026-10-27T10:00:00Z'))
    expect((await startLink(confirmationPage)).status).toBe(302)
    expect((await fetch(`${API_BASE_URL}/auth/external/pending-link`)).status).toBe(200)

    vi.setSystemTime(new Date('2026-10-27T10:15:01Z'))
    const read = await fetch(`${API_BASE_URL}/auth/external/pending-link`)
    const confirmed = await fetch(`${API_BASE_URL}/auth/external/pending-link/confirm`, { method: 'POST' })

    expect(read.status).toBe(404)
    expect(confirmed.status).toBe(404)
  })
})
