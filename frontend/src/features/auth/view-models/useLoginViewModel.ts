// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

import { computed, onMounted, ref, type ComputedRef, type Ref } from 'vue'
import { useRouter } from 'vue-router'
import { getActiveRuntime } from '@/app/runtime/runtimeContext'
import { getAuthOptions, supportsTenantSignIn, type AuthOptions } from '@/services/authOptionsService'
import { useSession } from '@/composables/useSession'

type LoginEndpoint = (payload: { username: string; password: string }) => Promise<{ accessToken: string }>

class LoginInvalidCredentialsError extends Error {
  constructor() { super('Invalid username or password') }
}
class LoginRequestError extends Error {
  constructor(message: string) { super(message) }
}

const defaultLogin: LoginEndpoint = async (payload) => {
  const res = await fetch(getActiveRuntime().apiBaseUrl + '/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload),
    credentials: 'include', // store the httpOnly refresh cookie set by the backend
  })
  if (res.status === 401) throw new LoginInvalidCredentialsError()
  if (!res.ok) throw new LoginRequestError('Login failed. Please try again.')
  return (await res.json()) as { accessToken: string }
}

export interface LoginViewModel {
  readonly name: 'useLoginViewModel'

  /** Auth options snapshot loaded on mount. Null while loading or on failure. */
  authOptions: Ref<AuthOptions | null>
  authOptionsError: Ref<string>
  signInMessage: ComputedRef<string>
  ssoCapabilityMessage: ComputedRef<string>
  canUseTenantSignIn: ComputedRef<boolean>

  username: Ref<string>
  password: Ref<string>
  tenantSlug: Ref<string>
  showTenantSlugPrompt: Ref<boolean>
  loading: Ref<boolean>
  validationError: Ref<string>
  authError: Ref<string>
  tenantValidationError: Ref<string>

  loadAuthOptions: () => Promise<void>
  submitLogin: () => Promise<void>
  submitTenantLogin: () => Promise<void>
  closeTenantPrompt: () => void
}

export interface UseLoginViewModelOptions {
  /** Override the login service call. Used by tests. */
  loginService?: LoginEndpoint
  /** Override the auth-options fetcher. Used by tests. The view-model tolerates a
   *  null result (authOptions stays null), so the seam allows it explicitly. */
  authOptionsService?: () => Promise<AuthOptions | null>
  /** Skip the onMounted auth-options load; tests can call loadAuthOptions() explicitly. */
  autoLoadAuthOptions?: boolean
}

export function useLoginViewModel(options: UseLoginViewModelOptions = {}): LoginViewModel {
  const router = useRouter()
  const { establishSession } = useSession()
  const loginFn = options.loginService ?? defaultLogin
  const authOptionsFn = options.authOptionsService ?? getAuthOptions
  const autoLoad = options.autoLoadAuthOptions ?? true

  const authOptions = ref<AuthOptions | null>(null)
  const authOptionsError = ref('')
  const username = ref('')
  const password = ref('')
  const tenantSlug = ref('')
  const showTenantSlugPrompt = ref(false)
  const loading = ref(false)
  const validationError = ref('')
  const authError = ref('')
  const tenantValidationError = ref('')

  const signInMessage = computed(() => {
    if (!authOptions.value) return 'Sign in with your username and password.'
    return authOptions.value.availableSignInMethods.includes('sso')
      ? 'Password and single sign-on are available for this installation.'
      : 'Password sign-in is available for this installation.'
  })

  const ssoCapabilityMessage = computed(() =>
    authOptions.value?.capabilities.find((capability) => capability.key === 'sso-authentication')?.message ?? '',
  )

  const canUseTenantSignIn = computed(() => supportsTenantSignIn(authOptions.value))

  async function loadAuthOptions(): Promise<void> {
    try {
      authOptions.value = await authOptionsFn()
    } catch {
      authOptionsError.value = 'Unable to load sign-in options right now.'
    }
  }

  async function submitLogin(): Promise<void> {
    validationError.value = ''
    authError.value = ''

    if (!username.value.trim()) {
      validationError.value = 'Username is required'
      return
    }
    if (!password.value) {
      validationError.value = 'Password is required'
      return
    }

    loading.value = true
    try {
      const session = await loginFn({ username: username.value, password: password.value })
      await establishSession(session)
      await router.push({ name: 'home' })
    } catch (err) {
      if (err instanceof LoginInvalidCredentialsError) {
        authError.value = err.message
      } else if (err instanceof LoginRequestError) {
        authError.value = err.message
      } else {
        authError.value = 'Connection error. Please try again.'
      }
    } finally {
      loading.value = false
    }
  }

  async function submitTenantLogin(): Promise<void> {
    tenantValidationError.value = ''
    if (!tenantSlug.value.trim()) {
      tenantValidationError.value = 'Tenant slug is required'
      return
    }
    await router.push({
      name: 'tenant-login',
      params: { tenantSlug: tenantSlug.value.trim() },
    })
  }

  function closeTenantPrompt(): void {
    tenantValidationError.value = ''
    tenantSlug.value = ''
    showTenantSlugPrompt.value = false
  }

  if (autoLoad) {
    onMounted(loadAuthOptions)
  }

  return {
    name: 'useLoginViewModel',
    authOptions,
    authOptionsError,
    signInMessage,
    ssoCapabilityMessage,
    canUseTenantSignIn,
    username,
    password,
    tenantSlug,
    showTenantSlugPrompt,
    loading,
    validationError,
    authError,
    tenantValidationError,
    loadAuthOptions,
    submitLogin,
    submitTenantLogin,
    closeTenantPrompt,
  }
}
