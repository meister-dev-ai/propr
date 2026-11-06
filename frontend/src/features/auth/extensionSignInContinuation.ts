const key = 'propr:extension-sign-in-request'
let storageUnavailable = false
let remembered = false
let failedContinuation = false

type Continuation = { name: 'extension-consent'; query: { request: string } | { error: 'continuation_unavailable' } }
const restart = (): Continuation => ({ name: 'extension-consent', query: { error: 'continuation_unavailable' } })
function isRequest(request: unknown): request is string {
  return typeof request === 'string' && request.length > 0 && request.length <= 16384 && /^[A-Za-z0-9_:.-]+$/.test(request)
}

/** Stores only the protected request of one typed consent route, never a return URL. */
export function rememberExtensionSignIn(request: unknown): boolean {
  if (!isRequest(request)) { forgetExtensionSignIn(); return false }
  remembered = true
  failedContinuation = false
  try {
    sessionStorage.setItem(key, request)
    storageUnavailable = false
    return true
  } catch {
    storageUnavailable = true
    failedContinuation = true
    return false
  }
}

export function takeExtensionSignIn(): Continuation | undefined {
  if (failedContinuation) { failedContinuation = false; remembered = false; return restart() }
  if (storageUnavailable) return undefined
  let request: string | null = null
  try {
    request = sessionStorage.getItem(key)
    sessionStorage.removeItem(key)
  } catch {
    storageUnavailable = true
    const knownAttempt = remembered || isRequest(request)
    remembered = false
    return knownAttempt ? restart() : undefined
  }
  remembered = false
  return isRequest(request) ? { name: 'extension-consent', query: { request } } : undefined
}

export function forgetExtensionSignIn(): void {
  remembered = false
  failedContinuation = false
  try { sessionStorage.removeItem(key) }
  catch { storageUnavailable = true }
}

/** A failed continuation must stop before login can navigate to an external identity provider. */
export function extensionSignInLoginRoute(request: unknown): Continuation | { name: 'login' } {
  return rememberExtensionSignIn(request) ? { name: 'login' } : restart()
}

export function isExtensionSignInUnavailable(query: Record<string, unknown>): boolean {
  return Object.keys(query).length === 1 && query.error === 'continuation_unavailable'
}
