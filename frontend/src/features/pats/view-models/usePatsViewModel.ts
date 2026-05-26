// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { onMounted, ref, type Ref } from 'vue'
import { getActiveRuntime } from '@/app/runtime/runtimeContext'
import { useSession } from '@/composables/useSession'

export interface PatItem {
  id: string
  label: string
  createdAt: string
  lastUsedAt: string | null
  expiresAt: string | null
  isRevoked: boolean
}

export interface PatsService {
  list: (headers: Record<string, string>) => Promise<PatItem[]>
  create: (headers: Record<string, string>, body: { label: string; expiresAt?: string }) => Promise<{ token: string }>
  revoke: (headers: Record<string, string>, id: string) => Promise<void>
}

const defaultService: PatsService = {
  async list(headers) {
    const res = await fetch(`${getActiveRuntime().apiBaseUrl}/users/me/pats`, { headers })
    if (!res.ok) throw new Error(await res.text())
    return (await res.json()) as PatItem[]
  },
  async create(headers, body) {
    const res = await fetch(`${getActiveRuntime().apiBaseUrl}/users/me/pats`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', ...headers },
      body: JSON.stringify(body),
    })
    if (!res.ok) throw new Error(await res.text())
    return (await res.json()) as { token: string }
  },
  async revoke(headers, id) {
    await fetch(`${getActiveRuntime().apiBaseUrl}/users/me/pats/${id}`, { method: 'DELETE', headers })
  },
}

export interface PatsViewModel {
  readonly name: 'usePatsViewModel'
  pats: Ref<PatItem[]>
  loading: Ref<boolean>
  loadError: Ref<string>
  creating: Ref<boolean>
  createError: Ref<string>
  generatedToken: Ref<string>
  newLabel: Ref<string>
  newExpires: Ref<string>
  revoking: Ref<string | null>

  loadPats: () => Promise<void>
  createToken: () => Promise<void>
  revokeToken: (id: string) => Promise<void>
  dismissGeneratedToken: () => void
  copyGeneratedToken: () => Promise<void>
  formatDate: (iso: string) => string
}

export interface UsePatsViewModelOptions {
  /** Override the PAT service; tests inject a fake. */
  service?: PatsService
  /** Skip loading on mount. */
  autoLoad?: boolean
  /** Override the clipboard handler. Defaults to navigator.clipboard.writeText. */
  copyToClipboard?: (value: string) => Promise<void>
}

export function usePatsViewModel(options: UsePatsViewModelOptions = {}): PatsViewModel {
  const { getAccessToken } = useSession()
  const service = options.service ?? defaultService
  const autoLoad = options.autoLoad ?? true
  const copyFn = options.copyToClipboard ?? ((value: string) => navigator.clipboard.writeText(value))

  const pats = ref<PatItem[]>([])
  const loading = ref(false)
  const loadError = ref('')
  const creating = ref(false)
  const createError = ref('')
  const generatedToken = ref('')
  const newLabel = ref('')
  const newExpires = ref('')
  const revoking = ref<string | null>(null)

  function authHeaders(): Record<string, string> {
    const token = getAccessToken()
    return token ? { Authorization: `Bearer ${token}` } : {}
  }

  async function loadPats(): Promise<void> {
    loading.value = true
    loadError.value = ''
    try {
      pats.value = await service.list(authHeaders())
    } catch (err) {
      loadError.value = String(err)
    } finally {
      loading.value = false
    }
  }

  async function createToken(): Promise<void> {
    createError.value = ''
    if (!newLabel.value.trim()) {
      createError.value = 'Label is required.'
      return
    }
    creating.value = true
    try {
      const body: { label: string; expiresAt?: string } = { label: newLabel.value }
      if (newExpires.value) body.expiresAt = new Date(newExpires.value).toISOString()
      const result = await service.create(authHeaders(), body)
      generatedToken.value = result.token
      newLabel.value = ''
      newExpires.value = ''
      await loadPats()
    } catch (err) {
      createError.value = String(err)
    } finally {
      creating.value = false
    }
  }

  async function revokeToken(id: string): Promise<void> {
    revoking.value = id
    try {
      await service.revoke(authHeaders(), id)
      pats.value = pats.value.filter((pat) => pat.id !== id)
    } finally {
      revoking.value = null
    }
  }

  function dismissGeneratedToken(): void {
    generatedToken.value = ''
  }

  async function copyGeneratedToken(): Promise<void> {
    if (generatedToken.value) {
      await copyFn(generatedToken.value)
    }
  }

  function formatDate(iso: string): string {
    return new Date(iso).toLocaleString()
  }

  if (autoLoad) {
    onMounted(loadPats)
  }

  return {
    name: 'usePatsViewModel',
    pats,
    loading,
    loadError,
    creating,
    createError,
    generatedToken,
    newLabel,
    newExpires,
    revoking,
    loadPats,
    createToken,
    revokeToken,
    dismissGeneratedToken,
    copyGeneratedToken,
    formatDate,
  }
}
