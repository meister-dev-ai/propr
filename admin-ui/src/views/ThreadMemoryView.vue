<!-- Copyright (c) Andreas Rain. -->
<!-- Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms. -->

<template>
  <div class="page-with-sidebar thread-memory-view">
    <aside class="page-sidebar">
      <div class="detail-page-title" style="margin-bottom: 1.5rem;">
        <h2 class="view-title gradient-text" style="font-size: 1.75rem; margin-bottom: 0;">Memory</h2>
        <p class="detail-page-subtitle" style="margin-top: 0.25rem;">Manage resolved PR comment threads and vector embeddings.</p>
      </div>

      <div class="sidebar-nav">
        <div class="sidebar-nav-group">
          <h4>Memory Data</h4>
          <button class="sidebar-nav-link" :class="{ 'active': activeTab === 'embeddings' }" @click="activeTab = 'embeddings'">
            <i class="fi fi-rr-brain"></i> Stored Embeddings
          </button>
          <button class="sidebar-nav-link" :class="{ 'active': activeTab === 'log' }" @click="activeTab = 'log'">
            <i class="fi fi-rr-list-check"></i> Activity Log
          </button>
        </div>
      </div>
    </aside>

    <main class="page-main-content">
      <div class="main-layout">
      <div class="content-area">
        <!-- Stored Embeddings Tab -->
        <Transition name="fade-slide" mode="out-in">
          <div v-if="activeTab === 'embeddings'" key="embeddings" class="tab-pane">
            <div class="glass-card section-container">
              <div class="section-header">
                <div class="header-main">
                  <div class="client-selector-wrapper">
                    <label class="small-label">Client</label>
                    <select v-model="selectedClientId" class="client-select" @change="loadEmbeddings(1)">
                      <option value="">— Select client —</option>
                      <option v-for="c in clients" :key="c.id" :value="c.id">{{ c.displayName ?? c.name }}</option>
                    </select>
                  </div>
                  <div class="search-wrapper">
                    <label class="small-label">Search</label>
                    <div class="input-with-icon">
                      <i class="fi fi-rr-search"></i>
                      <input
                        v-model="search"
                        class="glass-input search-field"
                        placeholder="Repo, file, or summary..."
                        @input="onSearchInput"
                      />
                    </div>
                  </div>
                </div>
                <div class="header-stats" v-if="selectedClientId && !embeddingsLoading">
                  <div class="stat-pill">
                    <span class="stat-label">Total</span>
                    <span class="stat-value">{{ embeddingsTotal }}</span>
                  </div>
                </div>
              </div>

              <!-- Content States -->
              <div v-if="!selectedClientId" class="empty-state-v2">
                <div class="empty-illustration">
                  <i class="fi fi-rr-search-alt animate-pulse-slow"></i>
                </div>
                <h3>Select a Client</h3>
                <p>Choose a client above to view their stored thread embeddings.</p>
              </div>

              <div v-else-if="embeddingsLoading" class="loading-state-v2">
                <ProgressOrb class="state-orb" />
                <span>Synchronizing vector store...</span>
              </div>

              <div v-else-if="embeddingsError" class="error-state-v2">
                <i class="fi fi-rr-warning error-icon"></i>
                <p>{{ embeddingsError }}</p>
                <button class="btn-primary" @click="loadEmbeddings(1)">
                  <i class="fi fi-rr-refresh"></i> Retry
                </button>
              </div>

              <div v-else-if="!embeddings.length" class="empty-state-v2">
                <i class="fi fi-rr-inbox empty-icon"></i>
                <p>No embeddings found{{ search ? ' for this search' : '' }}.</p>
              </div>

              <template v-else>
                <div class="table-outer">
                  <table class="premium-table">
                    <thead>
                      <tr>
                        <th>Context</th>
                        <th>File & Repository</th>
                        <th>Resolution Summary</th>
                        <th class="text-right">Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      <tr 
                        v-for="r in embeddings" 
                        :key="r.id" 
                        class="hover-row"
                        :class="{ 'row-active': selectedEmbedding?.id === r.id }"
                        @click="selectedEmbedding = r"
                      >
                        <td>
                          <div class="pr-info">
                            <span class="pr-number">#{{ r.pullRequestId }}</span>
                            <span class="thread-id">Thread {{ r.threadId }}</span>
                          </div>
                        </td>
                        <td>
                          <div class="repo-info">
                            <div class="repo-name"><i class="fi fi-rr-folder"></i> {{ r.repositoryId }}</div>
                            <div class="file-path" :title="r.filePath ?? ''">{{ truncate(r.filePath ?? '—', 40) }}</div>
                          </div>
                        </td>
                        <td class="summary-cell-v2">
                          {{ truncate(r.resolutionSummary, 100) }}
                        </td>
                        <td class="text-right">
                          <div class="row-actions">
                            <button class="icon-btn" title="View details" @click.stop="selectedEmbedding = r">
                              <i class="fi fi-rr-eye"></i>
                            </button>
                            <button class="icon-btn delete" title="Delete" @click.stop="confirmDeleteEmbedding(r)">
                              <i class="fi fi-rr-trash"></i>
                            </button>
                          </div>
                        </td>
                      </tr>
                    </tbody>
                  </table>
                </div>

                <!-- Footer / Pagination -->
                <div class="section-footer">
                   <div v-if="embeddingsTotalPages > 1" class="pagination-v2">
                    <button class="page-btn" :disabled="embeddingsPage === 1" @click="loadEmbeddings(embeddingsPage - 1)">
                      <i class="fi fi-rr-angle-small-left"></i>
                    </button>
                    <span class="page-current">{{ embeddingsPage }} / {{ embeddingsTotalPages }}</span>
                    <button class="page-btn" :disabled="embeddingsPage === embeddingsTotalPages" @click="loadEmbeddings(embeddingsPage + 1)">
                      <i class="fi fi-rr-angle-small-right"></i>
                    </button>
                  </div>
                </div>
              </template>
            </div>
          </div>

          <!-- Activity Log Tab -->
          <div v-else key="log" class="tab-pane">
            <div class="glass-card section-container">
              <div class="section-header">
                <div class="header-main">
                   <div class="client-selector-wrapper">
                    <label class="small-label">Client</label>
                    <select v-model="logClientId" class="client-select" @change="loadLog(1)">
                      <option value="">— Select client —</option>
                      <option v-for="c in clients" :key="c.id" :value="c.id">{{ c.displayName ?? c.name }}</option>
                    </select>
                  </div>
                  <div class="filter-wrapper">
                    <label class="small-label">Action</label>
                    <select v-model="logAction" @change="loadLog(1)">
                      <option value="">All actions</option>
                      <option value="0">Stored</option>
                      <option value="1">Removed</option>
                      <option value="2">NoOp</option>
                    </select>
                  </div>
                  <div class="filter-wrapper">
                    <label class="small-label">PR #</label>
                    <input
                      v-model="logPrId"
                      type="number"
                      min="1"
                      placeholder="Any"
                      class="glass-input pr-filter-input"
                      @input="onLogPrInput"
                    />
                  </div>
                </div>
                <div class="header-stats" v-if="logClientId">
                  <div class="header-stats-actions">
                    <button 
                      class="icon-btn refresh-btn" 
                      :class="{ spinning: logLoading }" 
                      title="Refresh log" 
                      @click="loadLog(logPage)"
                      :disabled="logLoading"
                    >
                      <i class="fi fi-rr-refresh"></i>
                    </button>
                    <div class="stat-pill" v-if="!logLoading && logTotal > 0">
                      <span class="stat-label">Total</span>
                      <span class="stat-value">{{ logTotal }}</span>
                    </div>
                  </div>
                </div>
              </div>

              <div v-if="!logClientId" class="empty-state-v2">
                <i class="fi fi-rr-search-alt animate-pulse-slow"></i>
                <h3>Select a Client</h3>
                <p>Select a client to view the activity log for thread processing.</p>
              </div>

              <div v-else-if="logLoading" class="loading-state-v2">
                <ProgressOrb class="state-orb" />
                <span>Fetching log entries...</span>
              </div>

              <div v-else-if="!logEntries.length" class="empty-state-v2">
                <i class="fi fi-rr-calendar-exclamation empty-icon"></i>
                <p>No activity log entries found.</p>
              </div>

              <template v-else>
                <div class="timeline-container">
                  <div class="timeline">
                    <div v-for="e in logEntries" :key="e.id" class="timeline-item">
                      <div class="timeline-dot" :class="{ active: e.action === 0 }"></div>
                      <div class="timeline-content hover-lift">
                        <div class="timeline-header">
                          <span class="time">{{ formatDate(e.occurredAt) }}</span>
                          <span class="chip" :class="actionChipClass(e.action)">{{ actionLabel(e.action) }}</span>
                        </div>
                        <div class="timeline-body">
                          <div class="event-target">
                            PR <span class="bold">#{{ e.pullRequestId }}</span>, Thread <span class="bold">{{ e.threadId }}</span> 
                            via <span class="dim">{{ e.repositoryId }}</span>
                          </div>
                          <div v-if="e.reason" class="event-reason">{{ e.reason }}</div>
                          <div class="event-status-flow" v-if="e.previousStatus || e.currentStatus">
                            <span class="status-tag" :class="'status-' + (e.previousStatus?.toLowerCase() || 'none')">{{ e.previousStatus ?? 'None' }}</span>
                            <i class="fi fi-rr-arrow-small-right"></i>
                            <span class="status-tag" :class="'status-' + e.currentStatus.toLowerCase()">{{ e.currentStatus }}</span>
                          </div>
                        </div>
                      </div>
                    </div>
                  </div>
                </div>

                <div class="section-footer">
                  <div class="pagination-v2">
                    <button class="page-btn" :disabled="logPage === 1" @click="loadLog(logPage - 1)">
                      <i class="fi fi-rr-angle-small-left"></i>
                    </button>
                    <span class="page-current">{{ logPage }} / {{ logTotalPages }}</span>
                    <button class="page-btn" :disabled="logPage === logTotalPages" @click="loadLog(logPage + 1)">
                      <i class="fi fi-rr-angle-small-right"></i>
                    </button>
                  </div>
                </div>
              </template>
            </div>
          </div>
        </Transition>
      </div>

      <!-- Detail Side Panel -->
      <Transition name="slide-panel">
        <div v-if="selectedEmbedding" class="detail-panel glass-card">
          <div class="panel-header">
            <h3>Embedding Details</h3>
            <button class="icon-btn" @click="selectedEmbedding = null"><i class="fi fi-rr-cross"></i></button>
          </div>
          <div class="panel-body">
            <div class="detail-group">
              <label>Thread Info</label>
              <div class="detail-value-box">
                PR #{{ selectedEmbedding.pullRequestId }} — Thread {{ selectedEmbedding.threadId }}
              </div>
            </div>
            
            <div class="detail-group">
              <label>Repository & File</label>
              <div class="detail-value">
                <strong>{{ selectedEmbedding.repositoryId }}</strong>
              </div>
              <div class="detail-code-pill">{{ selectedEmbedding.filePath ?? '—' }}</div>
            </div>

            <div class="detail-group">
              <label>Resolution Summary</label>
              <div class="summary-box glass">
                {{ selectedEmbedding.resolutionSummary }}
              </div>
            </div>

            <div class="detail-group">
              <label>Timestamps</label>
              <div class="timestamp-grid">
                <div>
                  <span class="dim">Created:</span><br/>
                  {{ formatDateShort(selectedEmbedding.createdAt) }}
                </div>
                <div>
                  <span class="dim">Updated:</span><br/>
                  {{ formatDateShort(selectedEmbedding.updatedAt) }}
                </div>
              </div>
            </div>
          </div>
          <div class="panel-footer">
            <button class="btn-secondary" @click="selectedEmbedding = null">Close</button>
            <button class="btn-danger-minimal" @click="confirmDeleteEmbedding(selectedEmbedding)">
              <i class="fi fi-rr-trash"></i> Delete Embedding
            </button>
          </div>
        </div>
      </Transition>
      </div>
    </main>

    <!-- Delete Confirm -->
    <ConfirmDialog
      :open="!!deletingEmbedding"
      :message="`Are you sure you want to delete the embedding for PR #${deletingEmbedding?.pullRequestId}? This will remove it from the vector store until the next crawl cycle.`"
      @confirm="executeDelete"
      @cancel="deletingEmbedding = null"
    />
  </div>
</template>

<script setup lang="ts">
import { ref, computed, onMounted } from 'vue'
import ProgressOrb from '@/components/ProgressOrb.vue'
import ConfirmDialog from '@/components/ConfirmDialog.vue'
import { createAdminClient } from '@/services/api'
import {
  fetchStoredEmbeddings,
  deleteEmbedding,
  fetchActivityLog,
  ACTION_LABELS,
  type ThreadMemoryRecordDto,
  type MemoryActivityLogEntryDto,
} from '@/services/threadMemoryService'
import { useNotification } from '@/composables/useNotification'
import type { components } from '@/services/generated/openapi'

type ClientDto = components['schemas']['ClientDto']

const activeTab = ref<'embeddings' | 'log'>('embeddings')
const clients = ref<ClientDto[]>([])

// Selection
const selectedEmbedding = ref<ThreadMemoryRecordDto | null>(null)

// Embeddings Tab State
const selectedClientId = ref('')
const search = ref('')
const embeddings = ref<ThreadMemoryRecordDto[]>([])
const embeddingsLoading = ref(false)
const embeddingsError = ref('')
const embeddingsPage = ref(1)
const embeddingsTotal = ref(0)
const PAGE_SIZE = 50
const embeddingsTotalPages = computed(() => Math.max(1, Math.ceil(embeddingsTotal.value / PAGE_SIZE)))

let searchTimer: ReturnType<typeof setTimeout> | null = null
function onSearchInput() {
  if (searchTimer) clearTimeout(searchTimer)
  searchTimer = setTimeout(() => loadEmbeddings(1), 400)
}

// Activity Log State
const logClientId = ref('')
const logAction = ref('')
const logPrId = ref('')
const logEntries = ref<MemoryActivityLogEntryDto[]>([])
const logLoading = ref(false)
const logPage = ref(1)
const logTotal = ref(0)
const logTotalPages = computed(() => Math.max(1, Math.ceil(logTotal.value / PAGE_SIZE)))

let logPrTimer: ReturnType<typeof setTimeout> | null = null
function onLogPrInput() {
  if (logPrTimer) clearTimeout(logPrTimer)
  logPrTimer = setTimeout(() => loadLog(1), 400)
}

// Delete
const deletingEmbedding = ref<ThreadMemoryRecordDto | null>(null)

const { notify } = useNotification()

onMounted(async () => {
  try {
    const { data } = await createAdminClient().GET('/clients', {})
    clients.value = (data as ClientDto[]) ?? []
    
    // Auto-select first client for embeddings if available
    if (clients.value.length > 0) {
      selectedClientId.value = clients.value[0].id
      loadEmbeddings(1)
    }
  } catch (e) {
    console.error('Failed to load clients', e)
  }
})

async function loadEmbeddings(page: number) {
  if (!selectedClientId.value) return
  embeddingsLoading.value = true
  embeddingsError.value = ''
  embeddingsPage.value = page
  try {
    const result = await fetchStoredEmbeddings(selectedClientId.value, search.value || undefined, page, PAGE_SIZE)
    embeddings.value = result.items
    embeddingsTotal.value = result.totalCount
  } catch (e) {
    embeddingsError.value = e instanceof Error ? e.message : 'Failed to load embeddings.'
  } finally {
    embeddingsLoading.value = false
  }
}

async function loadLog(page: number) {
  if (!logClientId.value) return
  logLoading.value = true
  logPage.value = page
  try {
    const result = await fetchActivityLog(logClientId.value, {
      action: logAction.value !== '' ? Number(logAction.value) : undefined,
      pullRequestId: logPrId.value !== '' ? Number(logPrId.value) : undefined,
      page,
      pageSize: PAGE_SIZE,
    })
    logEntries.value = result.items
    logTotal.value = result.totalCount
  } catch (e) {
    notify('Failed to load activity log', 'error')
  } finally {
    logLoading.value = false
  }
}

function confirmDeleteEmbedding(r: ThreadMemoryRecordDto) {
  deletingEmbedding.value = r
}

async function executeDelete() {
  const r = deletingEmbedding.value
  if (!r || !selectedClientId.value) return
  deletingEmbedding.value = null
  try {
    await deleteEmbedding(r.id, selectedClientId.value)
    notify('Embedding removed from vector store.', 'success')
    if (selectedEmbedding.value?.id === r.id) selectedEmbedding.value = null
    await loadEmbeddings(embeddingsPage.value)
  } catch (e) {
    notify(e instanceof Error ? e.message : 'Delete failed.', 'error')
  }
}

function actionLabel(action: number): string {
  return ACTION_LABELS[action] ?? String(action)
}

function actionChipClass(action: number): string {
  if (action === 0) return 'chip-success'
  if (action === 1) return 'chip-warning'
  return 'chip-muted'
}

function truncate(text: string, maxLength: number): string {
  return text && text.length > maxLength ? text.slice(0, maxLength) + '…' : text
}

function formatDate(iso: string): string {
  return new Date(iso).toLocaleString([], { dateStyle: 'medium', timeStyle: 'short' })
}

function formatDateShort(iso: string): string {
  return new Date(iso).toLocaleDateString() + ' ' + new Date(iso).toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
}
</script>

<style scoped>
.thread-memory-view {
  height: 100%;
}

.view-header {
  margin-bottom: 2rem;
}

.view-subtitle {
  color: var(--color-text-muted);
  font-size: 0.95rem;
  margin-top: 0.25rem;
}

/* Segmented Control */
.segmented-control {
  display: flex;
  position: relative;
  width: fit-content;
  background: rgba(255, 255, 255, 0.04);
  padding: 4px;
  border-radius: 12px;
  margin-bottom: 2rem;
  border: 1px solid rgba(255, 255, 255, 0.08);
}

.segmented-btn {
  position: relative;
  z-index: 2;
  flex: 1;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  padding: 0.6rem 1.5rem;
  background: transparent;
  border: none;
  border-radius: 9px;
  color: var(--color-text-muted);
  font-weight: 500;
  font-size: 0.9rem;
  cursor: pointer;
  transition: color 0.3s ease;
  min-width: 180px;
  justify-content: center;
}

.segmented-btn.active {
  color: #fff;
}

.segmented-btn i {
  font-size: 1rem;
}

.segmented-slider {
  position: absolute;
  top: 4px;
  bottom: 4px;
  width: calc(50% - 6px);
  background: rgba(255, 255, 255, 0.08);
  border-radius: 8px;
  z-index: 1;
  transition: all 0.3s cubic-bezier(0.4, 0, 0.2, 1);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.1);
}

.main-layout {
  display: flex;
  gap: 2rem;
  align-items: flex-start;
  position: relative;
}

.content-area {
  flex: 1;
  min-width: 0;
}

.section-container {
  min-height: 500px;
  display: flex;
  flex-direction: column;
}

.section-header {
  padding: 1.5rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.05);
  display: flex;
  justify-content: space-between;
  align-items: flex-end;
  gap: 1rem;
}

.header-main {
  display: flex;
  gap: 1.5rem;
  flex: 1;
}

.small-label {
  display: block;
  font-size: 0.75rem;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--color-text-muted);
  margin-bottom: 0.4rem;
  font-weight: 600;
}

.glass-input {
  background: rgba(255, 255, 255, 0.03);
  border: 1px solid rgba(255, 255, 255, 0.08);
  border-radius: 8px;
  padding: 0.6rem 0.8rem;
  color: #fff;
  font-size: 0.9rem;
  transition: all 0.2s;
}

.glass-input:focus {
  border-color: var(--color-accent);
  background: rgba(255, 255, 255, 0.06);
  box-shadow: 0 0 0 2px rgba(34, 211, 238, 0.15);
}

.client-select {
  min-width: 200px;
}

.pr-filter-input {
  width: 90px;
  /* Hide browser number input spinners */
  -moz-appearance: textfield;
}

.pr-filter-input::-webkit-inner-spin-button,
.pr-filter-input::-webkit-outer-spin-button {
  -webkit-appearance: none;
  margin: 0;
}

.search-field {
  width: 100%;
  padding-left: 2.5rem;
}

.input-with-icon {
  position: relative;
  flex: 1;
}

.input-with-icon i {
  position: absolute;
  left: 1rem;
  top: 50%;
  transform: translateY(-50%);
  color: var(--color-text-muted);
  pointer-events: none;
}

.header-stats {
  margin-bottom: 4px;
}

.header-stats-actions {
  display: flex;
  align-items: center;
  gap: 0.75rem;
}

.refresh-btn {
  background: rgba(255, 255, 255, 0.04);
  border: 1px solid rgba(255, 255, 255, 0.08);
}

.refresh-btn.spinning i {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

.stat-pill {
  display: flex;
  align-items: center;
  gap: 0.5rem;
  background: rgba(255, 255, 255, 0.04);
  padding: 0.4rem 0.8rem;
  border-radius: 20px;
  border: 1px solid rgba(255, 255, 255, 0.05);
  font-size: 0.85rem;
}

.stat-label { color: var(--color-text-muted); }
.stat-value { font-weight: 700; color: var(--color-accent); }

/* Table Improvements */
.table-outer {
  flex: 1;
  overflow-x: auto;
}

.premium-table {
  width: 100%;
  border-collapse: collapse;
}

.premium-table th {
  padding: 1rem 1.5rem;
  font-size: 0.75rem;
  font-weight: 600;
  text-transform: uppercase;
  color: var(--color-text-muted);
  border-bottom: 1px solid rgba(255, 255, 255, 0.05);
  white-space: nowrap;
}

.premium-table td {
  padding: 1.25rem 1.5rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.03);
  font-size: 0.95rem;
}

.hover-row {
  transition: background 0.15s ease;
  cursor: pointer;
}

.hover-row:hover {
  background: rgba(255, 255, 255, 0.02);
}

.row-active {
  background: rgba(34, 211, 238, 0.04) !important;
}

.pr-info {
  display: flex;
  flex-direction: column;
}

.pr-number { font-weight: 700; color: #fff; }
.thread-id { font-size: 0.75rem; color: var(--color-text-muted); }

.repo-info {
  display: flex;
  flex-direction: column;
  gap: 0.2rem;
}

.repo-name { font-weight: 500; font-size: 0.9rem; display: flex; align-items: center; gap: 0.4rem; }
.repo-name i { color: var(--color-accent); font-size: 0.8rem; opacity: 0.7; }
.file-path { font-size: 0.8rem; color: var(--color-text-muted); font-family: monospace; }

.summary-cell-v2 {
  color: var(--color-text-muted);
  font-size: 0.9rem;
  line-height: 1.4;
  max-width: 400px;
}

.row-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
}

.icon-btn {
  width: 32px;
  height: 32px;
  border-radius: 8px;
  display: flex;
  align-items: center;
  justify-content: center;
  background: transparent;
  border: 1px solid transparent;
  color: var(--color-text-muted);
  cursor: pointer;
  transition: all 0.2s;
}

.icon-btn:hover {
  background: rgba(255, 255, 255, 0.05);
  color: #fff;
  border-color: rgba(255, 255, 255, 0.1);
}

.icon-btn.delete:hover {
  background: rgba(239, 68, 68, 0.1);
  color: var(--color-danger);
  border-color: rgba(239, 68, 68, 0.2);
}

/* Timeline specific */
.timeline-container {
  padding: 2rem;
}

.timeline-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 0.5rem;
}

.time { font-size: 0.75rem; color: var(--color-text-muted); font-family: monospace; }

.event-target { font-size: 0.9rem; margin-bottom: 0.5rem; }
.event-reason { font-size: 0.85rem; color: var(--color-text-muted); background: rgba(0,0,0,0.2); padding: 0.5rem; border-radius: 6px; margin-bottom: 0.75rem; border: 1px solid rgba(255,255,255,0.03); }
.event-status-flow { display: flex; align-items: center; gap: 0.5rem; font-size: 0.8rem; }
.event-status-flow i { color: var(--color-text-muted); }

.bold { font-weight: 700; color: #fff; }
.dim { color: var(--color-text-muted); opacity: 0.7; }

/* Detail Panel */
.detail-panel {
  width: 400px;
  flex-shrink: 0;
  display: flex;
  flex-direction: column;
  position: sticky;
  top: 0;
  max-height: calc(100vh - 150px);
}

.panel-header {
  padding: 1.5rem;
  border-bottom: 1px solid rgba(255, 255, 255, 0.05);
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.panel-body {
  padding: 1.5rem;
  overflow-y: auto;
  flex: 1;
}

.detail-group { margin-bottom: 1.5rem; }
.detail-group label { display: block; font-size: 0.7rem; text-transform: uppercase; color: var(--color-text-muted); margin-bottom: 0.5rem; font-weight: 600; }
.detail-value { font-size: 1rem; color: #fff; }
.detail-value-box { background: rgba(34, 211, 238, 0.05); padding: 0.75rem; border-radius: 8px; border: 1px solid rgba(34, 211, 238, 0.1); font-weight: 600; }
.detail-code-pill { background: rgba(255, 255, 255, 0.04); padding: 0.3rem 0.6rem; border-radius: 4px; font-family: monospace; font-size: 0.8rem; border: 1px solid rgba(255, 255, 255, 0.05); margin-top: 0.4rem; overflow-wrap: break-word; }

.summary-box {
  padding: 1rem;
  border-radius: 12px;
  font-size: 0.95rem;
  line-height: 1.6;
  color: var(--color-text-muted);
  white-space: pre-wrap;
}

.timestamp-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 1rem;
  font-size: 0.8rem;
}

.panel-footer {
  padding: 1.25rem 1.5rem;
  border-top: 1px solid rgba(255, 255, 255, 0.05);
  display: flex;
  justify-content: space-between;
  gap: 1rem;
}

.btn-danger-minimal {
  padding: 0.6rem 1rem;
  background: transparent;
  border: 1px solid rgba(239, 68, 68, 0.2);
  color: var(--color-danger);
  border-radius: 8px;
  font-size: 0.85rem;
  cursor: pointer;
  display: flex;
  align-items: center;
  gap: 0.5rem;
  transition: all 0.2s;
}

.btn-danger-minimal:hover {
  background: rgba(239, 68, 68, 0.05);
  border-color: var(--color-danger);
}

/* Transition Animations */
.fade-slide-enter-active, .fade-slide-leave-active { transition: all 0.3s ease; }
.fade-slide-enter-from { opacity: 0; transform: translateY(10px); }
.fade-slide-leave-to { opacity: 0; transform: translateY(-10px); }

.slide-panel-enter-active, .slide-panel-leave-active { transition: all 0.4s cubic-bezier(0.16, 1, 0.3, 1); }
.slide-panel-enter-from, .slide-panel-leave-to { transform: translateX(50px); opacity: 0; }

/* Pagination v2 */
.pagination-v2 {
  display: flex;
  align-items: center;
  gap: 1.5rem;
}

.page-btn {
  width: 32px;
  height: 32px;
  border-radius: 50%;
  border: 1px solid rgba(255, 255, 255, 0.08);
  background: rgba(255, 255, 255, 0.03);
  color: #fff;
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  transition: all 0.2s;
}

.page-btn:hover:not(:disabled) { background: rgba(255, 255, 255, 0.1); border-color: rgba(255, 255, 255, 0.2); }
.page-btn:disabled { opacity: 0.3; cursor: not-allowed; }
.page-current { font-size: 0.85rem; color: var(--color-text-muted); font-weight: 500; font-family: monospace; }

.section-footer {
  padding: 1rem 1.5rem;
  display: flex;
  justify-content: center;
  border-top: 1px solid rgba(255, 255, 255, 0.03);
}

/* States v2 */
.empty-state-v2, .loading-state-v2, .error-state-v2 {
  flex: 1;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  padding: 4rem 2rem;
  text-align: center;
  color: var(--color-text-muted);
}

.empty-illustration i { font-size: 3rem; color: var(--color-accent); opacity: 0.2; margin-bottom: 2rem; }
.empty-state-v2 h3 { color: #fff; margin: 0 0 0.5rem; }
.empty-icon, .error-icon { font-size: 2.5rem; color: rgba(255, 255, 255, 0.05); margin-bottom: 1rem; }

.state-orb { transform: scale(1.5); margin-bottom: 2rem; }
</style>
