// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { setupServer } from 'msw/node'
import { afterAll, beforeAll, describe, expect, it } from 'vitest'

import { handlers } from '@/mocks/handlers'
import { API_BASE_URL } from '@/services/apiBase'

const server = setupServer(...handlers)

async function setMockEdition(edition: 'community' | 'commercial'): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/admin/licensing/mock`, {
    method: 'PATCH',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ edition }),
  })

  expect(response.status).toBe(200)
}

async function activate(): Promise<void> {
  const response = await fetch(`${API_BASE_URL}/admin/licensing/license`, {
    method: 'PUT',
    headers: { 'content-type': 'application/json' },
    body: JSON.stringify({ token: 'a-license-document' }),
  })

  expect(response.status).toBe(200)
}

async function readHistory(): Promise<{ action: string }[]> {
  const response = await fetch(`${API_BASE_URL}/admin/licensing/history`)

  return await response.json() as { action: string }[]
}

async function readSummary(): Promise<{ edition: string; stage: string }> {
  const response = await fetch(`${API_BASE_URL}/admin/licensing`)

  return await response.json() as { edition: string; stage: string }
}

// The mock lets an operator switch the installation between editions without going through an activation, so
// the license state and the summary can disagree unless both transitions read the same effective state.
describe('licensing mock handlers', () => {
  beforeAll(() => server.listen({ onUnhandledRequest: 'error' }))
  afterAll(() => server.close())

  it('records an activation, not a replacement, once the mock installation reports no license', async () => {
    await activate()
    await setMockEdition('community')

    expect((await readSummary()).stage).toBe('none')

    await activate()

    expect((await readHistory())[0].action).toBe('activated')
  })

  it('records no removal on an installation the summary reports as unlicensed', async () => {
    await activate()
    await setMockEdition('community')
    const before = await readHistory()

    const response = await fetch(`${API_BASE_URL}/admin/licensing/license`, { method: 'DELETE' })

    expect(response.status).toBe(204)
    expect(await readHistory()).toEqual(before)
  })
})
