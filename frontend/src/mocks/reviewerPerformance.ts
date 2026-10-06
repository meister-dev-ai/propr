// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { http, HttpResponse, delay } from 'msw'
import type {
  PerformanceQuery,
  PerformanceScope,
  PerformanceResponse,
  PerformanceScenario,
  PerformancePoint,
  PerformanceFacets,
  PerformanceMatrixCell,
  PerformanceSavedReport,
} from '@/services/reviewerPerformanceService'
import type { components } from '@/types'
type Counts = components['schemas']['ReviewerPerformanceCounts']
type Outcomes = components['schemas']['ReviewerPerformanceOutcomeCounts']
type Range = components['schemas']['ReviewerPerformanceRange']
const version = 'reviewer-performance-v1'
const clients = [
  { id: '10000000-0000-0000-0000-000000000001', label: 'Payments' },
  { id: '10000000-0000-0000-0000-000000000002', label: 'Platform' },
]
const scopes = [
  { client: clients[0]!.id, repo: 'payments-api', label: 'Payments API' },
  { client: clients[0]!.id, repo: 'payments-ui', label: 'Payments UI' },
  { client: clients[1]!.id, repo: 'platform-service', label: 'Platform Service' },
]
const models = ['review-large', 'review-small', 'review-next'].map((id) => ({
  id: btoa(JSON.stringify([id, 'review'])),
  label: id,
}))
const types = [
  { id: 'logic-error', label: 'Logic error' },
  { id: 'concurrency', label: 'Concurrency' },
]
const qualifiers = ['Missing', 'Incorrect', 'Extraneous'].map((id) => ({ id, label: id }))
const provider = 'AzureDevOps:https://dev.azure.com/example'
const repoId = (scope: (typeof scopes)[number]) =>
  btoa(JSON.stringify([scope.client, provider, scope.repo]))
const outcomeKeys = [
  'positive',
  'wrong',
  'dismissed',
  'wontFix',
  'byDesign',
  'unknown',
  'unresolved',
] as const
const emptyOutcomes = (): Outcomes => Object.fromEntries(outcomeKeys.map((key) => [key, 0]))
const emptyCounts = (): Counts => ({
  outcomes: emptyOutcomes(),
  confirmedDuplicates: emptyOutcomes(),
  actedMisses: 0,
  unactedMisses: 0,
  provisionalMisses: 0,
  generated: 0,
  suppressedRepeats: 0,
  withheld: 0,
  publicationUnknown: 0,
  duplicateChecked: 0,
  duplicateSuspected: 0,
  duplicateUnknown: 0,
  unclassified: 0,
  unattributed: 0,
  harvestedThreads: 0,
  failedMissJudgements: 0,
})
function add(a: Counts, b: Counts): void {
  for (const key of outcomeKeys) {
    a.outcomes![key] = (a.outcomes?.[key] ?? 0) + (b.outcomes?.[key] ?? 0)
    a.confirmedDuplicates![key] =
      (a.confirmedDuplicates?.[key] ?? 0) + (b.confirmedDuplicates?.[key] ?? 0)
  }
  for (const key of [
    'actedMisses',
    'unactedMisses',
    'provisionalMisses',
    'generated',
    'suppressedRepeats',
    'withheld',
    'publicationUnknown',
    'duplicateChecked',
    'duplicateSuspected',
    'duplicateUnknown',
    'unclassified',
    'unattributed',
    'harvestedThreads',
    'failedMissJudgements',
  ] as const)
    a[key] = (a[key] ?? 0) + (b[key] ?? 0)
}
function summarize(values: (number | null | undefined)[]): Range | undefined {
  const sorted = values.filter((value): value is number => value != null).sort((a, b) => a - b)
  if (!sorted.length) return undefined
  const quantile = (q: number) => {
    const index = (sorted.length - 1) * q
    const low = Math.floor(index)
    const fraction = index - low
    return sorted[low]! + fraction * ((sorted[low + 1] ?? sorted[low])! - sorted[low]!)
  }
  return {
    minimum: sorted[0],
    firstQuartile: quantile(0.25),
    median: quantile(0.5),
    thirdQuartile: quantile(0.75),
    maximum: sorted.at(-1),
    availableScenarios: sorted.length,
  }
}
function score(
  counts: Counts,
  modelScope: boolean,
): components['schemas']['ReviewerPerformanceScore'] {
  if (modelScope) {
    counts.actedMisses = 0
    counts.unactedMisses = 0
    counts.harvestedThreads = 0
  }
  const scenarios: PerformanceScenario[] = []
  for (const dismissed of ['positive', 'negative', 'excluded'])
    for (const wontFix of ['positive', 'negative', 'excluded'])
      for (const byDesign of ['positive', 'negative', 'excluded'])
        for (const missRule of ['acted', 'allSettled'])
          for (const duplicates of ['retain', 'negative', 'excluded']) {
            let tp = 0
            let fp = 0
            let excluded = (counts.outcomes?.unknown ?? 0) + (counts.outcomes?.unresolved ?? 0)
            const contribution = (rule: string, count: number) => {
              if (rule === 'positive') tp += count
              else if (rule === 'negative') fp += count
              else excluded += count
            }
            const rules = { positive: 'positive', wrong: 'negative', dismissed, wontFix, byDesign }
            for (const key of ['positive', 'wrong', 'dismissed', 'wontFix', 'byDesign'] as const) {
              const duplicate = counts.confirmedDuplicates?.[key] ?? 0
              contribution(rules[key], (counts.outcomes?.[key] ?? 0) - duplicate)
              contribution(duplicates === 'retain' ? rules[key] : duplicates, duplicate)
            }
            const fn =
              (counts.actedMisses ?? 0) +
              (missRule === 'allSettled' ? (counts.unactedMisses ?? 0) : 0)
            scenarios.push({
              id: `${version}/dismissed:${dismissed}/wontFix:${wontFix}/byDesign:${byDesign}/misses:${missRule}/duplicates:${duplicates}`,
              dismissed,
              wontFix,
              byDesign,
              misses: missRule,
              duplicates,
              truePositives: tp,
              falsePositives: fp,
              falseNegatives: modelScope ? null : fn,
              excluded,
              precision: tp + fp ? tp / (tp + fp) : null,
              recall: !modelScope && tp + fn ? tp / (tp + fn) : null,
              f1: !modelScope && 2 * tp + fp + fn ? (2 * tp) / (2 * tp + fp + fn) : null,
            })
          }
  counts.outcomes = {
    ...counts.outcomes,
    total: outcomeKeys.reduce((total, key) => total + (counts.outcomes?.[key] ?? 0), 0),
  }
  counts.confirmedDuplicates = {
    ...counts.confirmedDuplicates,
    total: outcomeKeys.reduce((total, key) => total + (counts.confirmedDuplicates?.[key] ?? 0), 0),
  }
  return {
    counts,
    scenarios,
    summary: {
      precision: summarize(scenarios.map((item) => item.precision)),
      recall: summarize(scenarios.map((item) => item.recall)),
      f1: summarize(scenarios.map((item) => item.f1)),
    },
  }
}
type Cell = {
  date: string
  client: string
  repository: string
  model: string
  type: string
  qualifier: string
  counts: Counts
}
function cells(from: string, to: string): Cell[] {
  const result: Cell[] = []
  const today = Math.floor(Date.now() / 86400000)
  const start = Math.floor(Date.parse(from) / 86400000)
  const end = Math.floor(Date.parse(to) / 86400000)
  for (let day = start; day <= end; day++) {
    if (day < today - 59 || day > today) continue
    for (const [s, scope] of scopes.entries())
      for (const [m, model] of models.entries())
        for (const [t, type] of types.entries())
          for (const [k, qualifier] of qualifiers.entries()) {
            if ((m === 0 && day > today - 6) || (m === 2 && day < today - 13) || day % 7 === 0)
              continue
            const counts = emptyCounts()
            const n = day % 5
            counts.outcomes = {
              positive: 7 + (m === 0 ? 5 : 0) + n,
              wrong: 1 + s + (m === 1 ? 2 : 0),
              dismissed: day > today - 8 ? 4 : 2,
              wontFix: day > today - 8 ? 3 : 1,
              byDesign: k === 2 ? 1 : 0,
              unknown: 1,
              unresolved: day > today - 3 ? 2 : 0,
            }
            counts.confirmedDuplicates = {
              positive: 1,
              wrong: 1,
              dismissed: 1,
              wontFix: 0,
              byDesign: 0,
              unknown: 0,
              unresolved: 0,
            }
            counts.actedMisses = 1
            counts.unactedMisses = t === 1 ? 1 : 0
            counts.provisionalMisses = day > today - 3 ? 1 : 0
            counts.harvestedThreads = 2
            counts.generated =
              outcomeKeys.reduce((sum, key) => sum + (counts.outcomes?.[key] ?? 0), 0) + 2
            counts.suppressedRepeats = 1
            counts.withheld = 1
            counts.duplicateChecked = counts.generated! - 4
            counts.duplicateUnknown = 2
            result.push({
              date: new Date(day * 86400000).toISOString().slice(0, 10),
              client: scope.client,
              repository: repoId(scope),
              model: model.id,
              type: type.id,
              qualifier: qualifier.id,
              counts,
            })
          }
  }
  return result
}
const matches = (cell: Cell, scope: PerformanceScope) =>
  (scope.clientIds == null || scope.clientIds.includes(cell.client)) &&
  (scope.repositories == null || scope.repositories.includes(cell.repository)) &&
  (scope.models == null || scope.models.includes(cell.model)) &&
  (scope.types == null || scope.types.includes(cell.type)) &&
  (scope.qualifiers == null || scope.qualifiers.includes(cell.qualifier))
const key = (cell: Cell, dimension: string) =>
  ({
    client: cell.client,
    repository: cell.repository,
    model: cell.model,
    type: cell.type,
    qualifier: cell.qualifier,
  })[dimension as 'client'] ?? 'all'
const label = (id: string, dimension: string) =>
  ({
    client: clients,
    repository: scopes.map((scope) => ({
      id: repoId(scope),
      label: `${scope.label} · ${clients.find((client) => client.id === scope.client)?.label}`,
    })),
    model: models,
    type: types,
    qualifier: qualifiers,
  })[dimension as 'client']?.find((item) => item.id === id)?.label ?? 'All selected evidence'
function bucketStart(date: string, bucket: string): string {
  const value = new Date(`${date}T00:00:00Z`)
  if (bucket === 'week') value.setUTCDate(value.getUTCDate() - ((value.getUTCDay() + 6) % 7))
  if (bucket === 'month') value.setUTCDate(1)
  return value.toISOString().slice(0, 10)
}
function endOfBucket(date: string, bucket: string): string {
  const value = new Date(`${date}T00:00:00Z`)
  if (bucket === 'week') value.setUTCDate(value.getUTCDate() + 6)
  if (bucket === 'month') {
    value.setUTCMonth(value.getUTCMonth() + 1)
    value.setUTCDate(0)
  }
  return value.toISOString().slice(0, 10)
}
function dates(scope: PerformanceScope, bucket: string): string[] {
  const values: string[] = []
  const end = Date.parse(scope.to!)
  for (let day = Date.parse(scope.from!); day <= end; day += 86400000)
    values.push(bucketStart(new Date(day).toISOString().slice(0, 10), bucket))
  return [...new Set(values)]
}
function measurement(
  rows: Cell[],
  date: string,
  scope: PerformanceScope,
  query: PerformanceQuery,
  dimension: string,
  observed = true,
): PerformancePoint {
  const counts = emptyCounts()
  for (const row of rows) add(counts, row.counts)
  const hasObservation = query.aggregation === 'period' ? observed : rows.length > 0
  const modelScope = scope.models != null || dimension === 'model'
  const result = score(counts, modelScope)
  if (!hasObservation) {
    result.summary = {}
    result.scenarios = result.scenarios?.map((item) => ({
      ...item,
      precision: null,
      recall: null,
      f1: null,
    }))
  }
  return {
    date,
    windowFrom:
      query.aggregation === 'period' ? (date < scope.from! ? scope.from : date) : scope.from,
    windowTo:
      endOfBucket(date, query.bucket ?? 'day') > scope.to!
        ? scope.to
        : endOfBucket(date, query.bucket ?? 'day'),
    score: result,
    unavailableReasons: [
      ...(modelScope ? ['miss-model-attribution-unavailable'] : []),
      ...(counts.provisionalMisses ? ['miss-observation-provisional'] : []),
      ...(counts.duplicateUnknown ? ['duplicate-verification-incomplete'] : []),
      ...(!hasObservation ? ['no-series-observation-in-period'] : []),
    ],
  }
}
export function mockPerformanceResponse(query: PerformanceQuery): PerformanceResponse {
  if (!query.views?.length || query.views.length > 2) throw new Error('Provide one or two views.')
  const views = query.views.map((scope, index) => {
    if (
      !scope.from ||
      !scope.to ||
      Date.parse(scope.to) - Date.parse(scope.from) > 365 * 86400000 ||
      scope.to < scope.from
    )
      throw new Error('The date window must be between 1 and 366 days.')
    const raw = cells(scope.from, scope.to)
    const selected = raw.filter((cell) => matches(cell, scope))
    const bucket = query.bucket ?? 'day'
    const grouping = query.grouping ?? 'none'
    const availableClients = clients.filter(
      (client) => scope.clientIds == null || scope.clientIds.includes(client.id),
    )
    const facets: PerformanceFacets = {
      clients: availableClients,
      repositories: scopes
        .filter((item) => availableClients.some((client) => client.id === item.client))
        .map((item) => ({ id: repoId(item), label: item.label })),
      models,
      types,
      qualifiers,
    }
    const keys =
      grouping === 'none'
        ? ['all']
        : [...new Set(selected.map((cell) => key(cell, grouping)))].sort()
    const series = keys.map((id) => ({
      id,
      label: label(id, grouping),
      points: dates(scope, bucket).map((date) => {
        const periodEnd = endOfBucket(date, bucket)
        const rows = selected.filter(
          (cell) =>
            (grouping === 'none' || key(cell, grouping) === id) &&
            cell.date <= periodEnd &&
            (query.aggregation !== 'period' || cell.date >= date),
        )
        return measurement(
          rows,
          date,
          scope,
          query,
          grouping,
          selected.some(
            (cell) =>
              (grouping === 'none' || key(cell, grouping) === id) &&
              cell.date >= date &&
              cell.date <= periodEnd,
          ),
        )
      }),
    }))
    const breakdown: PerformanceMatrixCell[] = []
    const axes = query.breakdown
    if (axes && (axes.viewIndex ?? 0) === index) {
      const date = bucketStart(axes.date ?? scope.to, bucket)
      const periodEnd = endOfBucket(date, bucket)
      const rows = selected.filter(
        (cell) => cell.date <= periodEnd && (query.aggregation !== 'period' || cell.date >= date),
      )
      for (const rowId of new Set(rows.map((cell) => key(cell, axes.rows!))))
        for (const columnId of new Set(rows.map((cell) => key(cell, axes.columns!)))) {
          const intersection = rows.filter(
            (cell) => key(cell, axes.rows!) === rowId && key(cell, axes.columns!) === columnId,
          )
          const scoped = {
            ...scope,
            models:
              axes.rows === 'model' || axes.columns === 'model'
                ? [axes.rows === 'model' ? rowId : columnId]
                : scope.models,
          }
          breakdown.push({
            rowId,
            rowLabel: label(rowId, axes.rows!),
            columnId,
            columnLabel: label(columnId, axes.columns!),
            measurement: measurement(
              intersection,
              date,
              scoped,
              query,
              grouping,
              intersection.length > 0,
            ),
          })
        }
    }
    return {
      scope: { ...scope, clientIds: availableClients.map((client) => client.id) },
      facets,
      series,
      breakdown,
      aggregateCells: selected.length,
      evidence: {
        oldestProjectionAt: new Date().toISOString(),
        newestProjectionAt: new Date().toISOString(),
        projectionVersion: 3,
        pendingSourceAggregates: 0,
      },
    }
  })
  return {
    calculationVersion: version,
    capturedAt: new Date().toISOString(),
    evidenceRevision: 'mock-retained-population-v1',
    query,
    premiseIds: score(emptyCounts(), false).scenarios?.map((item) => item.id!),
    views,
  }
}
const reports = new Map<string, { request: string; report: PerformanceSavedReport }>()
export function reviewerPerformanceHandlers(base: string) {
  return [
    http.post(`${base}/reviewer-performance/ranges/query`, async ({ request }) => {
      await delay(80)
      try {
        return HttpResponse.json(
          mockPerformanceResponse((await request.json()) as PerformanceQuery),
        )
      } catch (error) {
        return HttpResponse.json({ detail: (error as Error).message }, { status: 400 })
      }
    }),
    http.get(`${base}/reviewer-performance/reports`, () =>
      HttpResponse.json([...reports.values()].map((item) => item.report.report)),
    ),
    http.post(`${base}/reviewer-performance/reports`, async ({ request }) => {
      const body =
        (await request.json()) as components['schemas']['ReviewerPerformanceSaveReportRequest']
      const requestKey = JSON.stringify(body)
      const existing = reports.get(body.id!)
      if (existing)
        return existing.request === requestKey
          ? HttpResponse.json(existing.report)
          : HttpResponse.json(
              { detail: 'The identifier belongs to another report.' },
              { status: 409 },
            )
      try {
        const response = mockPerformanceResponse(body.query!)
        if (!response.views?.some((view) => view.scope?.clientIds?.length))
          throw new Error('A saved report must include at least one authorized client.')
        const now = new Date()
        const report = {
          report: {
            id: body.id,
            name: body.name,
            calculationVersion: version,
            capturedAt: now.toISOString(),
            expiresAt: new Date(now.getTime() + 365 * 86400000).toISOString(),
          },
          response,
          compatibleVersion: true,
        }
        reports.set(body.id!, { request: requestKey, report: structuredClone(report) })
        return HttpResponse.json(report)
      } catch (error) {
        return HttpResponse.json({ detail: (error as Error).message }, { status: 400 })
      }
    }),
    http.get(`${base}/reviewer-performance/reports/:id`, ({ params }) => {
      const stored = reports.get(params.id as string)
      return stored ? HttpResponse.json(stored.report) : new HttpResponse(null, { status: 404 })
    }),
    http.delete(
      `${base}/reviewer-performance/reports/:id`,
      ({ params }) =>
        new HttpResponse(null, { status: reports.delete(params.id as string) ? 204 : 404 }),
    ),
  ]
}
