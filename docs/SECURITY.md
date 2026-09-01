# Security

## Invariants

These are requirements, not preferences. A change that breaks one of them is a bug even
if every test passes.

```text
- no anonymous proxying
- the acting Seerr identity comes from Jellyfin authentication and from nothing else
- a Jellyfin API key is not a user and can never proxy
- client-supplied identity never reaches Seerr, in a header or in a body
- the forwarded path is allowlisted before any other work happens
- a forwarded URL can never leave the configured Seerr /api/v1 root
- the Seerr API key is never returned to a client, logged, or echoed in an error
- the Seerr API key never travels to a host other than the configured one
- every surface is rate limited regardless of how well-credentialed the caller is
- plugin disabled or removed -> proxying is impossible
- an upstream response body is never relayed to a client unparsed
```

## The trust model

This plugin is a **capability transfer**. Seerr's API key is a server-wide credential;
the plugin holds it and hands out a much narrower capability in its place — "act as your
own linked Seerr user, on this list of endpoints". Everything here exists to keep that
narrowing intact.

Two surfaces, gated differently:

| Surface | Gate 1 | Gate 2 | Rate limit |
| --- | --- | --- | --- |
| `GET /Plugins/SeerrProxy/Status` | Jellyfin auth, real user | — | per user |
| `/Plugins/SeerrProxy/api/v1/**` | Jellyfin auth, real user | allowlist | per user |
| `POST /Plugins/SeerrProxy/Test` | `Policies.RequiresElevation` | operator secret | process-wide |

## Why "real user" is a gate

On Jellyfin 10.11.x, `CustomAuthenticationHandler` assigns the `Administrator` role to
**any valid API key**, not only to admin users
(`authorizationInfo.IsApiKey || user.HasPermission(IsAdministrator)`). Every API key
already issued on the server therefore satisfies `Policies.RequiresElevation` on its own.

The proxy is safe from this because it does not ask "are you elevated?" — it asks "which
Jellyfin user are you?", and an API key answers `Guid.Empty`. There is no user to resolve
to a Seerr account, so the request is refused with 401. **This is load-bearing.** Any
future change that lets the proxy fall back to a configured or default user would hand
every API key on the server the power to file requests as somebody.

The elevated `Test` endpoint has no such natural defence, which is what the operator
secret is for.

## The operator secret

Header (fixed, do not rename):

```http
X-Seerr-Proxy-Secret: <secret>
```

### Generation

At least 256 bits of randomness, machine-generated. Run this on a trusted machine:

```bash
SECRET="$(openssl rand -base64 32 | tr '+/' '-_' | tr -d '=')"
HASH="$(printf '%s' "$SECRET" | sha256sum | awk '{print $1}')"
printf 'Operator secret (keep for the caller): %s\n' "$SECRET"
printf 'Operator hash   (give Jellyfin):       %s\n' "$HASH"
```

The `awk '{print $1}'` is not cosmetic. `sha256sum` prints `<hash>  -`, and those
trailing characters make the configured value 67 characters rather than 64, which
`AdminSecretVerifier` rejects as malformed — the gate then refuses every request instead
of failing loudly.

### Storage

The plugin stores nothing. Only `HASH` from above — `SHA-256(secret)`, hex-encoded — is
supplied by the deployment environment:

```text
SEERR_PROXY_ADMIN_SECRET_HASH        the hash itself
SEERR_PROXY_ADMIN_SECRET_HASH_FILE   path to a root-owned or mounted file holding it
SEERR_PROXY_REQUIRE_ADMIN_SECRET     set to 1 to make the gate mandatory
```

The file form takes precedence when both are set, and is the preferred one: the file can
be root-owned with restrictive permissions, and only its *path* ever reaches a log.

Plain SHA-256 is sufficient **because the secret is a uniformly random 256-bit value** —
offline guessing is not a realistic attack against that input. If human-chosen
passphrases are ever accepted, this must change to a password KDF (Argon2id/bcrypt).

Verification compares fixed-size hashes in **constant time**
(`CryptographicOperations.FixedTimeEquals`).

### Opt-in, then fail closed

Unlike this plugin's sibling `jellyfin-plugin-session-provisioning`, the secret is **not
mandatory by default**, and that difference is deliberate. There, the gated endpoint
mints sessions; here it reports a version string. Making the secret mandatory on upgrade
would break the dashboard's Test button on every existing install to protect a
low-value operation.

So the behaviour is:

```text
no hash configured                        -> Test requires elevation only
hash configured                           -> Test requires elevation AND the secret
hash malformed, REQUIRE_ADMIN_SECRET unset-> Test requires elevation only
hash absent or malformed, REQUIRE set     -> Test refuses everything
```

Set `SEERR_PROXY_REQUIRE_ADMIN_SECRET=1` in any deployment that cares. It is what stops a
hash file that fails to mount from silently downgrading the gate back to elevation alone.

Once a usable hash *is* configured, the gate is absolute: no localhost exemption, no
allowlisted key, no bypass.

## The Seerr API key

The key may come from either source; the environment wins when both are set:

```text
SEERR_PROXY_API_KEY_FILE   path to a root-owned or mounted file holding the key   (preferred)
SEERR_PROXY_API_KEY        the key itself
plugin configuration       the dashboard field                                    (fallback)
```

### Why the environment is preferred

A key stored in plugin configuration is readable back through
`GET /Plugins/Configuration/{guid}`, which — by the quirk above — **every API key on the
server can reach.** It is also written in plaintext into Jellyfin's plugin configuration
XML, which lands in backups.

Supplying it through the environment removes both exposures: the key is never written to
Jellyfin's configuration, never returned by the configuration endpoint, and never
rendered into the dashboard page. When the environment supplies it, the configuration
page reports the source and makes the field inert.

Migrating an existing install: set the environment variable, confirm the configuration
page shows *Supplied by the environment*, then clear the stored value and save.

**Do not name these variables with a `JELLYFIN_` prefix.** Jellyfin logs every
environment variable beginning with `JELLYFIN_`, `DOTNET_`, or `ASPNETCORE_`, with its
value, on every startup (`StartupHelpers.LogEnvironmentInfo`). A prefixed name puts the
secret in the server log at each boot.

### Keeping the key on the configured host

Outbound requests set `AllowAutoRedirect = false`. .NET strips only `Authorization`
across a cross-origin redirect; a **custom header such as `X-Api-Key` is carried to
whatever host the redirect names**. Following redirects would therefore turn an open
redirect in front of Seerr — or a plain-HTTP man-in-the-middle — into a way to
exfiltrate the key. Nothing this plugin calls redirects, so a 3xx is reported to the
administrator as a base-URL misconfiguration instead.

## Identity

The acting Seerr user is resolved server-side from Jellyfin's authorization info and sent
as `X-API-User`, an integer the client never sees or supplies.

Two channels could carry a competing identity, and both are closed:

- **Headers.** The plugin builds a fresh outbound `HttpRequestMessage` and copies no
  inbound headers, so a client-supplied `X-API-User`, `X-Api-Key`, or cookie cannot
  reach Seerr.
- **Body.** Seerr's request API also accepts a top-level `userId` and honours it when the
  calling identity holds `MANAGE_REQUESTS`. A body forwarded verbatim would therefore let
  a caller whose linked Seerr user happens to be privileged file requests as somebody
  else. `Security.ForwardedPayload` removes `userId`, `user`, `requestedBy`, and
  `modifiedBy` from the top level of every forwarded body, case-insensitively, and logs
  when it does.

Nested occurrences are left alone: Seerr honours only the top-level field, and rewriting
arbitrary nested data would corrupt legitimate payloads.

## The allowlist

Positive, not negative: a route family must be named in `Security.ApiAllowlist` to be
reachable, and anything unrecognised is a 404. It runs **before** the plugin resolves the
caller's Seerr user or reads configuration, so an unsupported path costs no work and
discloses nothing.

Every segment must additionally be non-empty, at most 64 characters, drawn from RFC 3986
unreserved characters (`A-Za-z0-9._~-`), and must not consist only of dots. At most 8
segments; the query string is capped at 2048 characters and may contain no control
character and no `#`.

### Why dot segments are checked twice

Kestrel resolves `.` and `..` out of a request path before routing sees it. But this
plugin composes the forwarded URI itself with `Uri` relative resolution, which *does*
honour dot segments — so a value that reached the route through some other decoding path
must not be able to climb out of Seerr's `/api/v1/` prefix.

The `discover` family is the one that permits arbitrary sub-paths (Seerr's own discover
routes are open-ended), which makes it the natural vehicle for such an attempt. Two
independent checks stop it: the segment validator rejects `..` and `%`, and
`SeerrClient.BuildUri` asserts `apiRoot.IsBaseOf(resolved)` on the composed URI and
refuses to issue anything that fails.

## Rate limiting

```text
proxy surface   120 requests per Jellyfin user per minute
elevated surface 30 requests process-wide per minute
```

Partitioned per user on the proxy so one client looping on `search` cannot starve
everyone else or turn the plugin into an amplifier pointed at Seerr. Process-wide and
much tighter on the elevated surface, where the only legitimate caller is an
administrator pressing a button.

The elevated limiter runs **before** the operator secret is examined, so it is also the
brute-force bound on that secret. A correct secret does not bypass it.

Both windows are process-local and reset when Jellyfin restarts. They are abuse bounds,
not quotas. A plugin cannot add middleware to Jellyfin's pipeline, so this uses
`System.Threading.RateLimiting` from the ASP.NET Core shared framework directly.

## Resource bounds

```text
request body    256 KiB   buffered whole before parsing -> 413 beyond
response body     8 MiB   buffered whole before parsing -> 502 beyond
request timeout   configured, clamped to 300s, applied with a linked CancellationTokenSource
```

The `HttpClient`'s own 100-second default timeout is disabled
(`Timeout.InfiniteTimeSpan`) because leaving it would silently cap a longer configured
value. `PooledConnectionLifetime` is five minutes so a singleton client does not pin the
first DNS answer for the life of the process.

## Information disclosure

An upstream body is **never relayed unparsed**. Seerr answers every `/api/v1` endpoint
with JSON; anything else on a success status means the request did not reach Seerr — a
captive portal, or an error page from something in front of it — and forwarding that page
would disclose whatever it happens to contain. Such a response becomes a generic 502.

The same applies to errors: a JSON error body contributes its `message` or `error` field
and nothing else; a non-JSON error body is discarded in favour of the status line. Any
occurrence of the API key in a message is redacted before it leaves the process.

Upstream 5xx statuses are not passed through as-is — `ToClientStatusCode` maps anything
outside 4xx to 502, so a client cannot distinguish Seerr's internal failures.

## Logging

Never log:

- the caller's Jellyfin token or API key;
- the Seerr API key, or any prefix/suffix of it;
- the operator secret, or its configured hash;
- full request headers;
- an unparsed upstream response body.

Permitted: the shape of the operation. Status codes, methods, sanitized paths, Jellyfin
user GUIDs, the *names* of identity fields that were stripped.

### Log-entry integrity

Values that reach a log line — forwarded paths, stripped field names — pass through
`Security.LogSanitizer.ForLog`: control and format characters are replaced and the length
is bounded to 256, so a value cannot append a forged second entry or reorder the one
carrying it (CWE-117).

This is the second line of defence. `ApiAllowlist` already constrains every segment to a
character set that excludes anything dangerous. The sanitizer exists so that the
integrity of a log line does not depend on a validator declared in another file
continuing to be correct.

## Lifecycle: when proxying must be impossible

```text
plugin disabled           -> every endpoint 404s immediately, no restart needed
plugin DLL/dir removed    -> routes gone after restart
plugin "Enabled" unticked -> proxy endpoints 403, Test still available to configure
base URL or key absent    -> 503
```

Jellyfin registers plugin controllers from loaded assemblies **once at startup**, so a
route cannot be un-registered while the server runs, and disabling a running plugin leaves
its in-memory status at `Restart` — which `IsEnabledAndSupported` still counts as enabled.
The plugin closes that gap itself by requiring `PluginStatus.Active` exactly before doing
anything else.

Note the distinction between the two "disabled" states: unticking **Enabled** on the
configuration page stops the proxy but deliberately leaves `Test` reachable, because an
administrator must be able to verify a connection before turning the proxy on. Disabling
the **plugin** stops everything.

## Network defense in depth

Application authorization is mandatory even on a trusted network. Where the deployment
supports it, the elevated endpoint may *additionally* sit behind reverse-proxy source
restrictions, mTLS, or firewall policy. These are extra layers, never replacements — and
the plugin does not become a home-grown firewall (no IP allowlists in plugin config).

The proxy surface, by contrast, is meant to be publicly reachable: it is what TV clients
call. Its defence is authentication and the allowlist, not network position.

```nginx
# in the public server block, before the general Jellyfin proxy_pass
location ^~ /Plugins/SeerrProxy/Test {
    return 404;
}
```

A 404 rather than a 403 keeps the edge from advertising that the capability is installed.

## Accepted risks

Stated plainly rather than hidden:

- **A linked Seerr administrator gets Seerr administrator powers through the proxy.** The
  plugin forwards as the user's *existing* Seerr identity; it does not create a second
  RBAC list. `target Bob` gets Bob's permissions. This is the same property as
  `jellyfin-plugin-session-provisioning` and is expected.
- **An API key stored in plugin configuration is readable by any admin credential.**
  Unavoidable while that storage option exists; the environment-supplied path is the fix,
  and the configuration page says so.
- **`Status` confirms to any authenticated user whether Seerr is reachable** and reveals
  their own linked display name. Both are information the user is entitled to.
- **Rate-limit windows reset on restart.** They bound abuse, not usage.
