// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { defineComponent, h, ref } from 'vue'
import { flushPromises, mount } from '@vue/test-utils'
import { beforeEach, describe, expect, it, vi } from 'vitest'
import type { ProtocolEventDto, ReviewProtocolPass } from '../../types'

const routeQuery = ref<Record<string, string | undefined>>({})
const replaceMock = vi.fn((location: { query?: Record<string, string | undefined> }) => {
    if (location?.query) {
        routeQuery.value = { ...location.query }
    }
    return Promise.resolve()
})

vi.mock('vue-router', async () => {
    const actual = await vi.importActual<typeof import('vue-router')>('vue-router')
    return {
        ...actual,
        useRoute: () => ({
            params: { id: 'job-1' },
            get query() {
                return routeQuery.value
            },
        }),
        useRouter: () => ({
            push: vi.fn(),
            replace: replaceMock,
        }),
        RouterLink: { props: ['to'], template: '<a><slot /></a>' },
    }
})

function event(overrides: Partial<ProtocolEventDto>): ProtocolEventDto {
    return {
        id: overrides.id ?? `${overrides.name}-${Math.random().toString(36).slice(2)}`,
        kind: 'operational',
        name: 'generic_event',
        occurredAt: '2026-06-20T00:00:00Z',
        ...overrides,
    } as ProtocolEventDto
}

function buildProtocols(): ReviewProtocolPass[] {
    return [
        {
            id: 'pass-baseline',
            jobId: 'job-1',
            attemptNumber: 1,
            label: 'src/baseline.ts',
            passKind: 'Baseline',
            startedAt: '2026-06-20T00:00:00Z',
            completedAt: '2026-06-20T00:01:00Z',
            events: [
                event({ id: 'b-gate-drop', name: 'review_finding_gate_decision', outputSummary: '{"disposition":"Drop"}' }),
                event({ id: 'b-gate-publish', name: 'review_finding_gate_decision', outputSummary: '{"disposition":"Publish"}' }),
                event({ id: 'b-discard', name: 'comment_relevance_filter_output', outputSummary: '{"discardedCount":2}' }),
                event({ id: 'b-discard-evaluator', kind: 'aiCall', name: 'ai_call_comment_relevance_evaluator', outputSummary: '{"discardedCount":9}' }),
                event({ id: 'b-memory', kind: 'memoryOperation', name: 'memory_reconsideration_completed' }),
                event({ id: 'b-tool-ok', kind: 'toolCall', name: 'get_changed_files', toolOutcome: 'Completed' }),
            ],
        },
        {
            id: 'pass-file2',
            jobId: 'job-1',
            attemptNumber: 1,
            label: 'src/other.ts',
            passKind: 'Baseline',
            startedAt: '2026-06-20T00:02:00Z',
            completedAt: '2026-06-20T00:03:00Z',
            events: [
                event({ id: 'h-error', kind: 'aiCall', name: 'ai_call_iter_1', error: 'model timeout' }),
                event({ id: 'h-tool-failed', kind: 'toolCall', name: 'read_file', toolOutcome: 'Failed' }),
            ],
        },
    ] as ReviewProtocolPass[]
}

let protocolsFixture: ReviewProtocolPass[] = []

vi.mock('@/services/api', () => ({
    createAdminClient: () => ({
        GET: vi.fn((path: string) => {
            if (path === '/jobs/{id}/protocol') {
                return Promise.resolve({ data: protocolsFixture, error: null })
            }
            if (path === '/jobs/{id}/result') {
                return Promise.resolve({ data: { status: 'completed' }, error: null })
            }
            if (path === '/jobs/{id}') {
                return Promise.resolve({ data: { status: 'completed', tokenBreakdown: [] }, error: null })
            }
            if (path === '/jobs/{id}/protocol/{protocolId}') {
                return Promise.resolve({ data: null, error: null })
            }
            return Promise.resolve({ data: null, error: null })
        }),
    }),
}))

vi.mock('@/services/findingDismissalsService', () => ({ createDismissal: vi.fn() }))
vi.mock('@/services/jobsService', () => ({ restartJob: vi.fn() }))

import { useJobProtocolViewModel } from '../../composables/useJobProtocolViewModel'
import JobProtocolTraceTab from '../JobProtocolTraceTab.vue'

const Host = defineComponent({
    setup(_, { expose }) {
        const vm = useJobProtocolViewModel()
        vm.activeTab = 'traces'
        expose({ vm })
        return () => h(JobProtocolTraceTab, { vm })
    },
})

async function mountTab() {
    const wrapper = mount(Host, {
        global: {
            stubs: {
                RouterLink: { props: ['to'], template: '<a><slot /></a>' },
                Teleport: true,
            },
        },
    })

    // Let onMounted load, the traces-tab watcher run, and reactive recompute settle.
    await flushPromises()
    await flushPromises()
    // Reveal the chip row (the toolbar starts collapsed).
    const vm = (wrapper.vm as unknown as { vm: ReturnType<typeof useJobProtocolViewModel> }).vm
    vm.isTraceSearchCollapsed = false
    await flushPromises()
    return { wrapper, vm }
}

function chip(wrapper: ReturnType<typeof mount>, id: string) {
    return wrapper.find(`[data-testid="trace-chip-${id}"]`)
}

describe('JobProtocolTraceTab quick filter chips', () => {
    beforeEach(() => {
        protocolsFixture = buildProtocols()
        routeQuery.value = {}
        replaceMock.mockClear()
    })

    it('renders the quick-filter chips with live counts against the unfiltered data', async () => {
        const { wrapper } = await mountTab()

        expect(chip(wrapper, 'droppedByGate').find('[data-testid="trace-chip-count"]').text()).toBe('1')
        expect(chip(wrapper, 'commentRelevanceDiscarded').find('[data-testid="trace-chip-count"]').text()).toBe('1')
        expect(chip(wrapper, 'memoryReconsiderations').find('[data-testid="trace-chip-count"]').text()).toBe('1')
        expect(chip(wrapper, 'errorsPresent').find('[data-testid="trace-chip-count"]').text()).toBe('1')
        expect(chip(wrapper, 'toolCallFailed').find('[data-testid="trace-chip-count"]').text()).toBe('1')
    })

    it('activating a chip narrows the visible rows and prunes empty sidebar passes', async () => {
        const { wrapper, vm } = await mountTab()

        await chip(wrapper, 'droppedByGate').trigger('click')
        await flushPromises()

        expect(vm.activeTraceChipIds.has('droppedByGate')).toBe(true)
        // Only the single drop row remains visible across the whole review.
        expect(vm.visibleTraceRows.length).toBe(1)
        expect(vm.visibleTraceRows[0].id).toBe('b-gate-drop')
        // The high-risk pass has no drop rows, so it is pruned from the tree.
        expect(vm.visiblePassCount).toBe(1)
    })

    it('ORs chips within a group and ANDs across groups', async () => {
        const { wrapper, vm } = await mountTab()

        // Two failure-cost chips OR together: error row + failed-tool row = 2 rows.
        await chip(wrapper, 'errorsPresent').trigger('click')
        await chip(wrapper, 'toolCallFailed').trigger('click')
        await flushPromises()
        expect(vm.visibleTraceRows.length).toBe(2)

        // Adding a gate-outcome chip ANDs across groups; no row is both a drop and an error/failed-tool.
        await chip(wrapper, 'droppedByGate').trigger('click')
        await flushPromises()
        expect(vm.visibleTraceRows.length).toBe(0)
    })

    it('encodes active chips into the URL and clears them via Clear filters', async () => {
        const { wrapper, vm } = await mountTab()

        await chip(wrapper, 'errorsPresent').trigger('click')
        await flushPromises()

        expect(replaceMock).toHaveBeenCalled()
        expect(routeQuery.value.traceChips).toBe('errorsPresent')

        vm.clearTraceFilters()
        await flushPromises()
        expect(vm.activeTraceChipIds.size).toBe(0)
        expect(routeQuery.value.traceChips).toBeUndefined()
    })

    it('restores chip state from the URL on mount', async () => {
        routeQuery.value = { traceChips: 'memoryReconsiderations' }
        const { vm } = await mountTab()

        expect(vm.activeTraceChipIds.has('memoryReconsiderations')).toBe(true)
        expect(vm.visibleTraceRows.length).toBe(1)
        expect(vm.visibleTraceRows[0].id).toBe('b-memory')
    })

    it('shows the empty state with a clear action when no rows match', async () => {
        const { wrapper, vm } = await mountTab()

        await chip(wrapper, 'droppedByGate').trigger('click')
        await chip(wrapper, 'errorsPresent').trigger('click')
        await flushPromises()
        expect(vm.visibleTraceRows.length).toBe(0)

        const emptyState = wrapper.find('[data-testid="trace-empty-state"]')
        expect(emptyState.exists()).toBe(true)
        const clearButton = wrapper.find('[data-testid="trace-empty-state-clear"]')
        expect(clearButton.exists()).toBe(true)

        await clearButton.trigger('click')
        await flushPromises()
        expect(vm.activeTraceChipIds.size).toBe(0)
    })

    it('disables a chip with zero matches and ignores clicks on it', async () => {
        // A data set with no gate-drop, comment-relevance, or memory rows leaves
        // those chips at zero and therefore disabled.
        protocolsFixture = [
            {
                id: 'pass-only-tools',
                jobId: 'job-1',
                attemptNumber: 1,
                label: 'src/only.ts',
                passKind: 'Baseline',
                startedAt: '2026-06-20T00:00:00Z',
                completedAt: '2026-06-20T00:01:00Z',
                events: [event({ id: 'ok-tool', kind: 'toolCall', name: 'read_file', toolOutcome: 'Completed' })],
            },
        ] as ReviewProtocolPass[]

        const { wrapper, vm } = await mountTab()

        const dropChip = chip(wrapper, 'droppedByGate')
        expect(dropChip.attributes('disabled')).toBeDefined()
        expect(dropChip.find('[data-testid="trace-chip-count"]').text()).toBe('0')

        await dropChip.trigger('click')
        await flushPromises()
        expect(vm.activeTraceChipIds.has('droppedByGate')).toBe(false)
    })
})
