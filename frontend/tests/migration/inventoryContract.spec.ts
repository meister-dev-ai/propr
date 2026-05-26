// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { readFileSync } from 'node:fs'
import path from 'node:path'
import { describe, expect, it } from 'vitest'
import { routeParityIds } from '@/app/routeParity'

function extractRouteIdsFromParityContract(markdown: string): string[] {
  const lines = markdown.split(/\r?\n/)
  const routeIds: string[] = []
  let inRouteTable = false

  for (const line of lines) {
    if (line.trim() === '## Route Parity Inventory') {
      inRouteTable = true
      continue
    }

    if (!inRouteTable) {
      continue
    }

    if (line.startsWith('## ') && line.trim() !== '## Route Parity Inventory') {
      break
    }

    if (!line.startsWith('|')) {
      continue
    }

    const cells = line.split('|').map((cell) => cell.trim())
    if (cells.length < 5) {
      continue
    }

    const routeName = cells[2]
    if (routeName === 'Route Name' || routeName === '---' || routeName.length === 0) {
      continue
    }

    routeIds.push(routeName.replace(/`/g, ''))
  }

  return routeIds
}

describe('route parity contract', () => {
  it('keeps route parity inventory ids in sync with the contract table', () => {
    const contractPath = path.resolve(
      process.cwd(),
      '../specs/062-vuetify-frontend-rebuild/contracts/frontend-ui-parity.md',
    )
    const contractMarkdown = readFileSync(contractPath, 'utf8')
    const parityContractIds = extractRouteIdsFromParityContract(contractMarkdown)

    expect(routeParityIds).toEqual(parityContractIds)
  })
})
