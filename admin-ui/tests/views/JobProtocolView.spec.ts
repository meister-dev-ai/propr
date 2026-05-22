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

vi.mock('@/components/ModalDialog.vue', () => ({
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
    default: vi.fn().mockImplementation(() => ({
      render: (s: string) => `<p>${s}</p>`,
    })),
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

async function mountView() {
  const { default: JobProtocolView } = await import('@/views/JobProtocolView.vue')
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

  const eventRow = wrapper.find('tr.row-clickable')
  expect(eventRow.exists()).toBe(true)
  await eventRow.trigger('click')
  await flushPromises()
}

describe('JobProtocolView — comment search and filter (T042)', () => {
  beforeEach(() => {
    vi.clearAllMocks()
    vi.resetModules()
    mockRoute.params.id = 'job-abc'
    mockRoute.query = {}
    mockGet.mockImplementation((path: string) => {
      if (path.includes('/protocol')) return Promise.resolve({ data: sampleProtocols, response: { ok: true } })
      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
    })
  })

  it('shows general review settings in the summary overview', async () => {
    const wrapper = await mountView()

    expect(wrapper.text()).toContain('Model')
    expect(wrapper.text()).toContain('gpt-4.1')
    expect(wrapper.text()).toContain('Temperature')
    expect(wrapper.text()).toContain('0.35')
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

    mockGet.mockImplementation((path: string, options?: { params?: { path?: { id?: string } } }) => {
      const jobId = options?.params?.path?.id
      if (jobId === 'job-def') {
        if (path.includes('/protocol')) return Promise.resolve({ data: nextJobProtocols, response: { ok: true } })
        if (path === '/jobs/{id}') return Promise.resolve({ data: nextJobDetail, response: { ok: true } })
        return Promise.resolve({ data: nextJobResult, response: { ok: true } })
      }

      if (path.includes('/protocol')) return Promise.resolve({ data: sampleProtocols, response: { ok: true } })
      if (path === '/jobs/{id}') return Promise.resolve({ data: sampleJobDetail, response: { ok: true } })
      return Promise.resolve({ data: sampleJobResult, response: { ok: true } })
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
    expect(mockGet).toHaveBeenCalledWith('/jobs/{id}/protocol', expect.objectContaining({ params: { path: { id: 'job-def' } } }))
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

    const eventRow = wrapper.find('tr.row-clickable')
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
})
