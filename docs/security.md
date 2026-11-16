# Security

AegisBank is a simulation with fictional data. The controls below are real
and tested, but the project has not been audited, penetration-tested or
certified against any standard. See [limitations.md](limitations.md).

## Authentication

### API: bearer tokens

- `POST /api/auth/login` verifies the password and returns a JWT access token
  and an opaque refresh token.
- Access tokens are HMAC-SHA256 signed, carry `sub`, `name`, `role`,
  `customer_id` and `jti`, and live for 10 minutes (`Jwt:AccessTokenMinutes`).
  Issuer, audience, lifetime, signature and algorithm are validated; clock
  skew is 30 seconds.
- The signing key comes from `Jwt__SigningKey` and must be at least 32 bytes.
  The API refuses to start without it.
- Refresh tokens are 32 random bytes. Only their SHA-256 hash is stored.
  They last 7 days and are **rotated on every use**: the presented token is
  revoked and a new one issued in the same token family. Presenting a token
  that was already rotated is treated as theft: every token of that family
  is revoked and the event is audited (`auth.refresh.reuse_detected`).
  Rotation takes a row lock, so two simultaneous uses of one token cannot
  both succeed.
- `POST /api/auth/logout` revokes the family of the presented refresh token.

Access tokens are not checked against the database on each request, so a
disabled user keeps API access until the token expires (at most 10 minutes).

### Dashboard: cookie

- Cookie authentication with `HttpOnly`, `SameSite=Strict`, `Secure` when
  HTTPS is required, 30-minute sliding expiry.
- Only `Admin` and `Auditor` can sign in. A customer with a correct password
  gets the same message as a wrong password.
- On every request the cookie principal is re-validated against the database
  (user still active, role unchanged), so disabling a user takes effect
  immediately.
- All state-changing requests are POSTs protected by ASP.NET Core antiforgery
  tokens, including sign-out. A POST without a valid token is rejected with
  400 before any handler runs.
- Data Protection keys are persisted to `DataProtection:KeysPath` so sessions
  survive restarts and work across replicas.

### Passwords

`PasswordHasher<T>` from ASP.NET Core Identity (PBKDF2-HMAC-SHA512 with a
per-password salt, current Identity v3 format). Hashes are upgraded on
sign-in when the framework reports a newer format.

### Handling of failed sign-ins

- Unknown user, wrong password, locked account, disabled account and the
  internal `system` user all return the same response: 401 `Invalid credentials.`
- A dummy hash is verified when the username does not exist, so both paths
  cost one PBKDF2 computation.
- After `Auth:MaxFailedLogins` (5) consecutive failures the account is locked
  for `Auth:LockoutMinutes` (15).
- The real reason is written to the audit log (`auth.login.failed`), never to
  the response. Passwords are never logged.

## Authorization

| Role | API | Dashboard |
|---|---|---|
| Customer | Own accounts, own transfers, transfer from own accounts | No access |
| Auditor | Read everything, including ledger, audit log and admin overview. No mutation | Read-only |
| Admin | Everything, including opening accounts, deposits, status changes and reversals | Full |
| System | Internal actor for seed postings. Cannot sign in | — |

- Ownership is enforced in the query and service layer (`Actor.CanView`,
  `Actor.CanDebit`), not only by route attributes. Customer queries are
  filtered by `customer_id` in SQL.
- Accounts and transfers that a caller may not see are reported as **404**,
  so responses do not confirm that an identifier exists.
- System accounts can never be the source or destination of a customer
  transfer.
- Denied transfer attempts are audited (`transfer.denied`).

## Input handling

- Model validation on every request body and query parameter; failures return
  Problem Details with field-level errors.
- Sort fields and enum filters are matched against whitelists.
- All SQL is parameterised: EF Core LINQ, `FromSqlInterpolated` and
  `ExecuteSqlInterpolated` (which turn interpolated values into parameters).
  The only non-parameterised SQL is constant text. `LIKE` wildcards in search
  input are escaped.
- Idempotency keys are restricted to 8–128 printable ASCII characters.
- Caller-supplied correlation ids are accepted only when short and
  alphanumeric; otherwise one is generated.
- Razor encodes all output. The dashboard has no inline script or style, and
  its Content-Security-Policy is `default-src 'self'; script-src 'self'; style-src 'self'; form-action 'self'; frame-ancestors 'none'`.
- The sign-in redirect only follows local URLs.

## Error handling

All API errors are RFC 9457 Problem Details with a stable `code` and the
request's `correlationId`. Unhandled exceptions are logged server-side with
the correlation id and returned as a generic 500; no exception message, stack
trace, SQL or connection detail is sent to a client. Health responses contain
fixed descriptions only.

## Transport and headers

- `Security:RequireHttps` (default `true` outside Development) enables HSTS
  and HTTPS redirection and marks cookies `Secure`. Kestrel's HTTPS endpoint
  and certificate are configured through the standard
  `ASPNETCORE_URLS` / `ASPNETCORE_Kestrel__Certificates__Default__*`
  variables, or TLS is terminated at a reverse proxy (forwarded headers are
  honoured).
- The Compose file sets `Security__RequireHttps=false` because it serves plain
  HTTP on localhost. That is a development convenience, not a deployment
  recommendation.
- Every response carries `X-Content-Type-Options: nosniff`,
  `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`, a
  Content-Security-Policy and a Permissions-Policy.

## Rate limiting

- API: token bucket per authenticated user (or per IP when anonymous),
  `RateLimiting:PermitPerMinute` (600).
- Login and refresh: fixed window per IP, `RateLimiting:AuthPermitPerMinute` (10).
- Dashboard sign-in POST: fixed window per IP.
- Rejections return 429 Problem Details.

Limits are per process and held in memory; behind several replicas the
effective limit is multiplied. See limitations.

## Audit and logging

- `audit_logs` records sign-in success and failure, lockouts, refresh-token
  reuse, sign-out, denied transfers, completed and rejected transfers,
  customer and account creation, and account status changes, each with actor,
  entity, JSON metadata and correlation id.
- Transfer audit rows are written in the same transaction as the transfer.
- The table is append-only: a database trigger rejects UPDATE and DELETE.
- Application logs are structured JSON outside Development and include the
  correlation id as a scope. Secrets, tokens and passwords are not logged.

## Secrets and configuration

| Variable | Purpose |
|---|---|
| `ConnectionStrings__Database` | PostgreSQL connection string |
| `Jwt__SigningKey` | HMAC key for access tokens (≥ 32 bytes) |
| `Seed__AdminPassword`, `Seed__AuditorPassword`, `Seed__CustomerPassword` | Passwords of the fictional demo users |
| `DataProtection__KeysPath` | Dashboard cookie key ring location |

No secret is committed. `.env` is git-ignored; `.env.example` contains
placeholders and clearly labelled demo values. `appsettings.json` contains no
credentials.

## Demo data

The seed creates fictional people and accounts and is controlled by
`Seed:Enabled`. `DemoSeeder` refuses to run when the environment is
`Production`, whatever the configuration says (tested). Do not put real
customer data into this system.

## Least privilege

- Containers run as the non-root `app` user.
- The application needs only DML rights at run time. Migrations (DDL) are
  applied when `Database:MigrateOnStartup=true`; in a hardened setup they
  would be run by a separate role and the flag left off. The Compose stack
  uses a single database role for simplicity.
