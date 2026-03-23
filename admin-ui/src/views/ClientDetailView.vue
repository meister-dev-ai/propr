<template>
    <div class="client-detail-view">
        <RouterLink class="back-link" to="/">← Back to clients</RouterLink>
        <p v-if="notFound" class="error">Client not found.</p>
        <p v-else-if="loading" class="loading">Loading…</p>
        <template v-else-if="client">
            <h2>{{ client.displayName }}</h2>

            <section class="edit-section">
                <label for="displayName">Display Name</label>
                <input
                    id="displayName"
                    v-model="editedDisplayName"
                    name="displayName"
                    type="text"
                />
                <button :disabled="saving" class="save-btn" @click="saveDisplayName">Save</button>
                <span v-if="saveError" class="error">{{ saveError }}</span>
            </section>

            <section class="status-section">
                <span>Status: {{ client.isActive ? 'Active' : 'Inactive' }}</span>
                <button :disabled="saving" class="toggle-status-btn" @click="toggleStatus">
                    {{ client.isActive ? 'Disable' : 'Enable' }}
                </button>
            </section>

            <section class="reviewer-section">
                <h3>AI Reviewer Identity</h3>
                <p v-if="client.reviewerId" class="current-reviewer">
                    Current: <code>{{ client.reviewerId }}</code>
                </p>
                <p v-else class="current-reviewer muted">Not configured</p>

                <div class="form-field">
                    <label for="reviewerOrgUrl">ADO Organisation URL</label>
                    <input
                        id="reviewerOrgUrl"
                        v-model="reviewerOrgUrl"
                        name="reviewerOrgUrl"
                        placeholder="https://dev.azure.com/my-org"
                        type="text"
                    />
                </div>
                <div class="form-field">
                    <label for="reviewerDisplayName">Identity Display Name</label>
                    <input
                        id="reviewerDisplayName"
                        v-model="reviewerDisplayName"
                        name="reviewerDisplayName"
                        placeholder="My AI Service Account"
                        type="text"
                    />
                </div>
                <button :disabled="resolving" class="resolve-btn" @click="resolveIdentity">
                    {{ resolving ? 'Resolving…' : 'Resolve' }}
                </button>
                <span v-if="resolveError" class="error">{{ resolveError }}</span>

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

                <button
                    v-if="selectedIdentityId"
                    :disabled="saving"
                    class="save-btn"
                    @click="saveReviewerId"
                >
                    Save Reviewer Identity
                </button>
                <span v-if="reviewerSaveError" class="error">{{ reviewerSaveError }}</span>
                <span v-if="reviewerSaveSuccess" class="success">Reviewer identity saved.</span>
            </section>

            <section class="ado-section">
                <AdoCredentialsForm
                    :clientId="client.id"
                    :hasCredentials="client.hasAdoCredentials"
                    @credentials-updated="client.hasAdoCredentials = true"
                    @credentials-cleared="client.hasAdoCredentials = false"
                />
            </section>

            <section class="reviews-section">
                <h3>Review History</h3>
                <ReviewHistorySection :clientId="client.id" />
            </section>

            <section class="danger-zone">
                <button class="delete-btn btn-danger" @click="showDeleteDialog = true">Delete Client</button>
                <ConfirmDialog
                    :open="showDeleteDialog"
                    message="Delete this client permanently?"
                    @cancel="showDeleteDialog = false"
                    @confirm="handleDelete"
                />
            </section>
        </template>
    </div>
</template>

<script lang="ts" setup>
import {onMounted, ref} from 'vue'
import {RouterLink, useRoute, useRouter} from 'vue-router'
import AdoCredentialsForm from '@/components/AdoCredentialsForm.vue'
import ConfirmDialog from '@/components/ConfirmDialog.vue'
import ReviewHistorySection from '@/components/ReviewHistorySection.vue'
import {createAdminClient} from '@/services/api'

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

// Reviewer identity resolution
const reviewerOrgUrl = ref('')
const reviewerDisplayName = ref('')
const resolving = ref(false)
const resolveError = ref('')
const resolvedIdentities = ref<IdentityMatch[]>([])
const selectedIdentityId = ref<string | null>(null)
const reviewerSaveError = ref('')
const reviewerSaveSuccess = ref(false)

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
</script>

<style scoped>
.reviews-section {
    margin-top: 2rem;
}

.reviews-section h3 {
    margin-bottom: 1rem;
}

.success {
    color: green;
    margin-left: 0.5rem;
}

.muted {
    color: #888;
}

.current-reviewer {
    font-size: 0.9rem;
    margin-bottom: 0.5rem;
}

.form-field {
    display: flex;
    flex-direction: column;
    gap: 0.25rem;
    margin-bottom: 0.5rem;
}

.identity-list {
    list-style: none;
    padding: 0;
    margin: 0.5rem 0;
}

.identity-list li {
    padding: 0.4rem 0.6rem;
    border: 1px solid #ccc;
    border-radius: 4px;
    margin-bottom: 0.25rem;
    cursor: pointer;
    display: flex;
    flex-direction: column;
}

.identity-list li.selected {
    border-color: #007bff;
    background: #e8f0fe;
}

.guid {
    font-size: 0.75rem;
    color: #555;
    font-family: monospace;
}
</style>
