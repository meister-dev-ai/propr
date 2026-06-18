// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { describe, it, expect, vi, beforeEach } from 'vitest'
import { mount, flushPromises } from '@vue/test-utils'
import { nextTick, reactive } from 'vue'

const mockRoute = reactive({ params: { id: 'job-abc' }, query: {} as Record<string, unknown> })

const mockGet = vi.fn()
vi.mock('@/services/api', () => ({
  createAdminClient: vi.fn(() => ({ GET: mockGet })),
  UnauthorizedError: class UnauthorizedError extends Error {},
}))

vi.mock('vue-router', () => ({
  useRouter: () => ({ push: vi.fn() }),
  useRoute: () => mockRoute,
  RouterLink: { template: '<a><slot /></a>' },
}))

vi.mock('@/components/dialogs/ModalDialog.vue', () => ({
  default: {
    name: 'ModalDialog',
    props: ['isOpen', 'title'],
    template: '<div class="modal-stub"><slot /></div>',
  },
}))

vi.mock('@/components/ProgressOrb.vue', () => ({
  default: {
    name: 'ProgressOrb',
    template: '<div class="orb-stub" />',
  },
}))

vi.mock('markdown-it', () => {
  return {
    // vitest 4: a mock used with `new` must be a class/function, not an arrow factory.
    default: class {
      render(s: string) {
        return `<p>${s}</p>`
      }
    },
  }
})

vi.mock('dompurify', () => ({
  default: { sanitize: (s: string) => s },
}))

const makeComment = (
  severity: string,
  message: string,
  filePath: string,
  lineNumber = 1,
) => ({ severity, message, filePath, lineNumber })

const sampleProtocols = [
  {
    id: 'pass-1',
    jobId: 'job-abc',
    label: 'src/foo.ts',
    providerScopePath: 'https://dev.azure.com/example',
    providerProjectKey: 'proj',
    repositoryId: 'repo',
    pullRequestId: 42,
    resolvedReviewStrategy: 'fileByFile',
    strategySelectionSource: 'fallbackDefault',
    startedAt: '2024-01-01T00:00:00Z',
    completedAt: '2024-01-01T00:01:00Z',
    totalInputTokens: 100,
    totalOutputTokens: 50,
    finalSummary: null,
    finalComments: null,
    fileOutcome: {
      filePath: 'src/foo.ts',
      isComplete: true,
      isFailed: false,
      isExcluded: false,
      isCarriedForward: false,
      exclusionReason: null,
      errorMessage: null,
      isDegraded: false,
    },
    events: [],
  },
]

const makeProtocolEvent = (overrides: Record<string, unknown> = {}) => ({
  id: 'event-1',
  kind: 'operational',
  name: 'comment_relevance_filter_output',
  occurredAt: '2024-01-01T00:00:30Z',
  inputTokens: null,
  outputTokens: null,
  inputTextSample: JSON.stringify({
    implementationId: 'heuristic-v1',
    implementationVersion: '1.0.0',
    filePath: 'src/foo.ts',
    originalCommentCount: 2,
    keptCount: 1,
    discardedCount: 1,
    degradedComponents: [],
    fallbackChecks: [],
    degradedCause: null,
  }),
  outputSummary: JSON.stringify({
    implementationId: 'heuristic-v1',
    implementationVersion: '1.0.0',
    filePath: 'src/foo.ts',
    originalCommentCount: 2,
    keptCount: 1,
    discardedCount: 1,
    reasonBuckets: { summary_level_only: 1 },
    decisionSources: { deterministic_screening: 2 },
    degradedComponents: [],
    fallbackChecks: [],
    degradedCause: null,
    aiTokenUsage: null,
    discarded: [
      {
        filePath: 'src/foo.ts',
        lineNumber: null,
        severity: 'suggestion',
        message: 'Overall cleanup suggestion.',
        reasonCodes: ['summary_level_only'],
        decisionSource: 'deterministic_screening',
      },
    ],
  }),
  error: null,
  ...overrides,
})

const traceRichProtocol = {
  ...sampleProtocols[0],
  modelId: 'gpt-4.1',
  events: [
    makeProtocolEvent({
      id: 'event-ai-1',
      kind: 'aiCall',
      name: 'ai_call_iter_1',
      inputTextSample: 'Review src/foo.ts for suspicious auth behavior',
      outputSummary: 'Calling verification tool for src/foo.ts',
    }),
    makeProtocolEvent({
      id: 'event-tool-1',
      kind: 'toolCall',
      name: 'verification_evidence_collected',
      inputTextSample: 'searching src/foo.ts for suspicious evidence',
      outputSummary: 'Found suspicious evidence in auth flow [REDACTED]',
      occurredAt: '2024-01-01T00:00:31Z',
    }),
    makeProtocolEvent({
      id: 'event-op-1',
      kind: 'operational',
      name: 'summary_reconciliation',
      inputTextSample: null,
      outputSummary: 'summary reconciliation completed for synthesis',
      occurredAt: '2024-01-01T00:00:32Z',
    }),
  ],
}

const traceReviewWideProtocols = [
  {
    ...sampleProtocols[0],
    id: 'pass-posting',
    label: 'posting',
    fileOutcome: null,
    events: [
      makeProtocolEvent({
        id: 'event-posting-1',
        kind: 'operational',
        name: 'dedup_summary',
        inputTextSample: '{"candidateCount":0}',
        outputSummary: 'posting duplicate suppression metadata',
        occurredAt: '2024-01-01T00:00:25Z',
      }),
    ],
  },
  {
    ...traceRichProtocol,
    id: 'pass-src-foo',
    label: 'src/foo.ts',
  },
]

const sampleJobResult = {
  status: 'completed',
  submittedAt: '2024-01-01T00:00:00Z',
  completedAt: '2024-01-01T00:01:00Z',
  result: {
    summary: 'All good',
    comments: [
      makeComment('error', 'null pointer in auth', 'src/auth.ts', 10),
      makeComment('warning', 'unused import', 'src/auth.ts', 20),
      makeComment('info', 'consider extracting method', 'src/utils.ts', 5),
      makeComment('suggestion', 'add unit test for edge case', 'src/utils.ts', 15),
    ],
  },
}

const sampleJobDetail = {
  id: 'job-abc',
  clientId: 'client-123',
  status: 2,
  submittedAt: '2024-01-01T00:00:00Z',
  processingStartedAt: '2024-01-01T00:00:05Z',
  completedAt: '2024-01-01T00:01:00Z',
  totalInputTokens: 100,
  totalOutputTokens: 50,
  errorMessage: null,
  aiModel: 'gpt-4.1',
  reviewTemperature: 0.35,
  tokenBreakdown: [],
  breakdownConsistent: true,
}

type MockGetOptions = {
  params?: {
    path?: {
      id?: string
      protocolId?: string
    }
    query?: {
      includeEvents?: boolean
    }
  }
}

function createProtocolMock(
  protocols: Array<Record<string, any>>,
  result: Record<string, any> = sampleJobResult,
  detail: Record<string, any> = sampleJobDetail,
) {
  return (path: string, options?: MockGetOptions) => {
    if (path === '/jobs/{id}/protocol/{protocolId}') {
      const protocolId = options?.params?.path?.protocolId
      const protocol = protocols.find((candidate) => candidate.id === protocolId) ?? protocols[0]
      return Promise.resolve({ data: protocol, response: { ok: true } })
    }

    if (path === '/jobs/{id}/protocol') {
      return Promise.resolve({ data: protocols, response: { ok: true } })
    }

    if (path === '/jobs/{id}') {
      return Promise.resolve({ data: detail, response: { ok: true } })
    }

    return Promise.resolve({ data: result, response: { ok: true } })
  }
}

async function mountView() {
  const { default: JobProtocolView } = await import('@/features/job-protocol/views/JobProtocolView.vue')
  const wrapper = mount(JobProtocolView, {
    attachTo: document.body,
  })
  await flushPromises()
  return wrapper
}

async function openTraceEventModal(wrapper: Awaited<ReturnType<typeof mountView>>) {
  const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
  expect(tracesTab).toBeTruthy()
  await tracesTab!.trigger('click')
  await flushPromises()

  const eventRow = wrapper.find('article.event-card.row-clickable')
  expect(eventRow.exists()).toBe(true)
  await eventRow.trigger('click')
  await flushPromises()
}

async function openTraceSearchPanel(wrapper: Awaited<ReturnType<typeof mountView>>) {
  const toggleButton = wrapper.get('[data-testid="trace-search-toggle"]')
  expect(wrapper.find('[data-testid="trace-search-panel"]').isVisible()).toBe(false)
  await toggleButton.trigger('click')
  await flushPromises()
  await nextTick()
}

describe('JobProtocolView — comment search and filter (T042)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.resetModules()
    mockRoute.params.id = 'job-abc'
    mockRoute.query = {}
    mockGet.mockImplementation(createProtocolMock(sampleProtocols))
  })

  it('shows general review settings in the summary overview', async () => {
    const wrapper = await mountView()

    expect(wrapper.find('.app-top-bar').exists()).toBe(true)
    expect(wrapper.text()).toContain('Model')
    expect(wrapper.text()).toContain('gpt-4.1')
    expect(wrapper.text()).toContain('Temperature')
    expect(wrapper.text()).toContain('0.35')
  }, 10000)

  it('renders cached and effective input totals plus per-call cache evidence', async () => {
    mockGet.mockImplementation(createProtocolMock([
      {
        ...sampleProtocols[0],
        totalInputTokens: 2000,
        totalOutputTokens: 300,
        totalCachedInputTokens: 1200,
        cacheObservability: 'observable',
        events: [
          makeProtocolEvent({
            id: 'cache-event',
            kind: 'aiCall',
            name: 'ai_call_iter_1',
            inputTokens: 2000,
            cachedInputTokens: 1200,
            cacheStatus: 'hit',
            outputTokens: 300,
            inputTextSample: null,
            outputSummary: null,
          }),
        ],
      },
    ]))

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    expect(tracesTab).toBeTruthy()
    await tracesTab!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Cached Input')
    expect(wrapper.text()).toContain('1,200')
    expect(wrapper.text()).toContain('Effective Input')
    expect(wrapper.text()).toContain('800')
    expect(wrapper.text()).toContain('Cached 1,200 · hit')
  })

  it('renders legacy cache rows as not captured and surfaces tool/finalization evidence', async () => {
    mockGet.mockImplementation(createProtocolMock([
      {
        ...sampleProtocols[0],
        totalInputTokens: 2000,
        totalOutputTokens: 300,
        totalCachedInputTokens: null,
        events: [
          makeProtocolEvent({
            id: 'final-event',
            kind: 'aiCall',
            name: 'ai_call_iter_1',
            inputTokens: 2000,
            cachedInputTokens: null,
            cacheStatus: 'notApplicable',
            outputTokens: 300,
            inputTextSample: null,
            outputSummary: null,
            finalizationAttemptKind: 'ForcedFinal',
            finalizationOutcome: 'ProducedFinalText',
          }),
          makeProtocolEvent({
            id: 'tool-event',
            kind: 'toolCall',
            name: 'get_file_content',
            inputTextSample: null,
            outputSummary: null,
            toolEvidence: {
              sourceToolName: 'get_file_content',
              originalPayloadTokens: 2000,
              boundedPayloadTokens: 256,
              action: 'Bounded',
              refreshable: true,
            },
          }),
        ],
      },
    ]))

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    expect(tracesTab).toBeTruthy()
    await tracesTab!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Cached — · not captured')
    expect(wrapper.text()).toContain('ForcedFinal: ProducedFinalText')
    expect(wrapper.text()).toContain('Evidence Bounded')
  })

  it('renders tool timing summaries and phase details for timed tool calls', async () => {
    mockGet.mockImplementation(createProtocolMock([
      {
        ...sampleProtocols[0],
        events: [
          makeProtocolEvent({
            id: 'timed-tool',
            kind: 'toolCall',
            name: 'search_source_repo',
            inputTextSample: '{"searchTerm":"needle"}',
            outputSummary: '{"status":"success"}',
            startedAt: '2024-01-01T00:00:10Z',
            completedAt: '2024-01-01T00:00:12Z',
            durationMs: 2100,
            waitDurationMs: 400,
            activeDurationMs: 1700,
            timingAvailability: 'captured',
            toolOutcome: 'succeeded',
            phaseTimings: [
              {
                name: 'scm_file_tree_fetch',
                displayName: 'SCM file tree fetch',
                sequence: 1,
                occurrence: null,
                startedAt: '2024-01-01T00:00:10Z',
                completedAt: '2024-01-01T00:00:11Z',
                durationMs: 900,
                availability: 'captured',
                outcome: 'succeeded',
                summary: 'candidate_paths=42',
              },
              {
                name: 'repository_search',
                displayName: 'Repository search',
                sequence: 2,
                occurrence: null,
                startedAt: '2024-01-01T00:00:11Z',
                completedAt: '2024-01-01T00:00:12Z',
                durationMs: 1200,
                availability: 'captured',
                outcome: 'succeeded',
                summary: 'matches=2',
              },
            ],
          }),
        ],
      },
    ]))

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    expect(tracesTab).toBeTruthy()
    await tracesTab!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('2s')
    expect(wrapper.text()).toContain('Active 1s')
    expect(wrapper.text()).toContain('Wait 400 ms')
    expect(wrapper.text()).toContain('2 phases')

    const eventRow = wrapper.find('article.event-card.row-clickable')
    expect(eventRow.exists()).toBe(true)
    await eventRow.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Tool Timing')
    expect(wrapper.text()).toContain('Availability')
    expect(wrapper.text()).toContain('SCM file tree fetch')
    expect(wrapper.text()).toContain('candidate_paths=42')
    expect(wrapper.text()).toContain('matches=2')
    expect(wrapper.text()).not.toContain('Show raw occurrences')
    expect(wrapper.text()).toContain('Input')
    expect(wrapper.text()).toContain('Output')
    const modalText = wrapper.find('.merged-modal-layout').text()
    expect(modalText.indexOf('Input')).toBeLessThan(modalText.indexOf('Output'))
    expect(modalText.indexOf('Output')).toBeLessThan(modalText.indexOf('Tool Timing'))
  })

  it('groups repeated phase timings and keeps raw occurrences collapsed by default', async () => {
    mockGet.mockImplementation(createProtocolMock([
      {
        ...sampleProtocols[0],
        events: [
          makeProtocolEvent({
            id: 'timed-tool-grouped',
            kind: 'toolCall',
            name: 'search_source_repo',
            inputTextSample: '{"searchTerm":"needle"}',
            outputSummary: '{"status":"success"}',
            durationMs: 4800,
            waitDurationMs: 1200,
            activeDurationMs: 3600,
            timingAvailability: 'captured',
            toolOutcome: 'succeeded',
            phaseTimings: [
              {
                name: 'repository_search',
                displayName: 'Repository search',
                sequence: 1,
                occurrence: 1,
                startedAt: '2024-01-01T00:00:10Z',
                completedAt: '2024-01-01T00:00:11Z',
                durationMs: 1000,
                availability: 'captured',
                outcome: 'succeeded',
                summary: 'matches=1',
              },
              {
                name: 'repository_search',
                displayName: 'Repository search',
                sequence: 2,
                occurrence: 2,
                startedAt: '2024-01-01T00:00:11Z',
                completedAt: '2024-01-01T00:00:13Z',
                durationMs: 2000,
                availability: 'captured',
                outcome: 'succeeded',
                summary: 'matches=4',
              },
              {
                name: 'retry_backoff',
                displayName: 'Retry backoff',
                sequence: 3,
                occurrence: 1,
                startedAt: '2024-01-01T00:00:13Z',
                completedAt: '2024-01-01T00:00:14Z',
                durationMs: 1200,
                availability: 'captured',
                outcome: 'succeeded',
                summary: 'waited-for-retry',
              },
            ],
          }),
        ],
      },
    ]))

    const wrapper = await mountView()
    await openTraceEventModal(wrapper)

    expect(wrapper.text()).toContain('3 phases across 2 groups')
    expect(wrapper.text()).toContain('2 groups / 3 occurrences')
    expect(wrapper.text()).toContain('Repository search')
    expect(wrapper.text()).toContain('3s total')
    expect(wrapper.text()).toContain('2 occurrences')
    expect(wrapper.text()).toContain('2 distinct summaries recorded')
    expect(wrapper.text()).toContain('Retry backoff')
    expect(wrapper.text()).toContain('Show raw occurrences')
    expect(wrapper.text()).not.toContain('Repository search #1')
    expect(wrapper.text()).not.toContain('Repository search #2')

    const toggle = wrapper.find('button.tool-phase-toggle')
    expect(toggle.exists()).toBe(true)
    await toggle.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Hide raw occurrences')
    expect(wrapper.text()).toContain('Repository search #1')
    expect(wrapper.text()).toContain('Repository search #2')
    expect(wrapper.text()).toContain('matches=1')
    expect(wrapper.text()).toContain('matches=4')
  })

  it('surfaces cross-pass timing insights and pass-level slowest call cues', async () => {
    mockGet.mockImplementation(createProtocolMock([
      {
        ...sampleProtocols[0],
        id: 'pass-1',
        label: 'src/foo.ts',
        events: [
          makeProtocolEvent({
            id: 'foo-fast',
            kind: 'toolCall',
            name: 'get_file_content',
            durationMs: 900,
            timingAvailability: 'captured',
            toolOutcome: 'succeeded',
          }),
          makeProtocolEvent({
            id: 'foo-slow',
            kind: 'toolCall',
            name: 'search_source_repo',
            durationMs: 3200,
            waitDurationMs: 600,
            activeDurationMs: 2600,
            timingAvailability: 'captured',
            toolOutcome: 'succeeded',
            phaseTimings: [],
          }),
        ],
      },
      {
        ...sampleProtocols[0],
        id: 'pass-2',
        label: 'src/bar.ts',
        startedAt: '2024-01-01T00:02:00Z',
        completedAt: '2024-01-01T00:03:00Z',
        events: [
          makeProtocolEvent({
            id: 'bar-slow',
            kind: 'toolCall',
            name: 'search_code',
            durationMs: 5100,
            waitDurationMs: 1000,
            activeDurationMs: 4100,
            timingAvailability: 'captured',
            toolOutcome: 'succeeded',
            phaseTimings: [{
              name: 'repository_search',
              displayName: 'Repository search',
              sequence: 1,
              occurrence: null,
              startedAt: '2024-01-01T00:02:01Z',
              completedAt: '2024-01-01T00:02:06Z',
              durationMs: 5100,
              availability: 'captured',
              outcome: 'succeeded',
              summary: 'matches=3',
            }],
          }),
        ],
      },
    ]))

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    expect(tracesTab).toBeTruthy()
    await tracesTab!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Timing Insights')
    expect(wrapper.text()).toContain('#1')
    expect(wrapper.text()).toContain('search_code')
    expect(wrapper.text()).toContain('src/bar.ts')
    expect(wrapper.text()).toContain('5s')
    expect(wrapper.text()).toContain('Current pass')
    expect(wrapper.text()).toContain('Wait 1s')
    expect(wrapper.text()).toContain('Active 4s')
    expect(wrapper.text()).not.toContain('5 phases')
  })

  it('keeps timing insights scoped to the selected pass', async () => {
    mockGet.mockImplementation(createProtocolMock([
      {
        ...sampleProtocols[0],
        id: 'pass-1',
        label: 'src/foo.ts',
        events: [
          makeProtocolEvent({
            id: 'foo-fast',
            kind: 'toolCall',
            name: 'get_file_content',
            durationMs: 900,
            timingAvailability: 'captured',
            toolOutcome: 'succeeded',
          }),
        ],
      },
      {
        ...sampleProtocols[0],
        id: 'pass-2',
        label: 'src/bar.ts',
        startedAt: '2024-01-01T00:02:00Z',
        completedAt: '2024-01-01T00:03:00Z',
        events: [
          makeProtocolEvent({
            id: 'bar-slow',
            kind: 'toolCall',
            name: 'search_source_repo',
            durationMs: 5100,
            waitDurationMs: 1000,
            activeDurationMs: 4100,
            timingAvailability: 'captured',
            toolOutcome: 'succeeded',
          }),
        ],
      },
    ]))

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    let insightsSection = wrapper.find('.timing-insights-list')
    expect(insightsSection.exists()).toBe(true)
    expect(insightsSection.text()).toContain('search_source_repo')
    expect(insightsSection.text()).not.toContain('get_file_content')

    const trigger = wrapper.find('[data-testid="pass-selector"] .pass-selector-trigger')
    expect(trigger.exists()).toBe(true)
    await trigger.trigger('click')
    await flushPromises()

    const fooOption = document.querySelector('[data-testid="pass-selector"] .pass-selector-option')
    const allOptions = document.querySelectorAll('[data-testid="pass-selector"] .pass-selector-option')
    const fooOptionEl = Array.from(allOptions).find((el) => el.textContent?.includes('foo.ts'))
    expect(fooOptionEl).toBeTruthy()
    fooOptionEl!.dispatchEvent(new MouseEvent('click', { bubbles: true }))
    await flushPromises()

    insightsSection = wrapper.find('.timing-insights-list')
    expect(insightsSection.exists()).toBe(true)
    expect(insightsSection.text()).toContain('get_file_content')
    expect(insightsSection.text()).not.toContain('search_source_repo')
  })

  it('does not treat malformed phase timing payloads as character-count phase totals', async () => {
    mockGet.mockImplementation(createProtocolMock([
      {
        ...sampleProtocols[0],
        events: [
          makeProtocolEvent({
            id: 'timed-tool-weird-phase',
            kind: 'toolCall',
            name: 'search_source_repo',
            durationMs: 2100,
            waitDurationMs: 400,
            activeDurationMs: 1700,
            timingAvailability: 'captured',
            toolOutcome: 'succeeded',
            phaseTimings: '{"unexpected":"serialized"}' as unknown as never,
          }),
        ],
      },
    ]))

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    expect(tracesTab).toBeTruthy()
    await tracesTab!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).not.toContain('serialized')
    expect(wrapper.text()).not.toContain('22 phases')
    expect(wrapper.text()).not.toContain('2594 phases')
  })

  it('reloads protocol data when navigating between job ids on the same route', async () => {
    const nextJobProtocols = [
      {
        ...sampleProtocols[0],
        id: 'pass-2',
        jobId: 'job-def',
        label: 'src/bar.ts',
      },
    ]
    const nextJobResult = {
      ...sampleJobResult,
      result: {
        summary: 'Needs follow-up',
        comments: [makeComment('warning', 'stale dependency in queue worker', 'src/bar.ts', 33)],
      },
    }
    const nextJobDetail = {
      ...sampleJobDetail,
      id: 'job-def',
      aiModel: 'gpt-4.2',
      reviewTemperature: 0.8,
    }

    mockGet.mockImplementation((path: string, options?: MockGetOptions) => {
      const jobId = options?.params?.path?.id
      if (jobId === 'job-def') {
        return createProtocolMock(nextJobProtocols, nextJobResult, nextJobDetail)(path, options)
      }

      return createProtocolMock(sampleProtocols)(path, options)
    })

    const wrapper = await mountView()
    expect(wrapper.text()).toContain('gpt-4.1')
    expect(wrapper.text()).toContain('0.35')
    expect(wrapper.text()).toContain('All good')
    expect(wrapper.text()).toContain('src/auth.ts')

    mockRoute.params.id = 'job-def'
    await nextTick()
    await flushPromises()

    expect(wrapper.text()).toContain('gpt-4.2')
    expect(wrapper.text()).toContain('0.80')
    expect(wrapper.text()).toContain('src/bar.ts')
    expect(wrapper.text()).toContain('Needs follow-up')
    expect(wrapper.text()).not.toContain('src/auth.ts')
    expect(mockGet).toHaveBeenCalledWith('/jobs/{id}/protocol', expect.objectContaining({
      params: {
        path: { id: 'job-def' },
        query: { includeEvents: false },
      },
    }))
    expect(mockGet).toHaveBeenCalledWith('/jobs/{id}/protocol/{protocolId}', expect.objectContaining({
      params: {
        path: {
          id: 'job-def',
          protocolId: 'pass-2',
        },
      },
    }))
    expect(mockGet).toHaveBeenCalledWith('/jobs/{id}', expect.objectContaining({ params: { path: { id: 'job-def' } } }))
  })

  it('shows all comments when no search and no severity filter', async () => {
    const wrapper = await mountView()
    // All 4 comments should be rendered
    const items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(4)
  })

  it('filters comments by message text (case-insensitive)', async () => {
    const wrapper = await mountView()
    const searchInput = wrapper.find('input.comment-search-input')
    expect(searchInput.exists()).toBe(true)
    await searchInput.setValue('null pointer')
    await flushPromises()
    const items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(1)
    expect(items[0].text()).toContain('null pointer in auth')
  })

  it('filters comments by file path (case-insensitive)', async () => {
    const wrapper = await mountView()
    const searchInput = wrapper.find('input.comment-search-input')
    await searchInput.setValue('utils.ts')
    await flushPromises()
    const items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(2)
  })

  it('filters comments by severity toggle', async () => {
    const wrapper = await mountView()
    const errorPill = wrapper.find('button.severity-pill[data-severity="error"]')
    expect(errorPill.exists()).toBe(true)
    await errorPill.trigger('click')
    await flushPromises()
    const items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(1)
    expect(items[0].text()).toContain('null pointer in auth')
  })

  it('applies combined search + severity filter', async () => {
    const wrapper = await mountView()
    // Activate warning filter
    const warningPill = wrapper.find('button.severity-pill[data-severity="warning"]')
    await warningPill.trigger('click')
    // Also search for 'auth' — only 'unused import' in auth.ts with severity=warning should match
    const searchInput = wrapper.find('input.comment-search-input')
    await searchInput.setValue('auth')
    await flushPromises()
    const items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(1)
    expect(items[0].text()).toContain('unused import')
  })

  it('clicking active severity pill deactivates it (toggle off)', async () => {
    const wrapper = await mountView()
    const infoBtn = wrapper.find('button.severity-pill[data-severity="info"]')
    await infoBtn.trigger('click')
    await flushPromises()
    let items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(1) // only info comments

    // Toggle off
    await infoBtn.trigger('click')
    await flushPromises()
    items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(4) // all comments back
  })

  it('shows empty state message when filter matches nothing', async () => {
    const wrapper = await mountView()
    const searchInput = wrapper.find('input.comment-search-input')
    await searchInput.setValue('zzznomatch')
    await flushPromises()
    const items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(0)
    expect(wrapper.find('.comments-empty-state').exists()).toBe(true)
  })

  it('polling update (reviewStatus ref change) does not reset searchQuery', async () => {
    const wrapper = await mountView()
    const searchInput = wrapper.find('input.comment-search-input')
    await searchInput.setValue('null pointer')
    await flushPromises()

    // Simulate polling update: mock returns new result with same comments
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) return Promise.resolve({ data: sampleProtocols, response: { ok: true } })
      return Promise.resolve({
        data: { ...sampleJobResult, result: { ...sampleJobResult.result } },
        response: { ok: true },
      })
    })

    // Wait for a poll interval (if polling occurs) — otherwise just re-assert
    await flushPromises()

    // searchQuery should not be reset
    const inputEl = wrapper.find('input.comment-search-input')
    expect((inputEl.element as HTMLInputElement).value).toBe('null pointer')

    // Filter still applied
    const items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(1)
  })

  it('polling update does not reset activeSeverities', async () => {
    const wrapper = await mountView()
    const errorPill = wrapper.find('button.severity-pill[data-severity="error"]')
    await errorPill.trigger('click')
    await flushPromises()

    // Simulate polling
    await flushPromises()

    const items = wrapper.findAll('.json-comment-item.synthesis-comment')
    expect(items.length).toBe(1) // error filter still active
  })

  it('renders comment relevance filter output with discarded details in the trace modal', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{ ...sampleProtocols[0], events: [makeProtocolEvent()] }],
          response: { ok: true },
        })
      }

      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    await openTraceEventModal(wrapper)

    expect(wrapper.text()).toContain('heuristic-v1 @ 1.0.0')
    expect(wrapper.text()).toContain('2 original -> 1 kept / 1 discarded')
    expect(wrapper.text()).toContain('summary_level_only')
    expect(wrapper.text()).toContain('Overall cleanup suggestion.')
    expect(wrapper.text()).toContain('deterministic_screening')
  })

  it('renders degraded markers and AI token usage for comment relevance events', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            events: [makeProtocolEvent({
              name: 'comment_relevance_evaluator_degraded',
              inputTextSample: JSON.stringify({
                implementationId: 'hybrid-v1',
                implementationVersion: '1.0.0',
                filePath: 'src/foo.ts',
                originalCommentCount: 1,
                keptCount: 1,
                discardedCount: 0,
                degradedComponents: ['comment_relevance_evaluator'],
                fallbackChecks: ['pre_filter_comments_retained'],
                degradedCause: 'Evaluator timeout.',
              }),
              outputSummary: JSON.stringify({
                implementationId: 'hybrid-v1',
                implementationVersion: '1.0.0',
                filePath: 'src/foo.ts',
                originalCommentCount: 1,
                keptCount: 1,
                discardedCount: 0,
                reasonBuckets: {},
                decisionSources: { fallback_mode: 1 },
                degradedComponents: ['comment_relevance_evaluator'],
                fallbackChecks: ['pre_filter_comments_retained'],
                degradedCause: 'Evaluator timeout.',
                aiTokenUsage: {
                  implementationId: 'hybrid-v1',
                  filePath: 'src/foo.ts',
                  inputTokens: 320,
                  outputTokens: 71,
                  modelCategory: 0,
                  modelId: 'test-evaluator',
                },
                discarded: [],
              }),
            })],
          }],
          response: { ok: true },
        })
      }

      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    await openTraceEventModal(wrapper)

    expect(wrapper.text()).toContain('comment_relevance_evaluator')
    expect(wrapper.text()).toContain('pre_filter_comments_retained')
    expect(wrapper.text()).toContain('Evaluator timeout.')
    expect(wrapper.text()).toContain('Input tokens: 320')
    expect(wrapper.text()).toContain('Output tokens: 71')
    expect(wrapper.text()).toContain('Model: test-evaluator')
  })

  it('renders final-gate summary and decision details in the trace modal', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            events: [makeProtocolEvent({
              name: 'review_finding_gate_decision',
              inputTextSample: JSON.stringify({
                candidateCount: 3,
                publishCount: 1,
                summaryOnlyCount: 1,
                dropCount: 1,
              }),
              outputSummary: JSON.stringify({
                findingId: 'finding-001',
                disposition: 'Drop',
                category: 'per_file_comment',
                provenance: {
                  originKind: 'per_file_comment',
                  generatedByStage: 'per_file_review',
                  sourceFilePath: 'src/foo.ts',
                  sourceFileResultId: 'file-result-001',
                },
                evidence: {
                  supportingFindingIds: ['finding-pf-001'],
                  supportingFiles: ['src/foo.ts'],
                  evidenceResolutionState: 'partial',
                  evidenceSource: 'synthesis_payload',
                },
                reasonCodes: ['invariant_contradiction'],
                blockedInvariantIds: ['review_comment_message_required'],
                ruleSource: 'invariant_contradiction_rules',
                summaryText: null,
              }),
            })],
          }],
          response: { ok: true },
        })
      }

      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    await openTraceEventModal(wrapper)

    expect(wrapper.text()).toContain('finding-001')
    expect(wrapper.text()).toContain('Drop')
    expect(wrapper.text()).toContain('per_file_comment')
    expect(wrapper.text()).toContain('per_file_review')
    expect(wrapper.text()).toContain('src/foo.ts')
    expect(wrapper.text()).toContain('invariant_contradiction')
    expect(wrapper.text()).toContain('review_comment_message_required')
    expect(wrapper.text()).toContain('synthesis_payload')
  })

  it('renders verification degraded details in the trace modal', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            events: [makeProtocolEvent({
              name: 'verification_degraded',
              inputTextSample: JSON.stringify({
                findingId: 'finding-cc-001',
                claimId: 'claim-001',
                stage: 'PrLevel',
                degradedComponent: 'evidence_collection',
              }),
              outputSummary: null,
              error: 'ProCursor unavailable.',
            })],
          }],
          response: { ok: true },
        })
      }

      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    await openTraceEventModal(wrapper)

    expect(wrapper.text()).toContain('finding-cc-001')
    expect(wrapper.text()).toContain('claim-001')
    expect(wrapper.text()).toContain('evidence_collection')
    expect(wrapper.text()).toContain('ProCursor unavailable.')
  })

  it('renders verification evidence attempts and ProCursor status in the trace modal', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            events: [makeProtocolEvent({
              name: 'verification_evidence_collected',
              inputTextSample: JSON.stringify({
                findingId: 'finding-cc-001',
                claimId: 'claim-001',
                coverageState: 'Missing',
              }),
              outputSummary: JSON.stringify({
                claimId: 'claim-001',
                evidenceItems: [],
                coverageState: 'Missing',
                retrievalNotes: 'No independent PR-level evidence was collected.',
                evidenceAttempts: [
                  {
                    attemptId: 'claim-001:attempt:001',
                    claimId: 'claim-001',
                    sourceFamily: 'ProCursorKnowledge',
                    attemptOrder: 1,
                    status: 'Empty',
                    scopeSummary: 'Queried ProCursor knowledge for the claim assertion.',
                    coverageImpact: 'NoChange',
                    failureReason: 'No matches.',
                  },
                ],
                hasProCursorAttempt: true,
                proCursorResultStatus: 'Empty',
              }),
            })],
          }],
          response: { ok: true },
        })
      }

      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    await openTraceEventModal(wrapper)

    expect(wrapper.text()).toContain('ProCursor Result:')
    expect(wrapper.text()).toContain('Empty')
    expect(wrapper.text()).toContain('Evidence Attempts:')
    expect(wrapper.text()).toContain('ProCursorKnowledge')
    expect(wrapper.text()).toContain('No matches.')
  })

  it('renders summary reconciliation and enriched final-gate summary details in the trace modal', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            events: [makeProtocolEvent({
              name: 'review_finding_gate_summary',
              inputTextSample: JSON.stringify({
                candidateCount: 3,
                publishCount: 1,
                summaryOnlyCount: 1,
                dropCount: 1,
              }),
              outputSummary: JSON.stringify({
                candidateCount: 3,
                publishCount: 1,
                summaryOnlyCount: 1,
                dropCount: 1,
                categoryCounts: { cross_cutting: 2 },
                invariantBlockedCount: 1,
                originalSummary: 'Original unsafe summary.',
                finalSummary: 'Verification retained 1 publishable finding.',
                summaryRewritePerformed: true,
                droppedFindingIds: ['finding-drop-1'],
                summaryOnlyFindingIds: ['finding-summary-1'],
                summaryRuleSource: 'deterministic_summary_rewrite',
              }),
            })],
          }],
          response: { ok: true },
        })
      }

      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    await openTraceEventModal(wrapper)

    expect(wrapper.text()).toContain('Original unsafe summary.')
    expect(wrapper.text()).toContain('Verification retained 1 publishable finding.')
    expect(wrapper.text()).toContain('finding-drop-1')
    expect(wrapper.text()).toContain('deterministic_summary_rewrite')
  })

  it('renders full raw protocol text in the trace modal without silent truncation', async () => {
    const fullInput = `${'input-chunk-'.repeat(600)}END-INPUT`
    const fullOutput = `${'output-chunk-'.repeat(600)}END-OUTPUT`

    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            events: [makeProtocolEvent({
              kind: 'toolCall',
              name: 'read_file',
              inputTextSample: `args=${fullInput}`,
              outputSummary: fullOutput,
            })],
          }],
          response: { ok: true },
        })
      }

      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    await openTraceEventModal(wrapper)

    const blocks = wrapper.findAll('pre.content-block')
    expect(blocks.length).toBeGreaterThanOrEqual(2)
    expect(blocks[0].text()).toContain('END-INPUT')
    expect(blocks[1].text()).toContain('END-OUTPUT')
    expect(blocks[1].text()).not.toContain('[TRUNCATED]')
    expect(blocks[1].text()).toBe(fullOutput)
  })

  it('renders escaped raw protocol text as readable code in the trace modal', async () => {
    const escapedOutput = 'name=\\"inputTextSample\\"\\n/// &lt;param name=\\"outputTextSample\\"&gt;Truncated AI response text (&lt;= 50,000 characters), or &lt;see langword=\\"null\\" /&gt;.&lt;/param&gt;'

    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            events: [makeProtocolEvent({
              kind: 'toolCall',
              name: 'get_file_content',
              inputTextSample: 'args={"path":"src/foo.ts"}',
              outputSummary: escapedOutput,
            })],
          }],
          response: { ok: true },
        })
      }

      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    await openTraceEventModal(wrapper)

    const blocks = wrapper.findAll('pre.content-block')
    const outputText = blocks[blocks.length - 1].text()

    expect(outputText).toBe('name="inputTextSample"\n/// <param name="outputTextSample">Truncated AI response text (<= 50,000 characters), or <see langword="null" />.</param>')
    expect(outputText).not.toContain('\\n')
    expect(outputText).not.toContain('\\"')
    expect(outputText).not.toContain('&lt;')
  })

  it('renders JSON string protocol output without re-escaping quotes and line breaks', async () => {
    const readableOutput = 'name="inputTextSample"\nTask RecordAiCallAsync(Guid protocolId)'

    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            events: [makeProtocolEvent({
              kind: 'toolCall',
              name: 'get_file_content',
              inputTextSample: 'args={"path":"src/foo.ts"}',
              outputSummary: JSON.stringify(readableOutput),
            })],
          }],
          response: { ok: true },
        })
      }

      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    await openTraceEventModal(wrapper)

    const blocks = wrapper.findAll('pre.content-block')
    const outputText = blocks[blocks.length - 1].text()

    expect(outputText).toBe(readableOutput)
    expect(outputText).not.toContain('\\n')
    expect(outputText).not.toContain('\\"')
  })

  it('does not show executing for completed posting diagnostics that have no output payload', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            label: 'posting',
            events: [makeProtocolEvent({
              kind: 'operational',
              name: 'dedup_summary',
              inputTextSample: JSON.stringify({ candidateCount: 2, postedCount: 1, suppressedCount: 1 }),
              outputSummary: null,
              error: null,
            })],
            finalSummary: null,
            finalComments: null,
          }],
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('dedup_summary')
    expect(wrapper.text()).not.toContain('Executing...')
  })

  it('does not show executing for completed evaluator AI calls that only recorded token usage', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            events: [makeProtocolEvent({
              kind: 'aiCall',
              name: 'ai_call_comment_relevance_evaluator',
              inputTokens: 202,
              outputTokens: 31,
              inputTextSample: JSON.stringify({
                filePath: 'src/foo.ts',
                implementationId: 'hybrid-v1',
              }),
              outputSummary: null,
              error: null,
            })],
            finalSummary: null,
            finalComments: null,
          }],
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('ai_call_comment_relevance_evaluator')
    expect(wrapper.text()).not.toContain('Executing...')

    const eventRow = wrapper.find('article.event-card.row-clickable')
    expect(eventRow.exists()).toBe(true)
    await eventRow.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('No output captured for this completed step.')
    expect(wrapper.text()).not.toContain('Currently Executing...')
  })

  it('explains provider-managed token accounting when the selected AI call uses provider-managed sessions', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            events: [
              makeProtocolEvent({
                id: 'event-ai-1',
                kind: 'aiCall',
                name: 'ai_call_iter_2',
                inputTokens: 5193,
                outputTokens: 287,
                inputTextSample: '[call_123]\\n{"status":"partial"}',
                outputSummary: '{"summary":"ok","comments":[]}',
              }),
              makeProtocolEvent({
                id: 'event-session-1',
                kind: 'operational',
                name: 'review_agent_session_turn',
                inputTextSample: JSON.stringify({
                  turnNumber: 2,
                  sessionMode: 'ProviderManagedSession',
                  contextStrategy: 'DeltaContext',
                  newInputSummary: '[call_123]\\n{"status":"partial"}',
                }),
                outputSummary: JSON.stringify({
                  outputSample: '{"summary":"ok","comments":[]}',
                  continuationHandle: 'resp_123',
                }),
              }),
            ],
            finalSummary: null,
            finalComments: null,
          }],
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    await openTraceEventModal(wrapper)

    expect(wrapper.text()).toContain('Provider-managed session: this input/output panel shows only the local delta sent for this turn.')
    expect(wrapper.text()).toContain('Token counts may be higher because the provider accounts for the full continued conversation it retained server-side.')
  })

  it('renders tool-call trace rows nested under the preceding ai_call_iter turn', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            events: [
              makeProtocolEvent({
                id: 'event-ai-1',
                kind: 'aiCall',
                name: 'ai_call_iter_1',
                inputTextSample: '[user]\nReview src/foo.ts',
                outputSummary: '[tool calls: get_file_content, search_repo]',
              }),
              makeProtocolEvent({
                id: 'event-tool-1',
                kind: 'toolCall',
                name: 'get_file_content',
                inputTextSample: '{"path":"src/foo.ts"}',
                outputSummary: '{"content":"..."}',
              }),
              makeProtocolEvent({
                id: 'event-tool-2',
                kind: 'toolCall',
                name: 'search_repo',
                inputTextSample: '{"query":"foo"}',
                outputSummary: '{"matches":[]}',
              }),
              makeProtocolEvent({
                id: 'event-session-1',
                kind: 'operational',
                name: 'review_agent_session_turn',
                inputTextSample: JSON.stringify({ turnNumber: 1, sessionMode: 'ProviderManagedSession' }),
                outputSummary: JSON.stringify({ continuationHandle: 'resp_123' }),
              }),
            ],
            finalSummary: null,
            finalComments: null,
          }],
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    const rows = wrapper.findAll('article.event-card.row-clickable')
    expect(rows).toHaveLength(4)

    expect(rows[0].attributes('data-event-name')).toBe('ai_call_iter_1')
    expect(rows[0].attributes('data-event-depth')).toBe('0')
    expect(rows[0].text()).toContain('2')
    expect(rows[0].find('.event-toggle').exists()).toBe(true)
    expect(rows[0].find('.event-toggle-chevron.fi').exists()).toBe(true)

    expect(rows[1].attributes('data-event-name')).toBe('get_file_content')
    expect(rows[1].attributes('data-event-depth')).toBe('1')
    expect(rows[1].attributes('data-parent-event-id')).toBe('event-ai-1')
    expect(rows[1].classes()).toContain('row-child')
    expect(rows[1].text()).toContain('AI 1')
    expect(rows[1].find('.event-child-rail').exists()).toBe(true)

    expect(rows[2].attributes('data-event-name')).toBe('search_repo')
    expect(rows[2].attributes('data-event-depth')).toBe('1')
    expect(rows[2].attributes('data-parent-event-id')).toBe('event-ai-1')
    expect(rows[2].classes()).toContain('row-child')
    expect(rows[2].text()).toContain('AI 1')

    expect(rows[3].attributes('data-event-name')).toBe('review_agent_session_turn')
    expect(rows[3].attributes('data-event-depth')).toBe('0')
  })

  it('keeps tool rows visibly subordinate when they appear between ai iterations', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            events: [
              makeProtocolEvent({
                id: 'event-ai-1',
                kind: 'aiCall',
                name: 'ai_call_iter_1',
                occurredAt: '2024-01-01T00:00:01Z',
                outputSummary: '[tool calls: get_file_content, search_repo]',
              }),
              makeProtocolEvent({
                id: 'event-tool-1',
                kind: 'toolCall',
                name: 'get_file_content',
                occurredAt: '2024-01-01T00:00:02Z',
              }),
              makeProtocolEvent({
                id: 'event-tool-2',
                kind: 'toolCall',
                name: 'search_repo',
                occurredAt: '2024-01-01T00:00:03Z',
              }),
              makeProtocolEvent({
                id: 'event-ai-2',
                kind: 'aiCall',
                name: 'ai_call_iter_2',
                occurredAt: '2024-01-01T00:00:04Z',
                outputSummary: '{"summary":"done","comments":[]}',
              }),
            ],
            finalSummary: null,
            finalComments: null,
          }],
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    const rows = wrapper.findAll('article.event-card.row-clickable')
    expect(rows).toHaveLength(4)
    expect(rows[1].text()).toContain('AI 1')
    expect(rows[2].text()).toContain('AI 1')
    expect(rows[3].text()).not.toContain('AI 1')
  })

  it('attaches leading tool-call bursts to the following ai iteration when the AI row is recorded later', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            events: [
              makeProtocolEvent({
                id: 'event-tool-1',
                kind: 'toolCall',
                name: 'get_file_content',
                occurredAt: '2024-01-01T00:00:01Z',
              }),
              makeProtocolEvent({
                id: 'event-tool-2',
                kind: 'toolCall',
                name: 'search_source_repo',
                occurredAt: '2024-01-01T00:00:02Z',
              }),
              makeProtocolEvent({
                id: 'event-ai-1',
                kind: 'aiCall',
                name: 'ai_call_iter_1',
                occurredAt: '2024-01-01T00:00:03Z',
                outputSummary: '{"summary":"done","comments":[]}',
              }),
              makeProtocolEvent({
                id: 'event-op-1',
                kind: 'operational',
                name: 'review_agent_session_turn',
                occurredAt: '2024-01-01T00:00:04Z',
              }),
            ],
            finalSummary: null,
            finalComments: null,
          }],
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    const rows = wrapper.findAll('article.event-card.row-clickable')
    expect(rows).toHaveLength(4)
    expect(rows[0].attributes('data-event-name')).toBe('ai_call_iter_1')
    expect(rows[0].text()).toContain('2')
    expect(rows[1].classes()).toContain('row-child')
    expect(rows[1].text()).toContain('AI 1')
    expect(rows[2].classes()).toContain('row-child')
    expect(rows[2].text()).toContain('AI 1')
  })

  it('collapses and expands child tool rows from the ai parent row toggle', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            events: [
              makeProtocolEvent({ id: 'event-ai-1', kind: 'aiCall', name: 'ai_call_iter_1' }),
              makeProtocolEvent({ id: 'event-tool-1', kind: 'toolCall', name: 'get_file_content' }),
              makeProtocolEvent({ id: 'event-tool-2', kind: 'toolCall', name: 'search_source_repo' }),
            ],
            finalSummary: null,
            finalComments: null,
          }],
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    expect(wrapper.findAll('article.event-card.row-clickable')).toHaveLength(3)

    const toggle = wrapper.find('button.event-toggle')
    expect(toggle.exists()).toBe(true)
    await toggle.trigger('click')
    await flushPromises()
    await new Promise((resolve) => setTimeout(resolve, 600))

    const collapsedRows = wrapper.findAll('article.event-card.row-clickable')
    expect(collapsedRows).toHaveLength(1)
    expect(collapsedRows[0].attributes('data-event-name')).toBe('ai_call_iter_1')

    await wrapper.find('button.event-toggle').trigger('click')
    await flushPromises()
    await new Promise((resolve) => setTimeout(resolve, 600))

    expect(wrapper.findAll('article.event-card.row-clickable')).toHaveLength(3)
  })

  it('shows final file summary and final comments in the selected file view', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            finalSummary: 'Final file summary from completed review.',
            finalComments: [
              makeComment('warning', 'Final file-level finding', 'src/foo.ts', 41),
            ],
            events: [makeProtocolEvent({
              kind: 'toolCall',
              name: 'read_file',
              inputTextSample: 'args={"path":"src/foo.ts"}',
              outputSummary: '{"ok":true}',
            })],
          }],
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    expect(tracesTab).toBeTruthy()
    await tracesTab!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Final File Result')
    expect(wrapper.text()).toContain('Final file summary from completed review.')
    expect(wrapper.text()).toContain('Final file-level finding')
  })

  it('renders agentic file strategy and degraded file outcome details', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            resolvedReviewStrategy: 'agenticFileByFile',
            strategySelectionSource: 'clientDefault',
            fileOutcome: {
              filePath: 'src/foo.ts',
              isComplete: true,
              isFailed: false,
              isExcluded: false,
              isCarriedForward: false,
              exclusionReason: null,
              errorMessage: null,
              isDegraded: true,
            },
            events: [
              makeProtocolEvent({
                name: 'review_strategy_selected',
                inputTextSample: JSON.stringify({ strategy: 'agentic_file_by_file' }),
                outputSummary: JSON.stringify({ strategy: 'AgenticFileByFile' }),
              }),
              makeProtocolEvent({
                name: 'agentic_file_plan_created',
                inputTextSample: JSON.stringify({ stage: 'planning', file: 'src/foo.ts' }),
                outputSummary: JSON.stringify({ anchorFilePath: 'src/foo.ts', investigationTasks: [{ taskId: 'task-001' }] }),
              }),
              makeProtocolEvent({
                name: 'agentic_file_degraded',
                inputTextSample: JSON.stringify({ stage: 'investigation', taskId: 'task-001' }),
                outputSummary: JSON.stringify({ reason: 'Tool budget exhausted.' }),
              }),
            ],
          }],
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()

    expect(wrapper.text()).toContain('Strategy')
    expect(wrapper.text()).toContain('Agentic File-by-File')

    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('File Outcome')
    expect(wrapper.text()).toContain('Degraded')
    expect(wrapper.text()).toContain('Agentic file investigation recorded a degraded intermediate outcome for this pass.')
  })

  it('renders authoritative runtime tool-attempt wording for agentic Stage B degraded traces', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            resolvedReviewStrategy: 'agenticFileByFile',
            strategySelectionSource: 'clientDefault',
            fileOutcome: {
              filePath: 'src/foo.ts',
              isComplete: true,
              isFailed: false,
              isExcluded: false,
              isCarriedForward: false,
              exclusionReason: null,
              errorMessage: null,
              isDegraded: true,
            },
            events: [makeProtocolEvent({
              name: 'agentic_file_degraded',
              inputTextSample: JSON.stringify({ stage: 'investigation', taskId: 'task-001' }),
              outputSummary: JSON.stringify({
                Status: 'degraded',
                ToolUsage: [
                  {
                    ToolName: 'get_file_content',
                    Status: 'blocked_scope_violation',
                    Target: 'src/other.ts',
                  },
                  {
                    ToolName: 'get_file_content',
                    Status: 'failed',
                    Target: 'src/foo.ts',
                  },
                ],
                Degraded: true,
                candidateCount: 0,
              }),
            })],
          }],
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    await openTraceEventModal(wrapper)

    expect(wrapper.text()).toContain('Runtime tool attempts:')
    expect(wrapper.text()).toContain('blocked_scope_violation')
    expect(wrapper.text()).toContain('Runtime blocked this attempt because the requested target was outside the approved file scope.')
    expect(wrapper.text()).toContain('Runtime attempted the lookup, but the repository/provider fetch failed.')
    expect(wrapper.text()).toContain('non-validated degraded intermediate outcome')
  })

  it('renders follow-up usage and dependency details for the selected file pass', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            resolvedReviewStrategy: 'agenticFileByFile',
            followUp: {
              used: true,
              triggerFamily: 'dispatch_or_registration',
              completedSuccessfully: true,
              dependencyRecorded: true,
            },
            fileOutcome: {
              filePath: 'src/foo.ts',
              isComplete: true,
              isFailed: false,
              isExcluded: false,
              isCarriedForward: false,
              exclusionReason: null,
              errorMessage: null,
              isDegraded: false,
            },
            events: [],
          }],
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Follow-up')
    expect(wrapper.text()).toContain('Used')
    expect(wrapper.text()).toContain('dispatch_or_registration')
    expect(wrapper.text()).toContain('Completed successfully')
    expect(wrapper.text()).toContain('Dependent finding recorded')
  })

  it('renders repeated-judgment decision details for the selected pass', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            resolvedReviewStrategy: 'agenticFileByFile',
            repeatedJudgment: {
              findingId: 'candidate-001',
              evidenceSetId: 'evidence-task-001',
              agreementState: 'Agreed',
              recommendedDisposition: 'Publish',
              usedSameEvidenceSet: true,
              reasonCodes: ['verified_bounded_claim_support'],
            },
            events: [],
          }],
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Repeated Judgment')
    expect(wrapper.text()).toContain('candidate-001')
    expect(wrapper.text()).toContain('evidence-task-001')
    expect(wrapper.text()).toContain('Agreed')
    expect(wrapper.text()).toContain('Publish')
    expect(wrapper.text()).toContain('verified_bounded_claim_support')
  })

  it('renders ProRV prefilter proof details for the selected pass', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            proRvPrefilter: {
              selected: true,
              executionState: 'completed',
              stageId: 'file-by-file.prorv-prefilter',
              runtimeSource: 'dedicated_runtime',
              modelId: 'gpt-5.4-mini',
              language: 'javascript',
              prefilterStatus: 'Success',
              guidanceCount: 2,
              aiCallRecorded: true,
              guidanceApplied: true,
              appliedPromptKind: 'per_file_review',
              appliedGuidanceIds: ['js/incomplete-sanitization', 'js/path-injection'],
            },
            events: [],
          }],
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('ProRV Prefilter')
    expect(wrapper.text()).toContain('Guidance Applied')
    expect(wrapper.text()).toContain('per_file_review')
    expect(wrapper.text()).toContain('dedicated_runtime')
    expect(wrapper.text()).toContain('gpt-5.4-mini')
    expect(wrapper.text()).toContain('js/incomplete-sanitization')
    expect(wrapper.text()).toContain('js/path-injection')
  })

  it('selects the focused protocol and marks the targeted event row from route query', async () => {
    mockRoute.query = { clientId: 'client-123', protocolId: 'pass-2', eventId: 'event-2' }
    mockGet.mockImplementation((path: string, options?: MockGetOptions) => {
      if (path === '/jobs/{id}/protocol') {
        return Promise.resolve({
          data: [
            { ...sampleProtocols[0], id: 'pass-1', events: [] },
            { ...sampleProtocols[0], id: 'pass-2', label: 'src/focused.ts', events: [] },
          ],
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}/protocol/{protocolId}') {
        return Promise.resolve({
          data: {
            ...sampleProtocols[0],
            id: 'pass-2',
            label: 'src/focused.ts',
            events: [
              makeProtocolEvent({ id: 'event-2', name: 'verification_evidence_collected' }),
            ],
          },
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    const focusedRow = wrapper.find('[data-event-id="event-2"]')
    expect(focusedRow.exists()).toBe(true)
    expect(focusedRow.classes()).toContain('row-focused')
  })

  it('filters execution traces within the opened review and shows suggestion-backed metadata', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: traceReviewWideProtocols,
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    const traceWorkspace = wrapper.find('.trace-workspace')
    expect(traceWorkspace.exists()).toBe(true)
    const globalToolbar = traceWorkspace.find('.trace-filter-toolbar--global')
    expect(globalToolbar.exists()).toBe(true)
    expect(globalToolbar.text()).toContain('Trace Search')
    expect(wrapper.get('[data-testid="trace-findings-only-toggle"]').text()).toContain('Final findings only')
    expect(wrapper.get('[data-testid="trace-search-toggle"]').text()).toContain('Show filters')
    expect(wrapper.find('[data-testid="trace-search-panel"]').isVisible()).toBe(false)

    expect(wrapper.text()).toContain('posting')
    expect(wrapper.text()).toContain('4 visible rows across this review.')
    expect(wrapper.findAll('article.event-card.row-clickable')).toHaveLength(1)

    await openTraceSearchPanel(wrapper)

    const filePathField = wrapper.get('[data-testid="trace-filter-file-path"]')
    const filePathInput = wrapper.get('[data-testid="trace-filter-file-path"] input')
    expect(filePathField.classes()).toContain('v-autocomplete')

    const queryInput = wrapper.get('[data-testid="trace-filter-query"] input')
    expect(wrapper.find('[data-testid="trace-filter-query"] input').exists()).toBe(true)
    await queryInput.setValue('suspicious')
    await flushPromises()

    const trigger = wrapper.find('[data-testid="pass-selector"] .pass-selector-trigger')
    expect(trigger.exists()).toBe(true)
    await trigger.trigger('click')
    await flushPromises()

    const allOptions = document.querySelectorAll('[data-testid="pass-selector"] .pass-selector-option')
    const fooOption = Array.from(allOptions).find((el) => el.textContent?.includes('foo.ts'))
    expect(fooOption).toBeTruthy()

    expect(wrapper.text()).toContain('2 visible rows across this review.')
    expect(wrapper.text()).toContain('Events (2)')
    expect(wrapper.findAll('article.event-card.row-clickable')).toHaveLength(2)
    expect(wrapper.text()).not.toContain('posting duplicate suppression metadata')
    expect(wrapper.text()).toContain('Review src/foo.ts for suspicious auth behavior')
    expect(wrapper.text()).toContain('searching src/foo.ts for suspicious evidence')
    expect(wrapper.text()).toContain('Found suspicious evidence in auth flow [REDACTED]')
    expect(wrapper.text()).toContain('Redacted')
  })

  it('shows an in-place empty state when trace filters remove all rows', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [traceRichProtocol],
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    await openTraceSearchPanel(wrapper)

    const queryInput = wrapper.get('[data-testid="trace-filter-query"] input')
    expect(wrapper.find('[data-testid="trace-filter-query"] input').exists()).toBe(true)
    await queryInput.setValue('no-such-trace')
    await flushPromises()

    expect(wrapper.findAll('article.event-card.row-clickable')).toHaveLength(0)
    expect(wrapper.text()).toContain('No trace rows in this review match the current filters.')
  })

  it('shows explicit limitation messaging when a matching row has sparse metadata and no surrounding context', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [{
            ...sampleProtocols[0],
            id: 'pass-limited',
            label: null,
            modelId: null,
            fileOutcome: null,
            events: [makeProtocolEvent({
              id: 'event-limited',
              kind: 'operational',
              name: 'summary_reconciliation',
              inputTextSample: null,
              outputSummary: null,
              error: null,
            })],
          }],
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    expect(wrapper.text()).toContain('Limited metadata')
    expect(wrapper.text()).toContain('Supporting metadata or nearby trace context was not captured for this row.')
  })

  it('can quickly filter the trace tree to files with final findings only', async () => {
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: [
            {
              ...traceRichProtocol,
              id: 'pass-with-findings',
              label: 'src/with-findings.ts',
              finalComments: [
                makeComment('warning', 'Final finding kept for tree filter', 'src/with-findings.ts', 7),
              ],
            },
            {
              ...sampleProtocols[0],
              id: 'pass-without-findings',
              label: 'src/without-findings.ts',
              finalComments: null,
              events: [
                makeProtocolEvent({
                  id: 'event-no-findings',
                  kind: 'operational',
                  name: 'summary_reconciliation',
                  inputTextSample: 'still visible before toggle',
                  outputSummary: 'no final findings here',
                }),
              ],
            },
          ],
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    const findingsOnlyToggle = wrapper.get('[data-testid="trace-findings-only-toggle"]')
    const triggerBefore = wrapper.find('[data-testid="pass-selector"] .pass-selector-trigger')
    await triggerBefore.trigger('click')
    await flushPromises()
    const passOptionsBefore = Array.from(document.querySelectorAll('[data-testid="pass-selector"] .pass-selector-option')).map((el) => el.textContent ?? '')
    expect(passOptionsBefore.some((text) => text.includes('with-findings.ts'))).toBe(true)
    expect(passOptionsBefore.some((text) => text.includes('without-findings.ts'))).toBe(true)
    expect(findingsOnlyToggle.attributes('aria-pressed')).toBe('false')
    await findingsOnlyToggle.trigger('click')
    await flushPromises()

    const triggerAfter = wrapper.find('[data-testid="pass-selector"] .pass-selector-trigger')
    await triggerAfter.trigger('click')
    await flushPromises()
    const passOptionsAfter = Array.from(document.querySelectorAll('[data-testid="pass-selector"] .pass-selector-option')).map((el) => el.textContent ?? '')
    expect(findingsOnlyToggle.attributes('aria-pressed')).toBe('true')
    expect(passOptionsAfter.some((text) => text.includes('with-findings.ts'))).toBe(true)
    expect(passOptionsAfter.some((text) => text.includes('without-findings.ts'))).toBe(false)
    expect(wrapper.text()).toContain('Final finding kept for tree filter')
  })

  it('updates trace suggestions as filters narrow the visible review-local result space and can reset them', async () => {
    const suggestionProtocols = [
      {
        ...traceRichProtocol,
        id: 'pass-foo',
        label: 'src/foo.ts',
      },
      {
        ...sampleProtocols[0],
        id: 'pass-bar',
        label: 'src/bar.ts',
        modelId: 'gpt-4.1-mini',
        fileOutcome: {
          ...sampleProtocols[0].fileOutcome,
          filePath: 'src/bar.ts',
        },
        events: [
          makeProtocolEvent({
            id: 'event-bar',
            kind: 'operational',
            name: 'summary_reconciliation',
            inputTextSample: 'bar trace payload',
            outputSummary: 'bar trace output',
          }),
        ],
      },
    ]

    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) {
        return Promise.resolve({
          data: suggestionProtocols,
          response: { ok: true },
        })
      }

      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    await openTraceSearchPanel(wrapper)

    const filePathField = wrapper.get('[data-testid="trace-filter-file-path"]')
    const filePathInput = wrapper.get('[data-testid="trace-filter-file-path"] input')
    expect(filePathField.classes()).toContain('v-autocomplete')

    await filePathInput.setValue('src/bar.ts')
    await flushPromises()

    expect((filePathInput.element as HTMLInputElement).value).toBe('src/bar.ts')
    expect(wrapper.text()).toContain('bar trace output')
    expect(wrapper.text()).not.toContain('Found suspicious evidence in auth flow [REDACTED]')

    const clearButton = wrapper.find('button.trace-filter-clear')
    expect(clearButton.exists()).toBe(true)
    await clearButton.trigger('click')
    await flushPromises()

    expect(wrapper.get('[data-testid="trace-search-toggle"]').text()).toContain('Hide filters')
    expect((filePathInput.element as HTMLInputElement).value).toBe('')
  })

  it('ignores unrelated execution-trace query keys when no matching pass or event exists', async () => {
    mockRoute.query = { clientId: 'client-123', protocolId: 'missing-pass', eventId: 'missing-event' }

    const wrapper = await mountView()
    const tracesTab = wrapper.findAll('button.tab-btn').find((btn) => btn.text() === 'Execution Traces')
    await tracesTab!.trigger('click')
    await flushPromises()

    expect(wrapper.find('.error').exists()).toBe(false)
    expect(wrapper.find('[data-event-id="missing-event"]').exists()).toBe(false)
  })
})
