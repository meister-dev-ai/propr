import { computed, ref } from 'vue'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import type { Router } from 'vue-router'

const authenticated = ref(false)
const { getConsent, startSso } = vi.hoisted(() => ({ getConsent: vi.fn(), startSso: vi.fn() }))
vi.mock('@/composables/useSession', () => ({ useSession: () => ({
  isAuthenticated: computed(() => authenticated.value), isAdmin: ref(false), username: ref('alice'),
  clientRoles: ref({}), tenantRoles: ref({}), hasClientRole: () => false, hasTenantRole: () => false,
  isCapabilityAvailable: () => true,
}) }))
vi.mock('@/services/extensionAuthorizationService', () => ({ getExtensionConsent: getConsent, decideExtensionConsent: vi.fn() }))
vi.mock('@/features/auth/views/LoginView.vue', () => ({ default: {
  template: '<button data-testid="start-sso" @click="startSso">Sign in with SSO</button>', setup: () => ({ startSso }),
} }))
let router: Router
let wrapper: VueWrapper | undefined
beforeEach(async () => {
  vi.restoreAllMocks(); vi.resetModules()
  const values = new Map<string, string>()
  vi.stubGlobal('sessionStorage', {
    getItem: (key: string) => values.get(key) ?? null,
    setItem: (key: string, value: string) => { values.set(key, value) },
    removeItem: (key: string) => { values.delete(key) },
    clear: () => values.clear(),
  })
  authenticated.value = false; getConsent.mockReset(); startSso.mockReset()
  window.history.replaceState({}, '', '/')
  router = (await import('@/app/router')).default
})
afterEach(() => { wrapper?.unmount(); wrapper = undefined; vi.unstubAllGlobals() })
async function render() {
  wrapper = mount({ template: '<router-view />' }, { global: { plugins: [router], stubs: { RouterView: false } } })
  await router.isReady(); await flushPromises()
  return wrapper
}
const unavailable = { name: 'extension-consent', query: { error: 'continuation_unavailable' } }

describe('extension sign-in routing', () => {
  it('stops before login or SSO when the protected continuation cannot be stored', async () => {
    vi.spyOn(sessionStorage, 'setItem').mockImplementation(() => { throw new Error('private storage failure') })
    await router.push({ name: 'extension-consent', query: { request: 'protected-request' } })
    const page = await render()
    expect(router.currentRoute.value.name).toBe('extension-consent')
    expect(router.currentRoute.value.query).toEqual(unavailable.query)
    expect(page.get('[role="alert"]').text()).toContain('session storage')
    expect(page.find('[data-testid="start-sso"]').exists()).toBe(false)
    expect(getConsent).not.toHaveBeenCalled()
    expect(startSso).not.toHaveBeenCalled()
  })
  it('still requires website login for a persisted valid request', async () => {
    await router.push({ name: 'extension-consent', query: { request: 'protected-request' } })
    const page = await render()
    expect(router.currentRoute.value.name).toBe('login')
    expect(page.find('[data-testid="start-sso"]').exists()).toBe(true)
    const continuation = await import('@/features/auth/extensionSignInContinuation')
    expect(continuation.takeExtensionSignIn()).toEqual({ name: 'extension-consent', query: { request: 'protected-request' } })
  })
  it('allows only the fixed static error surface without authentication', async () => {
    await router.push(unavailable)
    const page = await render()
    expect(router.currentRoute.value.name).toBe('extension-consent')
    expect(page.text()).toContain('extension settings and select Sign in again')
    expect(getConsent).not.toHaveBeenCalled()
    await router.push({ name: 'extension-consent', query: { ...unavailable.query, request: 'protected-request' } })
    expect(router.currentRoute.value.name).toBe('login')
    await router.push({ name: 'reviews', query: unavailable.query })
    expect(router.currentRoute.value.name).toBe('login')
  })
  it('preserves tenant-link confirmation before a remembered extension continuation', async () => {
    authenticated.value = true
    const continuation = await import('@/features/auth/extensionSignInContinuation')
    continuation.rememberExtensionSignIn('protected-request')
    ;(await import('@/features/auth/pendingSignInLink')).rememberPendingLinkConfirmation()
    const home = router.getRoutes().find((route) => route.name === 'home')!
    expect((home.redirect as () => unknown)()).toEqual({ name: 'sign-in-link' })
  })
  it('replaces an expired consent session with the static error before login when storage fails', async () => {
    authenticated.value = true
    getConsent.mockResolvedValue({ application: 'ProPR Lens', issuer: 'https://propr.test/api', permissions: [] })
    await router.push({ name: 'extension-consent', query: { request: 'protected-request' } })
    const page = await render()
    expect(page.find('.btn-primary').exists()).toBe(true)
    vi.spyOn(sessionStorage, 'setItem').mockImplementation(() => { throw new Error('private storage failure') })
    const continuation = await import('@/features/auth/extensionSignInContinuation')
    const destination = continuation.extensionSignInLoginRoute(router.currentRoute.value.query.request)
    authenticated.value = false
    await router.replace(destination)
    await flushPromises()
    expect(router.currentRoute.value.query).toEqual(unavailable.query)
    expect(page.get('[role="alert"]').text()).toContain('session storage')
    expect(page.find('.btn-primary').exists()).toBe(false)
    expect(page.find('[data-testid="start-sso"]').exists()).toBe(false)
    expect(getConsent).toHaveBeenCalledTimes(1)
  })
})
