# ADR-0005: Authentication and authorization

Status: Accepted (2026-10-27)

## Decision
- Own user store (email unique globally, belongs to exactly one tenant).
- Passwords hashed with ASP.NET Core Identity `PasswordHasher<T>` (PBKDF2-HMAC-SHA512, 100k iters).
- JWT access token (15 min default, HS256 with key from configuration/secret store) carrying
  `sub`, `tid`, `email`, `name`.
- Opaque refresh tokens (32 random bytes), stored as SHA-256 hashes, rotated on every use, grouped
  into families; reuse of a rotated token revokes the family.
- Roles are assigned per outlet (`user_roles.outlet_id`) or tenant-wide (`outlet_id IS NULL`, owner
  and tenant managers). Permissions are a static role→permission matrix in code
  (`RolePermissions`). Authorization is evaluated per request from the database.
- Kitchen screens use normal user logins with the `Kitchen` role.

## Consequences
- Deactivating a user or removing a role takes effect on the next request.
- The signing key must be provided via environment/secret in non-development environments; the API
  refuses to start in Production with the development key.
