// @vitest-environment node

import { describe, expect, it } from 'vitest'
import { rewriteApiProxyPath } from '../vite.config'

describe('rewriteApiProxyPath', () => {
  it('rewrites tenant admin endpoints to backend routing', () => {
    expect(rewriteApiProxyPath('/api/admin/tenants')).toBe('/admin/tenants')
    expect(rewriteApiProxyPath('/api/admin/tenants/tenant-1')).toBe('/admin/tenants/tenant-1')
    expect(rewriteApiProxyPath('/api/admin/tenants/tenant-1/memberships')).toBe('/admin/tenants/tenant-1/memberships')
  })

  it('rewrites non-tenant endpoints to match backend routing', () => {
    expect(rewriteApiProxyPath('/api/clients')).toBe('/clients')
    expect(rewriteApiProxyPath('/api/auth/me')).toBe('/auth/me')
    expect(rewriteApiProxyPath('/api/admin/providers')).toBe('/admin/providers')
  })
})
