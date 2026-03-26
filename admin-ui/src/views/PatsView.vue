<template>
  <div class="pats-view">
    <div class="pats-toolbar">
      <h2>Personal Access Tokens</h2>
    </div>
    <p class="pats-description">PATs let you authenticate API calls and ADO extension webhooks without your password. Treat them like passwords — they inherit your account permissions.</p>

    <section>
      <h2>Create new token</h2>
      <form @submit.prevent="handleCreate">
        <div v-if="createError" class="error">{{ createError }}</div>
        <div class="form-field">
          <label for="pat-label">Label</label>
          <input
            id="pat-label"
            v-model="newLabel"
            type="text"
            placeholder="e.g. CI pipeline"
          />
        </div>
        <div class="form-field">
          <label for="pat-expires">Expires (optional)</label>
          <input
            id="pat-expires"
            v-model="newExpires"
            type="datetime-local"
          />
        </div>
        <div class="form-actions" style="margin-top: 1rem;">
          <button class="btn-primary" type="submit" :disabled="creating">
            {{ creating ? 'Creating…' : 'Generate token' }}
          </button>
        </div>
      </form>

      <div v-if="generatedToken" class="token-reveal">
        <p><strong>Copy this token now — it will not be shown again.</strong></p>
        <code>{{ generatedToken }}</code>
        <div style="display: flex; gap: 0.5rem; margin-top: 1rem;">
          <button class="btn-primary" @click="copyToken">Copy</button>
          <button class="btn-secondary" @click="generatedToken = ''">Dismiss</button>
        </div>
      </div>
    </section>

    <section>
      <h2>Active tokens</h2>
      <div v-if="loadError" class="error">{{ loadError }}</div>
      <div v-if="loading">Loading…</div>
      <table v-else-if="pats.length">
        <thead>
          <tr>
            <th>Label</th>
            <th>Created</th>
            <th>Last used</th>
            <th>Expires</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="pat in pats" :key="pat.id">
            <td>{{ pat.label }}</td>
            <td>{{ formatDate(pat.createdAt) }}</td>
            <td>{{ pat.lastUsedAt ? formatDate(pat.lastUsedAt) : '—' }}</td>
            <td>{{ pat.expiresAt ? formatDate(pat.expiresAt) : 'Never' }}</td>
            <td>
              <button class="btn-danger" @click="handleRevoke(pat.id)" :disabled="revoking === pat.id">
                {{ revoking === pat.id ? 'Revoking…' : 'Revoke' }}
              </button>
            </td>
          </tr>
        </tbody>
      </table>
      <p v-else>No active tokens.</p>
    </section>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { RouterLink } from 'vue-router'
import { useSession } from '@/composables/useSession'

interface PatItem {
  id: string
  label: string
  createdAt: string
  lastUsedAt: string | null
  expiresAt: string | null
  isRevoked: boolean
}

const { getAccessToken } = useSession()

const pats = ref<PatItem[]>([])
const loading = ref(false)
const loadError = ref('')
const creating = ref(false)
const createError = ref('')
const generatedToken = ref('')
const newLabel = ref('')
const newExpires = ref('')
const revoking = ref<string | null>(null)

const base = import.meta.env.VITE_API_BASE_URL ?? ''

function authHeaders(): Record<string, string> {
  const token = getAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

async function loadPats() {
  loading.value = true
  loadError.value = ''
  try {
    const res = await fetch(`${base}/users/me/pats`, { headers: authHeaders() })
    if (!res.ok) throw new Error(await res.text())
    pats.value = (await res.json()) as PatItem[]
  } catch (e) {
    loadError.value = String(e)
  } finally {
    loading.value = false
  }
}

async function handleCreate() {
  createError.value = ''
  if (!newLabel.value.trim()) {
    createError.value = 'Label is required.'
    return
  }
  creating.value = true
  try {
    const body: Record<string, unknown> = { label: newLabel.value }
    if (newExpires.value) {
      body.expiresAt = new Date(newExpires.value).toISOString()
    }
    const res = await fetch(`${base}/users/me/pats`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify(body),
    })
    if (!res.ok) throw new Error(await res.text())
    const data = (await res.json()) as { token: string }
    generatedToken.value = data.token
    newLabel.value = ''
    newExpires.value = ''
    await loadPats()
  } catch (e) {
    createError.value = String(e)
  } finally {
    creating.value = false
  }
}

async function handleRevoke(id: string) {
  revoking.value = id
  try {
    await fetch(`${base}/users/me/pats/${id}`, {
      method: 'DELETE',
      headers: authHeaders(),
    })
    pats.value = pats.value.filter((p) => p.id !== id)
  } finally {
    revoking.value = null
  }
}

async function copyToken() {
  await navigator.clipboard.writeText(generatedToken.value)
}

function formatDate(iso: string): string {
  return new Date(iso).toLocaleString()
}

onMounted(loadPats)
</script>

<style scoped>

.pats-toolbar {
  display: flex;
  align-items: center;
  gap: 1rem;
  margin-bottom: 0.5rem;
}

.pats-toolbar h2 {
  margin: 0;
}

.pats-description {
  color: var(--color-text-muted);
  font-size: 0.95rem;
  margin-bottom: 1.5rem;
}

.token-reveal {
  background: var(--color-bg);
  border: 1px solid var(--color-success);
  border-radius: 12px;
  padding: 1.5rem;
  margin-top: 1.5rem;
}

.token-reveal code {
  display: block;
  font-family: monospace;
  font-size: 0.95rem;
  word-break: break-all;
  background: var(--color-surface);
  color: var(--color-accent);
  padding: 1rem;
  border-radius: 8px;
  margin: 1rem 0;
}

section {
  margin-bottom: 2rem;
}

section h2 {
  font-size: 1rem;
  margin-bottom: 1rem;
}
</style>
