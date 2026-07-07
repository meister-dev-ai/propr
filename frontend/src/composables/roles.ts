// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Role levels used by the client- and tenant-scoped role-gating helpers.
 * Values mirror the backend ClientRole/TenantRole enums: 0 = User, 1 = Administrator.
 */
export const RoleLevel = {
  User: 0,
  Administrator: 1,
} as const

export type RoleLevel = (typeof RoleLevel)[keyof typeof RoleLevel]
