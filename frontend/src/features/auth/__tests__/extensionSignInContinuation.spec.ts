import { beforeEach, describe, expect, it, vi } from 'vitest'

let continuation: typeof import('../extensionSignInContinuation')
beforeEach(async () => {
  vi.restoreAllMocks()
  sessionStorage.clear()
  vi.resetModules()
  continuation = await import('../extensionSignInContinuation')
})

describe('extension sign-in continuation', () => {
  it('remembers only a bounded typed request, never an arbitrary redirect', () => {
    continuation.rememberExtensionSignIn('protected-request')
    expect(continuation.takeExtensionSignIn()).toEqual({ name: 'extension-consent', query: { request: 'protected-request' } })
    expect(continuation.takeExtensionSignIn()).toBeUndefined()
    continuation.rememberExtensionSignIn('https://evil.test/')
    expect(continuation.takeExtensionSignIn()).toBeUndefined()
  })
  it('does not retain oversized or multiple requests', () => {
    continuation.rememberExtensionSignIn(['first', 'second'])
    expect(continuation.takeExtensionSignIn()).toBeUndefined()
    continuation.rememberExtensionSignIn('x'.repeat(16385))
    expect(continuation.takeExtensionSignIn()).toBeUndefined()
  })
  it.each(['getItem', 'removeItem'] as const)('allows ordinary website login when storage %s throws', (operation) => {
    vi.spyOn(sessionStorage, operation).mockImplementation(() => { throw new Error('storage unavailable') })
    expect(continuation.takeExtensionSignIn()).toBeUndefined()
    expect(() => continuation.forgetExtensionSignIn()).not.toThrow()
  })
  it.each(['setItem', 'getItem', 'removeItem'] as const)('returns a safe restart route once when continuation %s throws', (operation) => {
    if (operation !== 'setItem') continuation.rememberExtensionSignIn('protected-request')
    vi.spyOn(sessionStorage, operation).mockImplementation(() => { throw new Error('private browser failure') })
    if (operation === 'setItem') expect(() => continuation.rememberExtensionSignIn('protected-request')).not.toThrow()
    expect(continuation.takeExtensionSignIn()).toEqual({ name: 'extension-consent', query: { error: 'continuation_unavailable' } })
    expect(continuation.takeExtensionSignIn()).toBeUndefined()
  })
  it('does not restore a stale request after cleanup fails', () => {
    continuation.rememberExtensionSignIn('protected-request')
    vi.spyOn(sessionStorage, 'removeItem').mockImplementation(() => { throw new Error('unavailable') })
    expect(() => continuation.forgetExtensionSignIn()).not.toThrow()
    expect(continuation.takeExtensionSignIn()).toBeUndefined()
  })
})
