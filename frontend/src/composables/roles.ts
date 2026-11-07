// Copyright (c) Andreas Rain.
// Licensed under the Elastic License 2.0. See LICENSE file in the project root for full license terms.

/**
 * Role levels used by the client- and tenant-scoped role-gating helpers.
 * Values mirror the backend ClientRole/TenantRole enums: -1 = Viewer (clients only), 0 = User, 1 = Administrator.
 * A viewer reads a client's reviews and starts none; a user also starts and restarts reviews.
 */
export const RoleLevel = {
  Viewer: -1,
  User: 0,
  Administrator: 1,
} as const

export type RoleLevel = (typeof RoleLevel)[keyof typeof RoleLevel]
