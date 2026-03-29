<template>
    <div class="page-view">
        <RouterLink class="back-link" to="/">
            <i class="fi fi-rr-arrow-left"></i> Back to clients
        </RouterLink>

        <p v-if="notFound" class="error">Client not found.</p>
        <p v-else-if="loading" class="loading">Loading…</p>

        <template v-else-if="client">
            <!-- Page title -->
            <div class="detail-page-title">
                <h2>{{ client.displayName }}</h2>
                <p class="detail-page-subtitle">Client Configuration</p>
            </div>

            <!-- Tabs -->
            <div class="detail-tabs">
                <button
                    :class="['tab-btn', { 'tab-active': activeTab === 'config' }]"
                    @click="activeTab = 'config'"
                >
                    <i class="fi fi-rr-settings"></i> Configuration
                </button>
                <button
                    :class="['tab-btn', { 'tab-active': activeTab === 'ai' }]"
                    @click="activeTab = 'ai'; loadAiConnections()"
                >
                    <i class="fi fi-rr-robot"></i> AI Connections
                </button>
                <button
                    :class="['tab-btn', { 'tab-active': activeTab === 'history' }]"
                    @click="activeTab = 'history'"
                >
                    <i class="fi fi-rr-time-past"></i> Review History
                </button>
                <button
                    :class="['tab-btn', { 'tab-active': activeTab === 'dismissals' }]"
                    @click="activeTab = 'dismissals'; loadDismissals()"
                >
                    <i class="fi fi-rr-ban"></i> Dismissed Findings
                </button>
                <button
                    :class="['tab-btn', { 'tab-active': activeTab === 'prompt-overrides' }]"
                    @click="activeTab = 'prompt-overrides'; loadPromptOverrides()"
                >
                    <i class="fi fi-rr-code-simple"></i> Prompt Overrides
                </button>
            </div>

            <!-- Tab: Configuration -->
            <div v-show="activeTab === 'config'">

                <!-- Section 1: Client Identity -->
                <div class="section-card">
                    <div class="section-card-header">
                        <h3>Client Identity</h3>
                        <div class="section-card-header-actions">
                            <span :class="client.isActive ? 'chip chip-success' : 'chip chip-muted'">
                                <i :class="client.isActive ? 'fi fi-rr-check-circle' : 'fi fi-rr-ban'"></i>
                                {{ client.isActive ? 'Active' : 'Inactive' }}
                            </span>
                            <button
                                :disabled="saving"
                                :class="client.isActive ? 'btn-danger toggle-status-btn' : 'btn-primary toggle-status-btn'"
                                @click="toggleStatus"
                            >
                                {{ client.isActive ? 'Disable' : 'Enable' }}
                            </button>
                        </div>
                    </div>
                    <div class="section-card-body section-card-body--compact">
                        <div class="inline-field-row">
                            <div class="form-field flex-1">
                                <label for="displayName">Display Name</label>
                                <input id="displayName" v-model="editedDisplayName" name="displayName" type="text" />
                            </div>
                            <button :disabled="saving" class="btn-primary inline-save-btn" @click="saveDisplayName">Save</button>
                        </div>
                        <span v-if="saveError" class="error">{{ saveError }}</span>
                    </div>
                </div>

                <!-- Section 2: ADO Credentials (prerequisite) -->
                <div class="section-card">
                    <div class="section-card-header">
                        <h3>ADO Credentials</h3>
                        <span :class="client.hasAdoCredentials ? 'chip chip-success' : 'chip chip-muted'">
                            <i :class="client.hasAdoCredentials ? 'fi fi-rr-plug-connection' : 'fi fi-rr-minus-circle'"></i>
                            {{ client.hasAdoCredentials ? 'Configured' : 'Not configured' }}
                        </span>
                    </div>
                    <div class="section-card-body section-card-body--compact">
                        <AdoCredentialsForm
                            :clientId="client.id"
                            :hasCredentials="client.hasAdoCredentials"
                            @credentials-updated="client.hasAdoCredentials = true"
                            @credentials-cleared="client.hasAdoCredentials = false"
                        />
                    </div>
                </div>

                <!-- Section 3: AI Reviewer Identity (gated on ADO credentials) -->
                <div class="section-card" :class="{ 'section-card--locked': !client.hasAdoCredentials }">
                    <div class="section-card-header">
                        <h3>AI Reviewer Identity</h3>
                        <div class="section-card-header-actions">
                            <template v-if="!client.hasAdoCredentials">
                                <span class="chip chip-muted">
                                    <i class="fi fi-rr-lock"></i> Requires ADO credentials
                                </span>
                            </template>
                            <template v-else>
                                <code v-if="client.reviewerId" class="reviewer-id-pill" :title="client.reviewerId">
                                    {{ client.reviewerId }}
                                </code>
                                <span v-else class="chip chip-muted">
                                    <i class="fi fi-rr-minus-circle"></i> Not configured
                                </span>
                            </template>
                        </div>
                    </div>

                    <!-- Locked notice -->
                    <div v-if="!client.hasAdoCredentials" class="section-locked-notice">
                        <i class="fi fi-rr-lock"></i>
                        <p>Configure ADO Credentials above before setting up the AI Reviewer Identity.</p>
                    </div>

                    <!-- Form (only when ADO is configured) -->
                    <div v-else class="section-card-body section-card-body--compact">
                        <div class="reviewer-fields-grid">
                            <div class="form-field">
                                <label for="reviewerOrgUrl">ADO Organisation URL</label>
                                <input id="reviewerOrgUrl" v-model="reviewerOrgUrl" name="reviewerOrgUrl" placeholder="https://dev.azure.com/my-org" type="text" />
                            </div>
                            <div class="form-field">
                                <label for="reviewerDisplayName">Identity Display Name</label>
                                <input id="reviewerDisplayName" v-model="reviewerDisplayName" name="reviewerDisplayName" placeholder="My AI Service Account" type="text" />
                            </div>
                        </div>
                        <div class="form-actions">
                            <button :disabled="resolving" class="btn-secondary" @click="resolveIdentity">
                                <i class="fi fi-rr-search"></i> {{ resolving ? 'Resolving…' : 'Resolve Identity' }}
                            </button>
                            <span v-if="resolveError" class="error">{{ resolveError }}</span>
                        </div>

                        <ul v-if="resolvedIdentities.length" class="identity-list">
                            <li
                                v-for="identity in resolvedIdentities"
                                :key="identity.id"
                                :class="{ selected: selectedIdentityId === identity.id }"
                                @click="selectedIdentityId = identity.id"
                            >
                                <strong>{{ identity.displayName }}</strong>
                                <span class="guid">{{ identity.id }}</span>
                            </li>
                        </ul>

                        <div v-if="selectedIdentityId" class="form-actions identity-save-actions">
                            <button :disabled="saving" class="btn-primary" @click="saveReviewerId">
                                <i class="fi fi-rr-check"></i> Save Reviewer Identity
                            </button>
                            <span v-if="reviewerSaveError" class="error">{{ reviewerSaveError }}</span>
                            <span v-if="reviewerSaveSuccess" class="success">Reviewer identity saved.</span>
                        </div>
                    </div>
                </div>

                <!-- Danger Zone -->
                <div class="danger-zone-card">
                    <div class="danger-zone-info">
                        <i class="fi fi-rr-triangle-warning"></i>
                        <div>
                            <h3>Danger Zone</h3>
                            <p>Deleting this client is permanent and cannot be undone.</p>
                        </div>
                    </div>
                    <button class="btn-danger" @click="showDeleteDialog = true">
                        <i class="fi fi-rr-trash"></i> Delete Client
                    </button>
                    <ConfirmDialog
                        :open="showDeleteDialog"
                        message="Delete this client permanently?"
                        @cancel="showDeleteDialog = false"
                        @confirm="handleDelete"
                    />
                </div>
            </div>

            <!-- Tab: Review History -->
            <div v-show="activeTab === 'history'">
                <ReviewHistorySection :clientId="client.id" />
            </div>

            <!-- Tab: Dismissed Findings -->
            <div v-show="activeTab === 'dismissals'">
                <div class="section-card">
                    <div class="section-card-header">
                        <h3>Dismissed Findings</h3>
                        <button class="btn-primary btn-sm" @click="showDismissalForm = !showDismissalForm">
                            <i class="fi fi-rr-plus"></i> Add Dismissal
                        </button>
                    </div>

                    <!-- Create form -->
                    <div v-if="showDismissalForm" class="section-card-body">
                        <div class="form-field">
                            <label>Original Message <span class="field-hint-inline">(exact AI finding text)</span></label>
                            <textarea
                                v-model="newDismissal.originalMessage"
                                rows="3"
                                placeholder="Paste the exact finding message to suppress"
                                class="form-input"
                            />
                        </div>
                        <div class="form-field">
                            <label>Label <span class="field-hint-inline">(optional — why it's dismissed)</span></label>
                            <input v-model="newDismissal.label" type="text" placeholder="e.g. False positive: naming style" class="form-input" />
                        </div>
                        <span v-if="dismissalCreateError" class="error">{{ dismissalCreateError }}</span>
                        <div class="form-actions">
                            <button
                                :disabled="dismissalSaving || !newDismissal.originalMessage.trim()"
                                class="btn-primary"
                                @click="handleCreateDismissal"
                            >
                                {{ dismissalSaving ? 'Saving…' : 'Save Dismissal' }}
                            </button>
                            <button class="btn-secondary" @click="showDismissalForm = false">Cancel</button>
                        </div>
                    </div>

                    <div v-if="dismissalsLoading" class="section-card-body">
                        <p class="muted">Loading dismissed findings…</p>
                    </div>
                    <div v-else-if="dismissalsError" class="section-card-body">
                        <p class="error">{{ dismissalsError }}</p>
                    </div>
                    <div v-else-if="dismissals.length === 0 && !dismissalsLoading" class="section-card-body">
                        <p class="muted">No dismissed findings for this client.</p>
                    </div>
                    <div v-else class="section-card-body--compact">
                        <table class="admin-table">
                            <thead>
                                <tr>
                                    <th>Finding Message</th>
                                    <th style="width: 180px">Label</th>
                                    <th style="width: 120px">Created</th>
                                    <th style="width: 80px" class="text-right">Actions</th>
                                </tr>
                            </thead>
                            <tbody>
                                <tr v-for="d in dismissals" :key="d.id">
                                    <td class="dismissal-pattern-cell">
                                        <div class="pattern-text-wrapper" :title="d.patternText">
                                            {{ d.patternText }}
                                        </div>
                                    </td>
                                    <td>
                                        <span v-if="d.label" class="chip chip-muted chip-sm">{{ d.label }}</span>
                                        <span v-else class="muted-hint">No label</span>
                                    </td>
                                    <td class="muted-text small-text">{{ d.createdAt ? new Date(d.createdAt).toLocaleDateString() : '—' }}</td>
                                    <td class="text-right">
                                        <button class="btn-danger btn-xs" title="Delete Dismissal" @click="d.id && handleDeleteDismissal(d.id)">
                                            <i class="fi fi-rr-trash"></i>
                                        </button>
                                    </td>
                                </tr>
                            </tbody>
                        </table>
                    </div>
                </div>
            </div>

            <!-- Tab: Prompt Overrides -->
            <div v-show="activeTab === 'prompt-overrides'">
                <div class="section-card">
                    <div class="section-card-header">
                        <h3>Prompt Overrides</h3>
                        <button class="btn-primary btn-sm" @click="showOverrideForm = !showOverrideForm">
                            <i class="fi fi-rr-plus"></i> Add Override
                        </button>
                    </div>

                    <!-- Create form -->
                    <div v-if="showOverrideForm" class="section-card-body">
                        <div class="form-field">
                            <label>Prompt Key</label>
                            <select v-model="newOverride.promptKey" class="form-input">
                                <option value="">— select —</option>
                                <option value="SystemPrompt">SystemPrompt</option>
                                <option value="AgenticLoopGuidance">AgenticLoopGuidance</option>
                                <option value="SynthesisSystemPrompt">SynthesisSystemPrompt</option>
                                <option value="QualityFilterSystemPrompt">QualityFilterSystemPrompt</option>
                                <option value="PerFileContextPrompt">PerFileContextPrompt</option>
                            </select>
                        </div>
                        <div class="form-field">
                            <label>Override Text <span class="field-hint-inline">(full replacement for the prompt segment)</span></label>
                            <textarea
                                v-model="newOverride.overrideText"
                                rows="6"
                                placeholder="Enter the full replacement prompt text…"
                                class="form-input"
                            />
                        </div>
                        <span v-if="overrideCreateError" class="error">{{ overrideCreateError }}</span>
                        <div class="form-actions">
                            <button
                                :disabled="overrideSaving || !newOverride.promptKey || !newOverride.overrideText.trim()"
                                class="btn-primary"
                                @click="handleCreateOverride"
                            >
                                {{ overrideSaving ? 'Saving…' : 'Save Override' }}
                            </button>
                            <button class="btn-secondary" @click="showOverrideForm = false">Cancel</button>
                        </div>
                    </div>

                    <div v-if="overridesLoading" class="section-card-body">
                        <p class="muted">Loading prompt overrides…</p>
                    </div>
                    <div v-else-if="overridesError" class="section-card-body">
                        <p class="error">{{ overridesError }}</p>
                    </div>
                    <div v-else-if="promptOverrides.length === 0 && !overridesLoading" class="section-card-body">
                        <p class="muted">No prompt overrides configured for this client.</p>
                    </div>
                    <div v-else class="section-card-body--compact">
                        <table class="admin-table">
                            <thead>
                                <tr>
                                    <th style="width: 250px">Prompt Key</th>
                                    <th>Override Text</th>
                                    <th style="width: 80px" class="text-right">Actions</th>
                                </tr>
                            </thead>
                            <tbody>
                                <tr v-for="o in clientScopedOverrides" :key="o.id">
                                    <td class="font-semibold">{{ o.promptKey }}</td>
                                    <td class="dismissal-pattern-cell">
                                        <div class="pattern-text-wrapper" :title="o.overrideText">
                                            {{ o.overrideText }}
                                        </div>
                                    </td>
                                    <td class="text-right">
                                        <button class="btn-danger btn-xs" title="Delete Override" @click="handleDeleteOverride(o.id!)">
                                            <i class="fi fi-rr-trash"></i>
                                        </button>
                                    </td>
                                </tr>
                            </tbody>
                        </table>
                    </div>
                </div>
            </div>

            <!-- Tab: AI Connections -->
            <div v-show="activeTab === 'ai'">
                <div class="section-card">
                    <div class="section-card-header">
                        <h3>AI Connections</h3>
                        <button class="btn-primary btn-sm" @click="showCreateForm = true">
                            <i class="fi fi-rr-plus"></i> Add Connection
                        </button>
                    </div>

                    <!-- Inline create form -->
                    <div v-if="showCreateForm" class="section-card-body ai-create-form">
                        <div class="ai-form-grid">
                            <div class="form-field">
                                <label>Display Name</label>
                                <input v-model="newConn.displayName" type="text" placeholder="e.g. Azure OpenAI (prod)" />
                            </div>
                            <div class="form-field">
                                <label>Endpoint URL</label>
                                <input v-model="newConn.endpointUrl" type="text" placeholder="https://my-resource.openai.azure.com/" />
                            </div>
                            <div class="form-field">
                                <label>API Key</label>
                                <input v-model="newConn.apiKey" type="password" placeholder="Paste your API key" />
                            </div>
                            <div class="form-field">
                                <label>Models <span class="field-hint-inline">(comma-separated deployment names)</span></label>
                                <input v-model="newConn.modelsInput" type="text" placeholder="e.g. gpt-4o, gpt-4o-mini" />
                            </div>
                            <div class="form-field">
                                <label>Model Category <span class="field-hint-inline">(optional — for tier-based routing)</span></label>
                                <select v-model="newConn.modelCategory">
                                    <option value="">Default (no category)</option>
                                    <option value="lowEffort">Low Effort</option>
                                    <option value="mediumEffort">Medium Effort</option>
                                    <option value="highEffort">High Effort</option>
                                </select>
                            </div>
                        </div>

                        <span v-if="createError" class="error">{{ createError }}</span>
                        <div class="form-actions">
                            <button
                                :disabled="aiLoading || !newConn.modelsInput.trim()"
                                class="btn-primary"
                                @click="handleCreateConnection"
                            >
                                {{ aiLoading ? 'Creating…' : 'Save Connection' }}
                            </button>
                            <button class="btn-secondary" @click="cancelCreateForm">Cancel</button>
                        </div>
                    </div>

                    <div v-if="aiLoading && !showCreateForm" class="section-card-body">
                        <p class="muted">Loading connections…</p>
                    </div>
                    <div v-else-if="aiError" class="section-card-body">
                        <p class="error">{{ aiError }}</p>
                    </div>
                    <div v-else-if="aiConnections.length === 0 && !showCreateForm" class="section-card-body">
                        <p class="muted">No AI connections configured for this client.</p>
                    </div>
                    <ul v-else class="ai-connections-list">
                        <li v-for="conn in aiConnections" :key="conn.id" class="ai-connection-item">
                            <div class="ai-conn-main">
                                <div class="ai-conn-header">
                                    <span class="ai-conn-name">{{ conn.displayName }}</span>
                                    <span v-if="conn.isActive" class="chip chip-success chip-sm">
                                        <i class="fi fi-rr-check-circle"></i> Active
                                        <span v-if="conn.activeModel" class="active-model-label">— {{ conn.activeModel }}</span>
                                    </span>
                                    <span v-else class="chip chip-muted chip-sm">Inactive</span>
                                    <span v-if="conn.modelCategory === 'lowEffort'" class="chip chip-muted chip-sm tier-badge">Low Effort</span>
                                    <span v-else-if="conn.modelCategory === 'mediumEffort'" class="chip chip-muted chip-sm tier-badge">Medium Effort</span>
                                    <span v-else-if="conn.modelCategory === 'highEffort'" class="chip chip-accent chip-sm tier-badge">High Effort</span>
                                </div>
                                <div class="ai-conn-meta">
                                    <span class="ai-conn-endpoint">{{ conn.endpointUrl }}</span>
                                </div>
                                <div v-if="conn.models && conn.models.length" class="ai-conn-models">
                                    <span v-for="m in conn.models" :key="m" class="model-chip">{{ m }}</span>
                                </div>
                            </div>
                            <div class="ai-conn-actions">
                                <button v-if="!conn.isActive" class="btn-secondary btn-sm" @click="openActivateModal(conn)">
                                    Activate
                                </button>
                                <template v-else>
                                    <button class="btn-secondary btn-sm" @click="openActivateModal(conn)">
                                        Change Model
                                    </button>
                                    <button class="btn-secondary btn-sm" @click="handleDeactivate(conn.id!)">
                                        Deactivate
                                    </button>
                                </template>
                                <button class="btn-danger btn-sm" @click="handleDeleteConnection(conn.id!)">
                                    <i class="fi fi-rr-trash"></i>
                                </button>
                            </div>
                        </li>
                    </ul>
                </div>

                <!-- Activate modal -->
                <div v-if="activateModal.open" class="modal-backdrop" @click.self="activateModal.open = false">
                    <div class="modal-card">
                        <h3>Activate Connection</h3>
                        <p>
                            Select the model to use for <strong>{{ activateModal.conn?.displayName }}</strong>
                            <template v-if="activateModal.conn?.modelCategory">
                                <span class="chip chip-muted chip-sm tier-badge">{{ activateModal.conn.modelCategory === 'lowEffort' ? 'Low Effort' : activateModal.conn.modelCategory === 'mediumEffort' ? 'Medium Effort' : 'High Effort' }}</span>
                            </template>:
                        </p>
                        <select v-model="activateModal.selectedModel" class="modal-select">
                            <option v-for="m in activateModal.conn?.models ?? []" :key="m" :value="m">{{ m }}</option>
                        </select>
                        <div class="form-actions modal-actions">
                            <button :disabled="aiLoading" class="btn-primary" @click="handleActivate">
                                {{ aiLoading ? 'Activating…' : 'Activate' }}
                            </button>
                            <button class="btn-secondary" @click="activateModal.open = false">Cancel</button>
                        </div>
                        <p v-if="activateError" class="error">{{ activateError }}</p>
                    </div>
                </div>
            </div>
        </template>
    </div>
</template>

<script lang="ts" setup>
import {onMounted, ref, reactive, computed} from 'vue'
import {RouterLink, useRoute, useRouter} from 'vue-router'
import AdoCredentialsForm from '@/components/AdoCredentialsForm.vue'
import ConfirmDialog from '@/components/ConfirmDialog.vue'
import ReviewHistorySection from '@/components/ReviewHistorySection.vue'
import {createAdminClient} from '@/services/api'
import {
    listAiConnections,
    createAiConnection,
    activateAiConnection,
    deactivateAiConnection,
    deleteAiConnection,
} from '@/services/aiConnectionsService'
import type { AiConnectionDto } from '@/services/aiConnectionsService'
import {
    listDismissals,
    createDismissal,
    deleteDismissal,
} from '@/services/findingDismissalsService'
import { listOverrides, createOverride, deleteOverride } from '@/services/promptOverridesService'
import type { components } from '@/types'
type FindingDismissalDto = components['schemas']['FindingDismissalDto']
type PromptOverrideDto = components['schemas']['PromptOverrideDto']

interface Client {
    id: string
    displayName: string
    isActive: boolean
    hasAdoCredentials: boolean
    reviewerId?: string | null
    createdAt: string
}

interface IdentityMatch {
    id: string
    displayName: string
}

const router = useRouter()
const route = useRoute()
const clientId = route.params.id as string

const client = ref<Client | null>(null)
const loading = ref(false)
const notFound = ref(false)
const saving = ref(false)
const saveError = ref('')
const showDeleteDialog = ref(false)
const editedDisplayName = ref('')
const activeTab = ref<'config' | 'ai' | 'history' | 'dismissals' | 'prompt-overrides'>('config')

// Reviewer identity resolution
const reviewerOrgUrl = ref('')
const reviewerDisplayName = ref('')
const resolving = ref(false)
const resolveError = ref('')
const resolvedIdentities = ref<IdentityMatch[]>([])
const selectedIdentityId = ref<string | null>(null)
const reviewerSaveError = ref('')
const reviewerSaveSuccess = ref(false)

// AI Connections tab
const aiConnections = ref<AiConnectionDto[]>([])
const aiLoading = ref(false)
const aiError = ref('')
const showCreateForm = ref(false)
const createError = ref('')
const activateError = ref('')

const newConn = reactive({
    displayName: '',
    endpointUrl: '',
    apiKey: '',
    modelsInput: '',
    modelCategory: '' as '' | 'lowEffort' | 'mediumEffort' | 'highEffort',
})

const activateModal = reactive<{
    open: boolean
    conn: AiConnectionDto | null
    selectedModel: string
}>({
    open: false,
    conn: null,
    selectedModel: '',
})

// Dismissed Findings tab
const dismissals = ref<FindingDismissalDto[]>([])
const dismissalsLoading = ref(false)
const dismissalsError = ref('')
const newDismissal = reactive({ originalMessage: '', label: '' })
const dismissalCreateError = ref('')
const dismissalSaving = ref(false)
const showDismissalForm = ref(false)

// Prompt Overrides tab
const promptOverrides = ref<PromptOverrideDto[]>([])
const overridesLoading = ref(false)
const overridesError = ref('')
const showOverrideForm = ref(false)
const overrideSaving = ref(false)
const overrideCreateError = ref('')
const newOverride = reactive({
    scope: 'clientScope' as 'clientScope' | 'crawlConfigScope',
    promptKey: '',
    overrideText: '',
})

const clientScopedOverrides = computed(() =>
    promptOverrides.value.filter(o => o.scope === 'clientScope')
)

onMounted(async () => {
    loading.value = true
    try {
        const {data, response} = await createAdminClient().GET('/clients/{clientId}', {
            params: {path: {clientId}},
        })
        if (response && (response as Response).status === 404) {
            notFound.value = true
            router.push('/')
            return
        }
        client.value = data as Client
        editedDisplayName.value = (data as Client).displayName
    } catch {
        notFound.value = true
        router.push('/')
    } finally {
        loading.value = false
    }
})

async function saveDisplayName() {
    if (!client.value) return
    saving.value = true
    saveError.value = ''
    try {
        const {data} = await createAdminClient().PATCH('/clients/{clientId}', {
            params: {path: {clientId}},
            body: {displayName: editedDisplayName.value},
        })
        client.value = data as Client
    } catch {
        saveError.value = 'Failed to save.'
    } finally {
        saving.value = false
    }
}

async function toggleStatus() {
    if (!client.value) return
    saving.value = true
    try {
        const {data} = await createAdminClient().PATCH('/clients/{clientId}', {
            params: {path: {clientId}},
            body: {isActive: !client.value.isActive},
        })
        client.value = data as Client
    } catch {
        saveError.value = 'Failed to update status.'
    } finally {
        saving.value = false
    }
}

async function resolveIdentity() {
    resolveError.value = ''
    resolvedIdentities.value = []
    selectedIdentityId.value = null

    const orgUrl = reviewerOrgUrl.value.trim()
    const name = reviewerDisplayName.value.trim()

    if (!orgUrl || !name) {
        resolveError.value = 'Both Organisation URL and Display Name are required.'
        return
    }

    resolving.value = true
    try {
        const {data, response} = await createAdminClient().GET('/identities/resolve', {
            params: {query: {orgUrl, displayName: name}},
        })
        if ((response as Response).status === 404) {
            resolveError.value = `No identity found for "${name}".`
            return
        }
        const matches = data as IdentityMatch[]
        resolvedIdentities.value = matches
        if (matches.length === 1) {
            selectedIdentityId.value = matches[0].id
        }
    } catch {
        resolveError.value = 'Failed to resolve identity.'
    } finally {
        resolving.value = false
    }
}

async function saveReviewerId() {
    if (!client.value || !selectedIdentityId.value) return
    saving.value = true
    reviewerSaveError.value = ''
    reviewerSaveSuccess.value = false
    try {
        await createAdminClient().PUT('/clients/{clientId}/reviewer-identity', {
            params: {path: {clientId}},
            body: {reviewerId: selectedIdentityId.value},
        })
        client.value.reviewerId = selectedIdentityId.value
        reviewerSaveSuccess.value = true
        resolvedIdentities.value = []
        selectedIdentityId.value = null
    } catch {
        reviewerSaveError.value = 'Failed to save reviewer identity.'
    } finally {
        saving.value = false
    }
}

async function handleDelete() {
    try {
        await createAdminClient().DELETE('/clients/{clientId}', {
            params: {path: {clientId}},
        })
        router.push('/')
    } catch {
        router.push('/')
    }
}

async function loadAiConnections() {
    if (aiLoading.value) return
    aiLoading.value = true
    aiError.value = ''
    try {
        aiConnections.value = await listAiConnections(clientId)
    } catch {
        aiError.value = 'Failed to load AI connections.'
    } finally {
        aiLoading.value = false
    }
}

async function refreshAiConnections() {
    aiError.value = ''
    try {
        aiConnections.value = await listAiConnections(clientId)
    } catch {
        aiError.value = 'Failed to refresh AI connections.'
    }
}

async function handleCreateConnection() {
    createError.value = ''
    aiLoading.value = true
    try {
        const models = newConn.modelsInput
            .split(',')
            .map(s => s.trim())
            .filter(Boolean)
        const conn = await createAiConnection(clientId, {
            displayName: newConn.displayName,
            endpointUrl: newConn.endpointUrl,
            models,
            apiKey: newConn.apiKey,
            modelCategory: newConn.modelCategory || undefined,
        })
        aiConnections.value.push(conn)
        cancelCreateForm()
    } catch {
        createError.value = 'Failed to create connection.'
    } finally {
        aiLoading.value = false
    }
}

function cancelCreateForm() {
    showCreateForm.value = false
    createError.value = ''
    newConn.displayName = ''
    newConn.endpointUrl = ''
    newConn.apiKey = ''
    newConn.modelsInput = ''
    newConn.modelCategory = ''
}

function openActivateModal(conn: AiConnectionDto) {
    activateModal.conn = conn
    activateModal.selectedModel = (conn.models ?? [])[0] ?? ''
    activateModal.open = true
    activateError.value = ''
}

async function handleActivate() {
    if (!activateModal.conn?.id || !activateModal.selectedModel) return
    aiLoading.value = true
    activateError.value = ''
    try {
        await activateAiConnection(clientId, activateModal.conn.id, activateModal.selectedModel)
        await refreshAiConnections()
        activateModal.open = false
    } catch {
        activateError.value = 'Failed to activate connection.'
    } finally {
        aiLoading.value = false
    }
}

async function handleDeactivate(connectionId: string) {
    aiLoading.value = true
    try {
        await deactivateAiConnection(clientId, connectionId)
        await refreshAiConnections()
    } catch {
        aiError.value = 'Failed to deactivate connection.'
    } finally {
        aiLoading.value = false
    }
}

async function handleDeleteConnection(connectionId: string) {
    aiLoading.value = true
    try {
        await deleteAiConnection(clientId, connectionId)
        aiConnections.value = aiConnections.value.filter(c => c.id !== connectionId)
    } catch {
        aiError.value = 'Failed to delete connection.'
    } finally {
        aiLoading.value = false
    }
}

// ─── Dismissed Findings handlers ─────────────────────────────────────────────

async function loadDismissals() {
    if (dismissalsLoading.value) return
    dismissalsLoading.value = true
    dismissalsError.value = ''
    try {
        dismissals.value = await listDismissals(clientId)
    } catch {
        dismissalsError.value = 'Failed to load dismissed findings.'
    } finally {
        dismissalsLoading.value = false
    }
}

async function handleCreateDismissal() {
    dismissalCreateError.value = ''
    dismissalSaving.value = true
    try {
        const d = await createDismissal(clientId, {
            originalMessage: newDismissal.originalMessage,
            label: newDismissal.label || null,
        })
        dismissals.value.push(d)
        newDismissal.originalMessage = ''
        newDismissal.label = ''
        showDismissalForm.value = false
    } catch {
        dismissalCreateError.value = 'Failed to create dismissal.'
    } finally {
        dismissalSaving.value = false
    }
}

async function handleDeleteDismissal(id: string) {
    try {
        await deleteDismissal(clientId, id)
        dismissals.value = dismissals.value.filter(d => d.id !== id)
    } catch {
        dismissalsError.value = 'Failed to delete dismissed finding.'
    }
}

async function loadPromptOverrides() {
    if (overridesLoading.value) return
    overridesLoading.value = true
    overridesError.value = ''
    try {
        promptOverrides.value = await listOverrides(clientId)
    } catch {
        overridesError.value = 'Failed to load prompt overrides.'
    } finally {
        overridesLoading.value = false
    }
}

async function handleCreateOverride() {
    overrideCreateError.value = ''
    overrideSaving.value = true
    try {
        const o = await createOverride(clientId, {
            scope: 'clientScope',
            promptKey: newOverride.promptKey,
            overrideText: newOverride.overrideText,
        })
        promptOverrides.value.push(o)
        newOverride.promptKey = ''
        newOverride.overrideText = ''
        showOverrideForm.value = false
    } catch {
        overrideCreateError.value = 'Failed to save override. A duplicate scope+key may already exist.'
    } finally {
        overrideSaving.value = false
    }
}

async function handleDeleteOverride(id: string) {
    try {
        await deleteOverride(clientId, id)
        promptOverrides.value = promptOverrides.value.filter(o => o.id !== id)
    } catch {
        overridesError.value = 'Failed to delete override.'
    }
}
</script>

<style scoped>
.success {
    color: var(--color-success);
    margin-left: 0.5rem;
    font-weight: 500;
}

.toggle-status-btn {
    font-size: 0.8rem;
    padding: 0.35rem 0.85rem;
}

/* Compact section body variant */
.section-card-body--compact {
    padding: 1rem 1.25rem;
}

/* Inline field row — input + button on same line */
.inline-field-row {
    display: flex;
    align-items: flex-end;
    gap: 0.75rem;
}
.flex-1 { flex: 1; }
.inline-save-btn {
    flex-shrink: 0;
    align-self: flex-end;
    margin-bottom: 0;
}

/* 2-column grid for reviewer fields */
.reviewer-fields-grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.75rem;
    margin-bottom: 0.75rem;
}

.identity-save-actions {
    margin-top: 1rem;
    border-top: 1px solid var(--color-border);
    padding-top: 1rem;
}

/* Locked section state */
.section-card--locked .section-card-header {
    opacity: 0.65;
}

.section-locked-notice {
    display: flex;
    align-items: center;
    gap: 0.75rem;
    padding: 1rem 1.25rem;
    color: var(--color-text-muted);
    font-size: 0.875rem;
    border-top: 1px solid var(--color-border);
}

.section-locked-notice .fi {
    font-size: 0.9rem;
    flex-shrink: 0;
}

.section-locked-notice p {
    margin: 0;
}

.identity-list {
    list-style: none;
    padding: 0;
    margin: 0.75rem 0 0;
}

.identity-list li {
    padding: 0.6rem 0.875rem;
    border: 1px solid var(--color-border);
    border-radius: 8px;
    margin-bottom: 0.4rem;
    cursor: pointer;
    display: flex;
    flex-direction: column;
    background: var(--color-bg);
    transition: all 0.2s;
}

.identity-list li:hover {
    border-color: var(--color-text-muted);
}

.identity-list li.selected {
    border-color: var(--color-accent);
    background: rgba(34, 211, 238, 0.04);
}

.guid {
    font-size: 0.78rem;
    color: var(--color-text-muted);
    font-family: monospace;
    margin-top: 0.2rem;
}

/* AI Connections tab */
.btn-sm {
    font-size: 0.8rem;
    padding: 0.3rem 0.7rem;
}

.ai-create-form {
    border-top: 1px solid var(--color-border);
    padding: 1rem 1.25rem;
    display: flex;
    flex-direction: column;
    gap: 1rem;
}

.ai-form-grid {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 0.75rem;
}

.ai-form-grid .full-col {
    grid-column: span 2;
}

.field-hint-inline {
    font-weight: normal;
    font-size: 0.78rem;
    color: var(--color-text-muted);
    text-transform: none;
    letter-spacing: 0;
}

.ai-connections-list {
    list-style: none;
    margin: 0;
    padding: 0;
    border-top: 1px solid var(--color-border);
}

.ai-connection-item {
    display: flex;
    align-items: center;
    justify-content: space-between;
    padding: 0.85rem 1.25rem;
    border-bottom: 1px solid var(--color-border);
    gap: 1rem;
}

.ai-connection-item:last-child {
    border-bottom: none;
}

.ai-conn-main {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
}

.ai-conn-header {
    display: flex;
    align-items: center;
    gap: 0.6rem;
    flex-wrap: wrap;
}

.ai-conn-name {
    font-weight: 600;
    font-size: 0.95rem;
}

.chip-sm {
    font-size: 0.72rem;
    padding: 0.15rem 0.5rem;
}

.active-model-label {
    font-weight: normal;
    opacity: 0.8;
}

.ai-conn-meta {
    font-size: 0.78rem;
    color: var(--color-text-muted);
    font-family: monospace;
}

.ai-conn-models {
    display: flex;
    flex-wrap: wrap;
    gap: 0.3rem;
    margin-top: 0.25rem;
}

.model-chip {
    font-size: 0.75rem;
    background: rgba(255, 255, 255, 0.07);
    border: 1px solid var(--color-border);
    border-radius: 4px;
    padding: 0.1rem 0.45rem;
    font-family: monospace;
}

.ai-conn-actions {
    display: flex;
    align-items: center;
    gap: 0.5rem;
    flex-shrink: 0;
}

.muted {
    color: var(--color-text-muted);
    font-style: italic;
    padding: 1rem 1.25rem;
}

/* Modal */
.modal-backdrop {
    position: fixed;
    inset: 0;
    background: rgba(0, 0, 0, 0.55);
    display: flex;
    align-items: center;
    justify-content: center;
    z-index: 1000;
}

.modal-card {
    background: var(--color-surface);
    border: 1px solid var(--color-border);
    border-radius: 12px;
    padding: 1.5rem;
    width: 360px;
    max-width: 90vw;
    display: flex;
    flex-direction: column;
    gap: 0.75rem;
    box-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);
}

.modal-card h3 {
    margin: 0;
    font-size: 1.05rem;
}

.modal-select {
    width: 100%;
    background: var(--color-surface-raised, rgba(255, 255, 255, 0.08));
    border: 1px solid var(--color-border);
    border-radius: 6px;
    padding: 0.6rem 0.8rem;
    padding-right: 2rem;
    color: var(--color-text);
    font-size: 0.9rem;
    appearance: none;
    color-scheme: dark;
    background-image: url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='12' height='12' viewBox='0 0 12 12'%3E%3Cpath fill='%23888' d='M6 8L1 3h10z'/%3E%3C/svg%3E");
    background-repeat: no-repeat;
    background-position: right 0.65rem center;
    cursor: pointer;
}

.modal-select:focus {
    outline: none;
    border-color: var(--color-accent);
    box-shadow: 0 0 0 3px rgba(59, 130, 246, 0.15);
}

.modal-select option {
    background: var(--color-surface, #1a1a2e);
    color: var(--color-text);
}

.modal-actions {
    margin-top: 0.25rem;
}
/* Dismissals Table Layout */
.dismissal-pattern-cell {
    max-width: 0; /* Enable truncation in flex/grid/table context */
}

.pattern-text-wrapper {
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    font-family: inherit;
    font-size: 0.9rem;
    color: var(--color-text);
}

.muted-hint {
    color: var(--color-text-muted);
    font-size: 0.8rem;
    font-style: italic;
}

.text-right { text-align: right; }
.muted-text { color: var(--color-text-muted); }
.small-text { font-size: 0.85rem; }

.admin-table {
    width: 100%;
    border-collapse: collapse;
}

.admin-table th {
    text-align: left;
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.05em;
    color: var(--color-text-muted);
    padding: 0.75rem 1rem;
    border-bottom: 1px solid var(--color-border);
}

.admin-table td {
    padding: 0.875rem 1rem;
    border-bottom: 1px solid var(--color-border);
    vertical-align: middle;
}

.admin-table tr:last-child td {
    border-bottom: none;
}

.admin-table tr:hover td {
    background: rgba(255, 255, 255, 0.02);
}
</style>
