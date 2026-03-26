<template>
  <div class="users-view">
    <div class="users-toolbar">
      <h2>Users</h2>
      <button class="btn-primary" @click="showCreate = !showCreate">
        {{ showCreate ? 'Cancel' : 'New User' }}
      </button>
    </div>

    <!-- Create user form -->
    <section v-if="showCreate" class="card">
      <h3>Create user</h3>
      <div v-if="createError" class="error">{{ createError }}</div>
      <div class="form-field">
        <label for="new-username">Username</label>
        <input id="new-username" v-model="newUsername" type="text" placeholder="Username" />
      </div>
      <div class="form-field">
        <label for="new-password">Password</label>
        <input id="new-password" v-model="newPassword" type="password" placeholder="Min 8 characters" />
      </div>
      <div class="form-field">
        <label for="new-role">Role</label>
        <select id="new-role" v-model="newRole">
          <option value="User">User</option>
          <option value="Admin">Admin</option>
        </select>
      </div>
      <div class="form-actions">
        <button :disabled="creating" @click="handleCreate">{{ creating ? 'Creating…' : 'Create' }}</button>
        <button class="btn-secondary" @click="showCreate = false">Cancel</button>
      </div>
    </section>

    <!-- Users table -->
    <div v-if="loadError" class="error">{{ loadError }}</div>
    <div v-if="loading" class="loading">Loading…</div>

    <table v-else-if="users.length">
      <thead>
        <tr>
          <th>Username</th>
          <th>Role</th>
          <th>Status</th>
          <th>Created</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        <template v-for="user in users" :key="user.id">
          <tr :class="{ 'row-inactive': !user.isActive }">
            <td>{{ user.username }}</td>
            <td>{{ user.globalRole }}</td>
            <td>
              <span :class="user.isActive ? 'badge-active' : 'badge-inactive'">
                {{ user.isActive ? 'Active' : 'Disabled' }}
              </span>
            </td>
            <td>{{ formatDate(user.createdAt) }}</td>
            <td class="actions-cell">
              <button
                v-if="user.isActive"
                class="btn-danger"
                :disabled="disabling === user.id"
                @click="handleDisable(user.id)"
              >
                {{ disabling === user.id ? '…' : 'Disable' }}
              </button>
              <button
                class="btn-secondary"
                @click="toggleAssignments(user.id)"
              >
                {{ expandedUser === user.id ? 'Close' : 'Assignments' }}
              </button>
            </td>
          </tr>
          <!-- Inline assignments panel -->
          <tr v-if="expandedUser === user.id" class="assignments-row">
            <td colspan="5">
              <div class="assignments-panel">
                <div v-if="assignmentsLoading" class="loading">Loading assignments…</div>
                <div v-else>
                  <table v-if="currentAssignments.length" class="assignments-table">
                    <thead>
                      <tr>
                        <th>Client ID</th>
                        <th>Role</th>
                        <th></th>
                      </tr>
                    </thead>
                    <tbody>
                      <tr v-for="a in currentAssignments" :key="a.assignmentId">
                        <td class="mono">{{ a.clientId }}</td>
                        <td>{{ a.role }}</td>
                        <td>
                          <button
                            class="btn-danger btn-sm"
                            :disabled="removingAssignment === a.assignmentId"
                            @click="handleRemoveAssignment(user.id, a.clientId, a.assignmentId)"
                          >
                            {{ removingAssignment === a.assignmentId ? '…' : 'Remove' }}
                          </button>
                        </td>
                      </tr>
                    </tbody>
                  </table>
                  <p v-else class="muted">No client assignments.</p>

                  <!-- Add assignment -->
                  <div class="add-assignment">
                    <h4>Add assignment</h4>
                    <div v-if="assignError" class="error">{{ assignError }}</div>
                    <div class="add-assignment-form">
                      <select v-model="assignClientId" class="assign-select">
                        <option value="" disabled>Select client…</option>
                        <option v-for="c in clients" :key="c.id" :value="c.id">{{ c.displayName }}</option>
                      </select>
                      <select v-model="assignRole" class="assign-select">
                        <option value="ClientAdministrator">ClientAdministrator</option>
                        <option value="ClientUser">ClientUser</option>
                      </select>
                      <button :disabled="assigning || !assignClientId" @click="handleAddAssignment(user.id)">
                        {{ assigning ? '…' : 'Assign' }}
                      </button>
                    </div>
                  </div>
                </div>
              </div>
            </td>
          </tr>
        </template>
      </tbody>
    </table>
    <p v-else class="muted">No users found.</p>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { useSession } from '@/composables/useSession'

interface UserItem {
  id: string
  username: string
  globalRole: string
  isActive: boolean
  createdAt: string
}

interface AssignmentItem {
  assignmentId: string
  clientId: string
  role: string
  assignedAt: string
}

interface ClientItem {
  id: string
  displayName: string
  isActive: boolean
}

const { getAccessToken } = useSession()
const base = import.meta.env.VITE_API_BASE_URL ?? ''

function authHeaders(): Record<string, string> {
  const token = getAccessToken()
  return token ? { Authorization: `Bearer ${token}` } : {}
}

// Users state
const users = ref<UserItem[]>([])
const loading = ref(false)
const loadError = ref('')
const disabling = ref<string | null>(null)

// Create user state
const showCreate = ref(false)
const newUsername = ref('')
const newPassword = ref('')
const newRole = ref<'User' | 'Admin'>('User')
const creating = ref(false)
const createError = ref('')

// Assignments state
const expandedUser = ref<string | null>(null)
const currentAssignments = ref<AssignmentItem[]>([])
const assignmentsLoading = ref(false)
const clients = ref<ClientItem[]>([])
const assignClientId = ref('')
const assignRole = ref<'ClientAdministrator' | 'ClientUser'>('ClientUser')
const assigning = ref(false)
const removingAssignment = ref<string | null>(null)
const assignError = ref('')

async function loadUsers() {
  loading.value = true
  loadError.value = ''
  try {
    const res = await fetch(`${base}/admin/users`, { headers: authHeaders() })
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    users.value = (await res.json()) as UserItem[]
  } catch (e) {
    loadError.value = `Failed to load users: ${String(e)}`
  } finally {
    loading.value = false
  }
}

async function loadClients() {
  try {
    const res = await fetch(`${base}/clients`, { headers: authHeaders() })
    if (res.ok) clients.value = (await res.json()) as ClientItem[]
  } catch {
    // non-critical; assignment form will show empty dropdown
  }
}

async function handleCreate() {
  createError.value = ''
  if (!newUsername.value.trim()) { createError.value = 'Username is required.'; return }
  if (newPassword.value.length < 8) { createError.value = 'Password must be at least 8 characters.'; return }

  creating.value = true
  try {
    const res = await fetch(`${base}/admin/users`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify({ username: newUsername.value, password: newPassword.value, globalRole: newRole.value }),
    })
    if (!res.ok) {
      const body = await res.json().catch(() => ({ error: `HTTP ${res.status}` })) as { error?: string }
      throw new Error(body.error ?? `HTTP ${res.status}`)
    }
    const created = (await res.json()) as UserItem
    users.value.unshift(created)
    newUsername.value = ''
    newPassword.value = ''
    newRole.value = 'User'
    showCreate.value = false
  } catch (e) {
    createError.value = String(e)
  } finally {
    creating.value = false
  }
}

async function handleDisable(id: string) {
  if (!confirm('Disable this user? All their tokens and PATs will be revoked.')) return
  disabling.value = id
  try {
    const res = await fetch(`${base}/admin/users/${id}`, { method: 'DELETE', headers: authHeaders() })
    if (res.ok) {
      const user = users.value.find(u => u.id === id)
      if (user) user.isActive = false
    }
  } finally {
    disabling.value = null
  }
}

async function toggleAssignments(userId: string) {
  if (expandedUser.value === userId) {
    expandedUser.value = null
    return
  }
  expandedUser.value = userId
  currentAssignments.value = []
  assignError.value = ''
  assignmentsLoading.value = true
  try {
    const res = await fetch(`${base}/admin/users/${userId}`, { headers: authHeaders() })
    if (!res.ok) throw new Error(`HTTP ${res.status}`)
    const data = (await res.json()) as { assignments: AssignmentItem[] }
    currentAssignments.value = data.assignments ?? []
  } catch {
    currentAssignments.value = []
  } finally {
    assignmentsLoading.value = false
  }
}

async function handleAddAssignment(userId: string) {
  assignError.value = ''
  if (!assignClientId.value) return
  assigning.value = true
  try {
    const res = await fetch(`${base}/admin/users/${userId}/clients`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...authHeaders() },
      body: JSON.stringify({ clientId: assignClientId.value, role: assignRole.value }),
    })
    if (!res.ok) {
      const body = await res.json().catch(() => ({ error: `HTTP ${res.status}` })) as { error?: string }
      throw new Error(body.error ?? `HTTP ${res.status}`)
    }
    assignClientId.value = ''
    // reload assignments
    await toggleAssignments(userId)
    await toggleAssignments(userId)
  } catch (e) {
    assignError.value = String(e)
  } finally {
    assigning.value = false
  }
}

async function handleRemoveAssignment(userId: string, clientId: string, assignmentId: string) {
  removingAssignment.value = assignmentId
  try {
    await fetch(`${base}/admin/users/${userId}/clients/${clientId}`, {
      method: 'DELETE',
      headers: authHeaders(),
    })
    currentAssignments.value = currentAssignments.value.filter(a => a.assignmentId !== assignmentId)
  } finally {
    removingAssignment.value = null
  }
}

function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString()
}

onMounted(() => {
  loadUsers()
  loadClients()
})
</script>

<style scoped>
.users-toolbar {
  display: flex;
  align-items: center;
  margin-bottom: 2rem;
}

.users-toolbar h2 {
  margin: 0;
  flex: 1;
}

.card {
  background: var(--color-bg);
  border: 1px solid var(--color-border);
  border-radius: 12px;
  padding: 1.5rem 2rem;
  margin-bottom: 1.5rem;
}

.card h3 {
  margin: 0 0 1rem;
  font-size: 1.25rem;
}

select {
  display: block;
  width: 100%;
  padding: 0.75rem 1rem;
  border: 1px solid var(--color-border);
  border-radius: 0.5rem;
  font-size: 1rem;
  background: var(--color-bg);
  color: var(--color-text);
}
select:focus { outline: none; border-color: var(--color-accent); box-shadow: 0 0 0 1px var(--color-accent); }

.btn-secondary {
  background: transparent;
  color: var(--color-text);
  border: 1px solid var(--color-border);
  padding: 0.625rem 1.25rem;
  border-radius: 9999px;
  font-size: 0.875rem;
  font-weight: 600;
  cursor: pointer;
  transition: all 0.2s ease-in-out;
}
.btn-secondary:hover {
  background: var(--color-border);
  color: var(--color-text);
}

.btn-sm {
  padding: 0.375rem 0.875rem;
  font-size: 0.8rem;
}

.row-inactive td {
  opacity: 0.4;
}

.actions-cell {
  display: flex;
  gap: 0.75rem;
  align-items: center;
  white-space: nowrap;
}

.assignments-row > td {
  padding: 0;
  border-bottom: 1px solid var(--color-border);
}

.assignments-table {
  width: 100%;
  border-collapse: collapse;
}

.assignments-table th, .assignments-table td {
  padding: 0.75rem 1rem;
  text-align: left;
  border-bottom: 1px solid var(--color-border);
}

.assignments-table th {
  background: var(--color-surface);
  font-weight: 600;
  color: var(--color-text-muted);
}

.assignments-panel {
  background: var(--color-bg);
  padding: 2rem;
  border-radius: 12px;
  margin: 1rem 1.5rem 2rem 1.5rem;
  border: 1px solid var(--color-border);
}

.assignments-row:hover td {
  background: transparent !important;
}

.assignments-table {
  margin-bottom: 1.5rem;
}

.add-assignment h4 {
  margin: 1rem 0 0.75rem;
  font-size: 1rem;
}

.add-assignment-form {
  display: flex;
  gap: 1rem;
  align-items: stretch;
}

.assign-select {
  width: auto;
  min-width: 14rem;
}

.mono {
  font-family: monospace;
  font-size: 0.875rem;
  color: var(--color-text-muted);
}

.muted {
  color: var(--color-text-muted);
  font-size: 0.875rem;
}
</style>
