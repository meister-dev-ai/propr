<template>
    <div class="job-protocol-view">
        <div class="toolbar">
            <RouterLink class="back-link" to="/reviews">← Back to reviews</RouterLink>
            <h2>Job Protocol</h2>
        </div>

        <p v-if="loading" class="loading">Loading…</p>
        <p v-else-if="error" class="error">{{ error }}</p>
        <p v-else-if="protocols.length === 0" class="empty-state">No protocol available for this job.</p>

        <template v-else>
            <!-- Aggregated totals across all passes -->
            <section class="summary-card totals-card">
                <h3>Totals ({{ protocols.length }} pass{{ protocols.length === 1 ? '' : 'es' }})</h3>
                <table class="summary-table">
                    <tbody>
                        <tr>
                            <th>Job ID</th>
                            <td>{{ protocols[0].jobId }}</td>
                        </tr>
                        <tr>
                            <th>Input Tokens</th>
                            <td>{{ formatTokens(totalInputTokens) }}</td>
                        </tr>
                        <tr>
                            <th>Output Tokens</th>
                            <td>{{ formatTokens(totalOutputTokens) }}</td>
                        </tr>
                    </tbody>
                </table>
            </section>

            <!-- One accordion section per protocol pass -->
            <div
                v-for="(pass, index) in protocols"
                :key="pass.id"
                class="pass-card"
            >
                <button
                    type="button"
                    class="pass-header"
                    :class="{ 'pass-header--open': expandedPasses.has(pass.id) }"
                    @click="togglePass(pass.id)"
                >
                    <span class="pass-label">{{ pass.label ?? `Pass ${index + 1}` }}</span>
                    <span class="pass-meta">
                        {{ pass.outcome ?? '…' }}
                        · {{ formatDate(pass.startedAt) }}
                        · {{ formatTokens((pass.totalInputTokens ?? 0) + (pass.totalOutputTokens ?? 0)) }} tokens
                    </span>
                    <span class="pass-chevron">{{ expandedPasses.has(pass.id) ? '▲' : '▼' }}</span>
                </button>

                <div v-if="expandedPasses.has(pass.id)" class="pass-body">
                    <div class="pass-layout" :class="{ 'drawer-open': selectedEvent?.passId === pass.id && selectedEvent !== null }">
                        <div class="pass-main">
                            <table class="summary-table pass-summary">
                                <tbody>
                                    <tr>
                                        <th>Started</th>
                                        <td>{{ formatDate(pass.startedAt) }}</td>
                                    </tr>
                                    <tr>
                                        <th>Completed</th>
                                        <td>{{ formatDate(pass.completedAt) }}</td>
                                    </tr>
                                    <tr>
                                        <th>Outcome</th>
                                        <td>{{ pass.outcome ?? '—' }}</td>
                                    </tr>
                                    <tr>
                                        <th>Iterations</th>
                                        <td>{{ pass.iterationCount ?? '—' }}</td>
                                    </tr>
                                    <tr>
                                        <th>Tool Calls</th>
                                        <td>{{ pass.toolCallCount ?? '—' }}</td>
                                    </tr>
                                    <tr>
                                        <th>Final Confidence</th>
                                        <td>{{ pass.finalConfidence != null ? `${pass.finalConfidence}%` : '—' }}</td>
                                    </tr>
                                    <tr>
                                        <th>Input Tokens</th>
                                        <td>{{ formatTokens(pass.totalInputTokens) }}</td>
                                    </tr>
                                    <tr>
                                        <th>Output Tokens</th>
                                        <td>{{ formatTokens(pass.totalOutputTokens) }}</td>
                                    </tr>
                                </tbody>
                            </table>

                            <section class="events-section">
                                <h4>Events ({{ pass.events?.length ?? 0 }})</h4>
                                <p v-if="!pass.events?.length" class="empty-state">No events recorded.</p>
                                <table v-else class="events-table">
                                    <thead>
                                        <tr>
                                            <th>Time</th>
                                            <th>Kind</th>
                                            <th>Name</th>
                                            <th>Input Tokens</th>
                                            <th>Output Tokens</th>
                                            <th>Error</th>
                                        </tr>
                                    </thead>
                                    <tbody>
                                        <tr
                                            v-for="event in pass.events"
                                            :key="event.id"
                                            :class="{
                                                'row-error': !!event.error,
                                                'row-selected': selectedEvent?.event.id === event.id,
                                                'row-clickable': hasContent(event),
                                            }"
                                            @click="toggleDrawer(pass.id, event)"
                                        >
                                            <td class="date-cell">{{ formatDate(event.occurredAt) }}</td>
                                            <td class="kind-cell">{{ event.kind }}</td>
                                            <td class="name-cell">{{ event.name }}</td>
                                            <td class="tokens-cell">{{ formatTokens(event.inputTokens) }}</td>
                                            <td class="tokens-cell">{{ formatTokens(event.outputTokens) }}</td>
                                            <td class="error-cell">{{ event.error ?? '' }}</td>
                                        </tr>
                                    </tbody>
                                </table>
                            </section>
                        </div>

                        <!-- Side Drawer (scoped per pass) -->
                        <aside
                            v-if="selectedEvent !== null && selectedEvent.passId === pass.id"
                            class="event-drawer"
                        >
                            <div class="drawer-header">
                                <span class="drawer-title">{{ selectedEvent.event.name }}</span>
                                <button class="drawer-close" @click="selectedEvent = null" aria-label="Close">✕</button>
                            </div>
                            <div class="drawer-body">
                                <section class="drawer-section">
                                    <h4>Input</h4>
                                    <pre v-if="selectedEvent.event.inputTextSample" class="content-block">{{ selectedEvent.event.inputTextSample }}</pre>
                                    <p v-else class="no-content">No input captured.</p>
                                </section>
                                <section class="drawer-section">
                                    <h4>Output</h4>
                                    <pre v-if="selectedEvent.event.outputSummary" class="content-block">{{ selectedEvent.event.outputSummary }}</pre>
                                    <p v-else class="no-content">No output captured.</p>
                                </section>
                            </div>
                        </aside>
                    </div>
                </div>
            </div>
        </template>
    </div>
</template>

<script lang="ts" setup>
import { computed, onMounted, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import { createAdminClient } from '@/services/api'
import type { components } from '@/services/generated/openapi'

type ReviewJobProtocolDto = components['schemas']['ReviewJobProtocolDto']
type ProtocolEventDto = components['schemas']['ProtocolEventDto']

interface SelectedEvent {
    passId: string
    event: ProtocolEventDto
}

const route = useRoute()
const loading = ref(false)
const error = ref('')
const protocols = ref<ReviewJobProtocolDto[]>([])
const expandedPasses = ref(new Set<string>())
const selectedEvent = ref<SelectedEvent | null>(null)

const totalInputTokens = computed(() =>
    protocols.value.reduce((sum, p) => sum + (p.totalInputTokens ?? 0), 0),
)
const totalOutputTokens = computed(() =>
    protocols.value.reduce((sum, p) => sum + (p.totalOutputTokens ?? 0), 0),
)

onMounted(async () => {
    const jobId = route.params.id as string
    loading.value = true
    try {
        const { data, error: fetchError } = await createAdminClient().GET('/jobs/{id}/protocol', {
            params: { path: { id: jobId } },
        })
        if (fetchError) {
            error.value = 'Protocol not found for this job.'
        } else if (Array.isArray(data) && data.length > 0) {
            protocols.value = data
            // Expand first pass by default
            if (data[0].id) {
                expandedPasses.value.add(data[0].id)
            }
        }
    } catch {
        error.value = 'Failed to load protocol.'
    } finally {
        loading.value = false
    }
})

function togglePass(passId: string | undefined): void {
    if (!passId) return
    if (expandedPasses.value.has(passId)) {
        expandedPasses.value.delete(passId)
        if (selectedEvent.value?.passId === passId) {
            selectedEvent.value = null
        }
    } else {
        expandedPasses.value.add(passId)
    }
}

function hasContent(event: ProtocolEventDto): boolean {
    return !!(event.inputTextSample || event.outputSummary)
}

function toggleDrawer(passId: string | undefined, event: ProtocolEventDto): void {
    if (!hasContent(event) || !passId) return
    if (selectedEvent.value?.event.id === event.id) {
        selectedEvent.value = null
    } else {
        selectedEvent.value = { passId, event }
    }
}

function formatDate(iso: string | null | undefined): string {
    if (!iso) return '—'
    return new Date(iso).toLocaleString()
}

function formatTokens(n: number | null | undefined): string {
    if (n == null) return '—'
    return n.toLocaleString()
}
</script>

<style scoped>
.job-protocol-view {
    padding: 1rem;
}

.toolbar {
    display: flex;
    align-items: center;
    gap: 1rem;
    margin-bottom: 1.5rem;
}

.toolbar h2 {
    margin: 0;
}

.back-link {
    font-size: 0.9rem;
    color: #555;
    text-decoration: none;
}

.back-link:hover {
    text-decoration: underline;
}

.loading,
.empty-state {
    color: #888;
    font-style: italic;
}

.error {
    color: #c00;
}

/* Totals card */
.totals-card {
    margin-bottom: 1.5rem;
}

/* Per-pass accordion */
.pass-card {
    border: 1px solid #ddd;
    border-radius: 6px;
    overflow: hidden;
    margin-bottom: 1rem;
}

.pass-header {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    width: 100%;
    padding: 0.6rem 1rem;
    background: #f5f5f5;
    color: #333;
    border: none;
    cursor: pointer;
    text-align: left;
    font-size: 0.95rem;
}

.pass-header:hover {
    background: #ececec;
}

.pass-header--open {
    background: #e8f0fe;
    border-bottom: 1px solid #ddd;
}

.pass-label {
    font-weight: 600;
    flex: 1;
    min-width: 0;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.pass-meta {
    font-size: 0.82rem;
    color: #666;
    white-space: nowrap;
}

.pass-chevron {
    font-size: 0.7rem;
    color: #888;
    flex-shrink: 0;
}

.pass-body {
    padding: 1rem;
}

/* Layout with optional side drawer */
.pass-layout {
    display: flex;
    gap: 1.5rem;
    align-items: flex-start;
}

.pass-main {
    flex: 1;
    min-width: 0;
}

/* Summary table shared styles */
.summary-card {
    border: 1px solid #ddd;
    border-radius: 6px;
    overflow: hidden;
}

.summary-card h3 {
    margin: 0;
    padding: 0.6rem 1rem;
    background: #f5f5f5;
    border-bottom: 1px solid #ddd;
    font-size: 1rem;
}

.pass-summary {
    margin-bottom: 1.5rem;
}

.summary-table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.9rem;
}

.summary-table th,
.summary-table td {
    padding: 0.4rem 1rem;
    text-align: left;
    border-bottom: 1px solid #eee;
}

.summary-table th {
    width: 12rem;
    color: #555;
    font-weight: 600;
    background: #fafafa;
}

/* Events */
.events-section {
    margin-bottom: 1rem;
}

.events-section h4 {
    margin: 0 0 0.5rem;
    font-size: 0.95rem;
    color: #444;
}

.events-table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.88rem;
}

.events-table th,
.events-table td {
    padding: 0.4rem 0.75rem;
    text-align: left;
    border-bottom: 1px solid #eee;
}

.events-table th {
    background: #fafafa;
    font-weight: 600;
    color: #444;
}

.row-error td {
    background: #fff5f5;
}

.row-clickable {
    cursor: pointer;
}

.row-clickable:hover td {
    background: #f0f7ff;
}

.row-selected td {
    background: #e6f0ff;
}

.row-selected.row-error td {
    background: #ffe6e6;
}

.date-cell {
    white-space: nowrap;
    color: #555;
    min-width: 11rem;
}

.kind-cell {
    white-space: nowrap;
    color: #777;
}

.tokens-cell {
    text-align: right;
    font-family: monospace;
    color: #333;
}

.error-cell {
    color: #c00;
    font-size: 0.8rem;
}

/* Side Drawer */
.event-drawer {
    width: 380px;
    flex-shrink: 0;
    border: 1px solid #ddd;
    border-radius: 6px;
    background: #fff;
    position: sticky;
    top: 1rem;
    max-height: calc(100vh - 6rem);
    display: flex;
    flex-direction: column;
    overflow: hidden;
}

.drawer-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 0.6rem 1rem;
    background: #f5f5f5;
    border-bottom: 1px solid #ddd;
    gap: 0.5rem;
}

.drawer-title {
    font-weight: 600;
    font-size: 0.9rem;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.drawer-close {
    background: none;
    border: none;
    font-size: 1rem;
    cursor: pointer;
    color: #666;
    flex-shrink: 0;
    padding: 0 0.25rem;
}

.drawer-close:hover {
    color: #000;
}

.drawer-body {
    flex: 1;
    overflow-y: auto;
    padding: 1rem;
}

.drawer-section {
    margin-bottom: 1.5rem;
}

.drawer-section h4 {
    margin: 0 0 0.5rem;
    font-size: 0.85rem;
    color: #555;
    text-transform: uppercase;
    letter-spacing: 0.05em;
}

.content-block {
    background: #f8f8f8;
    border: 1px solid #ddd;
    border-radius: 4px;
    padding: 0.75rem;
    font-size: 0.8rem;
    overflow-x: auto;
    white-space: pre-wrap;
    word-break: break-word;
    max-height: 40vh;
    overflow-y: auto;
    margin: 0;
}

.no-content {
    color: #aaa;
    font-style: italic;
    font-size: 0.85rem;
    margin: 0;
}
</style>
