<template>
    <div class="job-protocol-view">
        <div class="header-stack">
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

            <!-- Master-Detail Layout -->
            <div class="protocol-master-detail">
                <!-- Left Sidebar: Pass Navigation -->
                <nav class="protocol-sidebar" aria-label="Pass Navigation">
                    <button
                        v-for="(pass, index) in protocols"
                        :key="pass.id"
                        class="pass-nav-item"
                        :class="{ 'active': activePassId === pass.id || (!activePassId && index === 0) }"
                        @click="activePassId = pass.id ?? null"
                    >
                        <div class="pass-nav-icon" :class="statusIconClass(pass.outcome)">
                            <template v-if="pass.outcome?.toLowerCase() === 'success' || pass.outcome?.toLowerCase() === 'completed'">✓</template>
                            <template v-else-if="pass.outcome?.toLowerCase() === 'processing'">
                                <ProgressOrb class="sidebar-orb" />
                            </template>
                            <template v-else-if="pass.outcome?.toLowerCase() === 'failed'">✕</template>
                            <template v-else>○</template>
                        </div>
                        <div class="pass-nav-info">
                            <span class="pass-nav-title" :title="pass.label ?? `Pass ${index + 1}`">{{ pass.label ?? `Pass ${index + 1}` }}</span>
                            <span class="pass-nav-meta">
                                <span class="fat-tokens">{{ formatTokens((pass.totalInputTokens ?? 0) + (pass.totalOutputTokens ?? 0)) }} tokens</span>
                            </span>
                        </div>
                    </button>
                </nav>

                <!-- Right Detail: Active Pass -->
                <div class="protocol-content" v-if="activePass">
                    <div class="pass-main">
                        <dl class="summary-grid pass-summary">
                            <div><dt>Started</dt><dd>{{ formatDate(activePass.startedAt) }}</dd></div>
                            <div><dt>Completed</dt><dd>{{ formatDate(activePass.completedAt) }}</dd></div>
                            <div><dt>Outcome</dt><dd>{{ activePass.outcome ?? '—' }}</dd></div>
                            <div><dt>Iterations</dt><dd>{{ activePass.iterationCount ?? '—' }}</dd></div>
                            <div><dt>Tool Calls</dt><dd>{{ activePass.toolCallCount ?? '—' }}</dd></div>
                            <div><dt>Confidence</dt><dd>{{ activePass.finalConfidence != null ? `${activePass.finalConfidence}%` : '—' }}</dd></div>
                            <div><dt>In Tokens</dt><dd class="fat-tokens">{{ formatTokens(activePass.totalInputTokens) }}</dd></div>
                            <div><dt>Out Tokens</dt><dd class="fat-tokens">{{ formatTokens(activePass.totalOutputTokens) }}</dd></div>
                        </dl>

                        <section class="events-section">
                            <h4>Events ({{ activePass.events?.length ? Math.ceil(activePass.events.length / 2) : 0 }})</h4>
                            <p v-if="!activePass.events?.length" class="empty-state">No events recorded.</p>
                            <table v-else class="events-table">
                                <thead>
                                    <tr>
                                        <th>Time</th>
                                        <th>Name</th>
                                        <th>Input Tokens</th>
                                        <th>Output Tokens</th>
                                        <th>Error</th>
                                    </tr>
                                </thead>
                                <TransitionGroup name="list" tag="tbody">
                                    <tr
                                        v-for="merged in processEvents(activePass.events)"
                                        :key="merged.id"
                                        class="row-clickable"
                                        :class="{
                                            'row-error': !!merged.callDetails.error || !!merged.resultDetails?.error,
                                            'row-processing': !merged.resultDetails
                                        }"
                                        @click="openMergedModal(merged)"
                                    >
                                        <td class="date-cell">{{ formatDate(merged.time) }}</td>
                                        <td class="name-cell">
                                            {{ merged.name }}
                                            <span v-if="merged.resultDetails?.outputSummary === null && merged.resultDetails?.error === null" class="status-badge status-processing" style="margin-left: 0.5rem">Executing...</span>
                                        </td>
                                        <td class="tokens-cell fat-tokens">{{ formatTokens(merged.callDetails.inputTokens) }}</td>
                                        <td class="tokens-cell fat-tokens">{{ formatTokens(merged.callDetails.outputTokens) }}</td>
                                        <td class="error-cell">{{ merged.callDetails.error ?? '' }}</td>
                                    </tr>
                                </TransitionGroup>
                            </table>
                        </section>
                    </div>
                </div>
            </div>
            
            <ModalDialog v-model:isOpen="isEventModalOpen" :title="selectedMergedEvent?.name ?? 'Event Protocol'">
                <div v-if="selectedMergedEvent" class="merged-modal-layout">
                    <section class="drawer-section">
                        <h4>Input</h4>
                        <pre v-if="selectedMergedEvent.callDetails.inputTextSample" class="content-block">{{ selectedMergedEvent.callDetails.inputTextSample }}</pre>
                        <p v-else class="no-content">No input captured.</p>
                    </section>
                    
                    <div class="modal-arrow">
                        <svg width="24" height="24" fill="none" viewBox="0 0 24 24" stroke="currentColor" stroke-width="2">
                          <path stroke-linecap="round" stroke-linejoin="round" d="M14 5l7 7m0 0l-7 7m7-7H3" />
                        </svg>
                    </div>

                    <section class="drawer-section">
                        <h4>Output</h4>
                        <template v-if="selectedMergedEvent.resultDetails">
                            <pre v-if="selectedMergedEvent.resultDetails.outputSummary !== null" class="content-block">{{ selectedMergedEvent.resultDetails.outputSummary }}</pre>
                            <template v-else-if="selectedMergedEvent.resultDetails.error !== null">
                                <pre class="content-block" style="color: var(--color-danger);">{{ selectedMergedEvent.resultDetails.error }}</pre>
                            </template>
                            <p v-else-if="selectedMergedEvent.resultDetails.outputSummary === null && selectedMergedEvent.resultDetails.error === null" class="no-content" style="color: var(--color-accent); font-weight: bold;">Currently Executing...</p>
                        </template>
                    </section>
                </div>
            </ModalDialog>
        </template>
    </div>
</template>

<script lang="ts" setup>
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import ModalDialog from '@/components/ModalDialog.vue'
import ProgressOrb from '@/components/ProgressOrb.vue'
import { createAdminClient } from '@/services/api'
import type { components } from '@/services/generated/openapi'

type ReviewJobProtocolDto = components['schemas']['ReviewJobProtocolDto']
type ProtocolEventDto = components['schemas']['ProtocolEventDto']

interface MergedEvent {
    id: string
    time: string
    name: string
    callDetails: ProtocolEventDto
    resultDetails: ProtocolEventDto | null
}

const route = useRoute()
const loading = ref(false)
const error = ref('')
const protocols = ref<ReviewJobProtocolDto[]>([])
const activePassId = ref<string | null>(null)
const selectedMergedEvent = ref<MergedEvent | null>(null)

const activePass = computed(() => {
    if (!protocols.value.length) return null
    return protocols.value.find(p => p.id === activePassId.value) ?? protocols.value[0]
})

const totalInputTokens = computed(() =>
    protocols.value.reduce((sum, p) => sum + (p.totalInputTokens ?? 0), 0),
)
const totalOutputTokens = computed(() =>
    protocols.value.reduce((sum, p) => sum + (p.totalOutputTokens ?? 0), 0),
)

let pollInterval: ReturnType<typeof setInterval> | null = null

async function loadProtocol(showLoading = false) {
    if (showLoading) loading.value = true
    try {
        const jobId = route.params.id as string
        const { data, fetchError } = await createAdminClient().GET('/jobs/{id}/protocol', {
            params: { path: { id: jobId } },
        }) as any
        
        if (fetchError) {
            if (showLoading) error.value = 'Protocol not found for this job.'
        } else if (Array.isArray(data)) {
            protocols.value = data
            if (showLoading && data.length > 0 && data[0].id) {
                activePassId.value = data[0].id
            }
            const isProcessing = data.some(p => !p.completedAt)
            if (isProcessing && !pollInterval) {
                pollInterval = setInterval(() => loadProtocol(false), 3000)
            } else if (!isProcessing && pollInterval) {
                clearInterval(pollInterval)
                pollInterval = null
            }
        }
    } catch {
        if (showLoading) error.value = 'Failed to load protocol.'
    } finally {
        if (showLoading) loading.value = false
    }
}

onMounted(() => {
    loadProtocol(true)
})

onUnmounted(() => {
    if (pollInterval) clearInterval(pollInterval)
})

function processEvents(events: ProtocolEventDto[] | undefined | null): MergedEvent[] {
    if (!events) return []
    return events.map((ev, i) => ({
        id: ev.id ?? String(i),
        time: ev.occurredAt ?? '',
        name: ev.name ?? ev.kind ?? 'Unknown',
        callDetails: ev,
        resultDetails: ev
    }))
}

function statusIconClass(status: string | undefined | null): string {
    switch (status?.toLowerCase()) {
        case 'completed':
        case 'success':
            return 'icon-success'
        case 'processing': return 'icon-processing'
        case 'failed': return 'icon-failed'
        default: return 'icon-pending'
    }
}

const isEventModalOpen = ref(false)

function openMergedModal(event: MergedEvent): void {
    selectedMergedEvent.value = event
    isEventModalOpen.value = true
}

function statusBadgeClass(status: string | undefined | null): string {
    switch (status?.toLowerCase()) {
        case 'completed':
        case 'success':
            return 'status-badge status-completed'
        case 'processing': return 'status-badge status-processing'
        case 'failed': return 'status-badge status-failed'
        default: return 'status-badge status-pending'
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
.toolbar {
    display: flex;
    align-items: center;
    gap: 1rem;
    margin-bottom: 2rem;
}

.toolbar h2 {
    margin: 0;
}

.loading,
.empty-state {
    color: var(--color-text-muted);
    font-style: italic;
}

.error {
    color: var(--color-danger);
}

/* Totals card */
.totals-card {
    margin-bottom: 2rem;
}

/* Animations */
.list-enter-active,
.list-leave-active {
    transition: all 0.5s ease;
}
.list-enter-from,
.list-leave-to {
    opacity: 0;
    transform: translateY(-10px);
}

.status-badge {
    display: inline-block;
    padding: 0.15rem 0.6rem;
    border-radius: 9999px;
    font-size: 0.75rem;
    font-weight: 600;
    text-transform: capitalize;
}

.status-processing {
    background: rgba(34, 211, 238, 0.15);
    color: var(--color-accent);
    animation: flash 2s cubic-bezier(0.4, 0, 0.6, 1) infinite;
}

.row-processing td {
    background: rgba(34, 211, 238, 0.05);
}

@keyframes flash {
    0%, 100% { opacity: 1; }
    50% { opacity: 0.5; }
}

.merged-modal-layout {
    display: flex;
    flex-direction: column;
    gap: 1.5rem;
}

@media (min-width: 768px) {
    .merged-modal-layout {
        flex-direction: row;
        align-items: stretch;
    }
    .merged-modal-layout .drawer-section {
        flex: 1;
        min-width: 0; 
        margin: 0;
    }
}

.modal-arrow {
    display: flex;
    align-items: center;
    justify-content: center;
    color: var(--color-text-muted);
}

@media (max-width: 767px) {
    .modal-arrow {
        transform: rotate(90deg);
    }
}

/* Master-Detail Architecture */
.protocol-master-detail {
    display: grid;
    grid-template-columns: 320px 1fr;
    gap: 2rem;
    align-items: start;
}

@media (max-width: 1024px) {
    .protocol-master-detail {
        grid-template-columns: 1fr;
    }
}

.protocol-sidebar {
    display: flex;
    flex-direction: column;
    gap: 0.5rem;
    position: sticky;
    top: 2rem;
    max-height: calc(100vh - 4rem);
    overflow-y: auto;
}

.pass-nav-item {
    display: flex;
    align-items: flex-start;
    gap: 0.75rem;
    width: 100%;
    text-align: left;
    padding: 1rem;
    background: transparent;
    border: 1px solid var(--color-border);
    border-radius: 12px;
    cursor: pointer;
    transition: all 0.2s cubic-bezier(0.16, 1, 0.3, 1);
    min-width: 0;
    overflow: hidden;
    flex-shrink: 0;
}

.pass-nav-item:hover {
    background: rgba(255, 255, 255, 0.02);
    border-color: rgba(255, 255, 255, 0.1);
}

.pass-nav-item.active {
    background: rgba(59, 130, 246, 0.05);
    border-color: var(--color-accent);
}

.pass-nav-icon {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 24px;
    height: 24px;
    border-radius: 50%;
    font-size: 0.85rem;
    font-weight: bold;
    flex-shrink: 0;
}

.sidebar-orb {
    transform: scale(0.65);
}

.icon-success { color: var(--color-success); }
.icon-failed { color: var(--color-danger); }
.icon-processing { color: var(--color-accent); }
.icon-pending { color: var(--color-text-muted); }

.pass-nav-info {
    display: flex;
    flex-direction: column;
    justify-content: center;
    gap: 0.25rem;
    flex: 1;
    min-width: 0;
}

.pass-nav-title {
    font-weight: 500;
    font-size: 0.95rem;
    color: var(--color-text);
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    display: block;
    width: 100%;
}

.pass-nav-meta {
    font-size: 0.8rem;
    color: var(--color-text-muted);
}

.protocol-content {
    background: var(--color-surface);
    border-radius: 12px;
    padding: 0;
    border: 1px solid var(--color-border);
    overflow: hidden;
    display: flex;
    flex-direction: column;
}

.pass-main {
    flex: 1;
    min-width: 0;
}

/* Summary table shared styles */
.summary-card {
    border: 1px solid var(--color-border);
    border-radius: 12px;
    overflow: hidden;
    background: var(--color-surface);
}

.summary-card h3 {
    margin: 0;
    padding: 1rem 1.5rem;
    background: var(--color-bg);
    border-bottom: 1px solid var(--color-border);
    font-size: 1.1rem;
}

.summary-grid {
    display: grid;
    grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
    gap: 1.5rem;
    margin: 0;
    padding: 1.5rem;
    background: rgba(255, 255, 255, 0.02);
    border-bottom: 1px solid var(--color-border);
}

.summary-grid div {
    display: flex;
    flex-direction: column;
    gap: 0.35rem;
}

.summary-grid dt {
    font-size: 0.8rem;
    color: var(--color-text-muted);
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.05em;
}

.summary-grid dd {
    margin: 0;
    font-size: 1rem;
    color: var(--color-text);
}

.pass-summary {
    margin-bottom: 2rem;
}

.summary-table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.95rem;
}

.summary-table th,
.summary-table td {
    padding: 0.75rem 1.5rem;
    text-align: left;
    border-bottom: 1px solid var(--color-border);
}

.summary-table th {
    width: 12rem;
    color: var(--color-text-muted);
    font-weight: 600;
    background: var(--color-bg);
}

/* Events */
.events-section {
    padding: 1.5rem;
    flex: 1;
    overflow-y: auto;
}

.events-section h4 {
    margin: 0 0 1rem;
    font-size: 1rem;
    color: var(--color-text);
}

.events-table {
    width: 100%;
    border-collapse: collapse;
    font-size: 0.9rem;
}

.events-table th,
.events-table td {
    padding: 0.75rem 1rem;
    text-align: left;
    border-bottom: 1px solid var(--color-border);
}

.events-table th {
    background: var(--color-surface);
    font-weight: 600;
    color: var(--color-text);
}

.row-error td {
    background: rgba(239, 68, 68, 0.1);
}

.row-clickable {
    cursor: pointer;
    transition: background 0.15s;
}

.row-clickable:hover td {
    background: var(--color-border);
}

.row-selected td {
    background: var(--color-border);
    border-left: 2px solid var(--color-accent);
}

.row-selected.row-error td {
    background: rgba(239, 68, 68, 0.2);
}

.date-cell {
    white-space: nowrap;
    color: var(--color-text-muted);
    min-width: 11rem;
}

.kind-cell {
    white-space: nowrap;
    color: var(--color-text-muted);
}

.tokens-cell {
    text-align: right;
    font-family: monospace;
    color: var(--color-text);
}

.error-cell {
    color: var(--color-danger);
    font-size: 0.85rem;
}

/* Side Drawer */
.event-drawer {
    width: 380px;
    flex-shrink: 0;
    border: 1px solid var(--color-border);
    border-radius: 12px;
    background: var(--color-surface);
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
    padding: 1rem 1.5rem;
    background: var(--color-bg);
    border-bottom: 1px solid var(--color-border);
    gap: 0.5rem;
}

.drawer-title {
    font-weight: 600;
    font-size: 1rem;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
}

.drawer-close {
    background: none;
    border: none;
    font-size: 1.2rem;
    cursor: pointer;
    color: var(--color-text-muted);
    flex-shrink: 0;
    padding: 0 0.25rem;
}

.drawer-close:hover {
    color: var(--color-danger);
}

.drawer-body {
    flex: 1;
    overflow-y: auto;
    padding: 1.5rem;
}

.drawer-section {
    margin-bottom: 2rem;
}

.drawer-section h4 {
    margin: 0 0 0.75rem;
    font-size: 0.85rem;
    color: var(--color-text-muted);
    text-transform: uppercase;
    letter-spacing: 0.05em;
}

.content-block {
    background: var(--color-bg);
    border: 1px solid var(--color-border);
    border-radius: 8px;
    padding: 1rem;
    font-size: 0.85rem;
    overflow-x: auto;
    white-space: pre-wrap;
    word-break: break-word;
    max-height: 40vh;
    overflow-y: auto;
    margin: 0;
    color: var(--color-text-muted);
}

.no-content {
    color: var(--color-text-muted);
    font-style: italic;
    font-size: 0.9rem;
    margin: 0;
}
</style>
