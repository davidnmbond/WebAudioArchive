# WebAudioArchive Plan 4a: Secure sign-in (Google and GitHub) using PanoramicData.AuthMagic

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. Group A runs in the PanoramicData.AuthMagic repository under that repository's own rules; group B runs in this repository. Do not start group B task B6 before task A8 has produced a local package.

**Goal:** The station manager and the presenters he adds sign in to the web app with Google or GitHub; the server enforces, on every request (pages, the Blazor circuit, `/api/clip`, `/media/clip`), that a manager sees everything and a presenter sees only the shows assigned to them, with exactly the 5 minute margins, and nothing else; every access to a show is audited in UTC; the session is a secure, HttpOnly, SameSite cookie backed by a server-side session row; and the web container stays on 127.0.0.1 until the owner has proved sign-in works.

**Architecture:** Sign-in is a small new server-side package in PanoramicData.AuthMagic, `PanoramicData.AuthMagic.AspNetCore`, built on ASP.NET Core's own OpenID Connect handler (Google) and OAuth handler (GitHub) with PKCE, nonce and state, validated id tokens, a hardened cookie, antiforgery-protected sign-in and sign-out endpoints, a canonical-origin guard and an `IAuthMagicAccountGate` hook. Everything specific to this archive stays in this repository: a per-instance SQLite database (users, logins, sessions, assignments, audit), the allow-list gate (verified email, bound to the provider's account id on first use), roles `manager` and `presenter`, presenter filtering, clip grants for `/media/clip`, the audit log, export and import of the manager list, rate limits, security headers and data protection keys.

**Tech Stack:** .NET 10, ASP.NET Core and Blazor interactive server, `Microsoft.AspNetCore.Authentication.OpenIdConnect` 10.0.13 (inside the AuthMagic package), `Microsoft.Data.Sqlite` 10.0.13, xUnit 2 and `WebApplicationFactory` here; xUnit v3, `Microsoft.AspNetCore.TestHost` 10.0.13 and bUnit 2.11.3 upstream. A fake identity provider (Google discovery, signing keys and token endpoint; GitHub token, user and email endpoints) in both test suites, so no real credentials are needed in CI.

**Spec:** `docs/superpowers/specs/2026-10-15-webaudioarchive-design.md` sections 3.8 (accounts and access), 3.10 (two instances, per-instance accounts), 3.11 (`app.db`), 3.12 (personal data), 5 (deployment, secrets) and 6 (web and security tests). This plan changes the spec in three places, recorded in task B12: sign-in is Google **and GitHub**; the database is `/data/web/app.db` (the web container mounts `/data` read-only); settings names are listed in "Settings" below.

**Dry run (2026-10-16 UTC):** while this plan was written, its listings were compiled and their tests run in scratch copies, never in either repository. Group A: the new project built with that repository's analyzer settings (`AnalysisMode` All, SecurityCodeScan, IDisposableAnalyzers, XML documentation, warnings as errors) with 0 warnings, and its 82 tests passed. Group B: applied to a clone of `main` at `35df8a9` with the group A package from a local folder, it built with 0 warnings and all 367 test cases passed (the existing ones, the web tests now signed in as the manager, and the new ones). Two findings from the dry run are already in the plan: the gate runs in `OnTicketReceived`, not `OnTokenValidated` (A4), and the theme test reads the external script (B11). Expect differences only where other agents have changed the same files since.

## Plan series

| Plan | Delivers | Status |
|---|---|---|
| 1 | Continuous recorder, health, Docker, CI | done |
| Web MVP | Status, Shows, Recordings, Player, clip API (no sign-in, loopback only) | done (issue #24) |
| 4a (this) | Secure sign-in, roles, SQLite, assignments, presenter view, audit, export and import, hardening | this plan |
| 4b and later | Weekly report (#89), transcripts and search in the player, events | after the analysis plan |

## What PanoramicData.AuthMagic provides today

Checked on 2026-10-16 against the local checkout (`main` at `84029b2`) and nuget.org.

- **Packages.** The current package is `PanoramicData.AuthMagic.Blazor` **2.0.19** (published 2026-02-19); it carries the `PanoramicData.AuthMagic.Core` assembly inside it (Core is not packed on its own). `PanoramicData.AuthMagic` 1.0 and `PanoramicData.AuthMagic.Core` 1.0 on nuget.org are old packages unrelated to the 2.x code. The repository has git tags up to `2.0.82`, newer than anything on nuget.org, so the release pipeline must be checked before relying on a new release (risk R2).
- **Providers.** Microsoft (Entra ID; authorization code, PKCE in SPA mode, refresh tokens), GitHub (authorization code with a client secret; reads `/user` and `/user/orgs`) and Google (builds the authorization URL and exchanges the code, but turning the token response into a signed-in user is not implemented, so Google sign-in does not complete today).
- **Shape.** Designed for Blazor WebAssembly and circuit-side use. The provider redirects to a Blazor page (`/signin-google`, `/signin-github`) that exchanges the code in `OnInitializedAsync`; the provider's response is kept in browser `localStorage` (Blazored.LocalStorage) and read back by a circuit-scoped `AuthenticationStateProvider`, which builds the `ClaimsPrincipal` on demand. An `IsAuthenticatedUserPermittedAsync` callback can reject a user. Sign-in buttons are links whose `href` is the provider's authorization URL.
- **Conventions in that repository** (its `CLAUDE.md`, `CONTRIBUTING.md`, `.editorconfig`, `Directory.Build.props`, CI): work on a branch and open a pull request against `main`, never commit to `main`; `TreatWarningsAsErrors`, `AnalysisMode` All, SecurityCodeScan and IDisposableAnalyzers, and analyzers are never suppressed; XML documentation on every public member; file-scoped namespaces; tabs; `System.Text.Json`, not Newtonsoft, in new code; xUnit v3 on Microsoft.Testing.Platform; Codacy gate with zero new issues and coverage uploaded from `PanoramicData.AuthMagic.Test`; existing public members are not changed; versions by Nerdbank.GitVersioning (`version.json` is `2.0`); `Publish.ps1` is run only by the maintainer, only when he asks.

### Is it suitable for this app as it stands?

No, not for server-side trust decisions. What this app needs and the existing flow does not provide:

| Need | Existing AuthMagic | Needed here |
|---|---|---|
| Identity on plain HTTP requests (`/media/clip`, `/api/clip`, page loads) | Identity exists only inside the circuit; HTTP requests carry none | An ASP.NET Core authentication scheme with a cookie |
| Session storage | Provider response in browser `localStorage` | Encrypted, signed, HttpOnly, Secure, SameSite cookie holding only identifiers; tokens discarded |
| Google id token | Not turned into a user | Validated by the OpenID Connect handler: signature against Google's published keys, issuer, audience, expiry, nonce |
| PKCE | Microsoft SPA mode only | Every provider |
| GitHub email | Not read | Verified emails from `/user/emails` |
| Allow-list with roles | A yes or no callback | A gate that returns the archive's claims, plus per-request session revalidation |
| Sign-in start and sign-out | Links and a client-side storage clear | POST endpoints with antiforgery, local-only return URLs, server session ended |

The maintainer is sent a private note on the existing client-side providers, following the repository's `SECURITY.md` (owner action O1). It is not repeated in this public repository. Group A does not change any existing type or member; the existing providers keep working exactly as they do for their current consumers.

## Design: upstream versus app

**Upstream (new package `PanoramicData.AuthMagic.AspNetCore`, additive, version 2.1):** everything generic to "a server-side ASP.NET Core or Blazor Server app that signs people in with Google or GitHub and keeps a cookie session":

- `AddAuthMagicServer<TGate>(options)`: registers a cookie scheme and, when configured, Google (OpenID Connect: discovery, `code` flow, PKCE S256, nonce, state, `response_mode=query`, id token validated for signature, issuer `https://accounts.google.com` or `accounts.google.com`, audience equal to the client id, expiry with 2 minutes skew; `email_verified` read) and GitHub (OAuth: PKCE S256, state, scopes `read:user user:email`, numeric account id as subject, verified emails from `/user/emails`, primary first). Tokens are never saved (`SaveTokens = false`). Correlation and nonce cookies are HttpOnly and SameSite=Lax; Secure over HTTPS.
- `IAuthMagicAccountGate`: `AdmitAsync(account)` returns the app's claims or a refusal code; `ValidateSessionAsync(principal)` runs on every request and can replace or reject the principal; `SignedOutAsync(principal)`.
- Session cookie: `__Host-authmagic` (HTTPS) or `authmagic` (HTTP on localhost only), HttpOnly, SameSite=Lax, Path=/, no Domain, absolute lifetime, not sliding, not persistent; unauthenticated browser navigations redirect to the sign-in page, every other request gets 401 or 403.
- `MapAuthMagicServer()`: `POST /auth/signin/{google|github}` and `POST /auth/signout`, both validating the antiforgery token explicitly; the return URL is accepted only as a local path; any existing session is ended before a new sign-in starts (no session fixation).
- `UseAuthMagicCanonicalOrigin(exempt...)`: requests for another host or scheme are redirected (GET, HEAD) or refused with 421, so cookies, redirect URIs and the Origin check all agree.
- `AuthMagicSignInButtons` Razor component: one plain HTML form per enabled provider, with the antiforgery token and the sanitised return path.

**App (this repository):** everything that is about this archive: the SQLite store, allow-list rules, roles, assignments, presenter filtering, clip grants, audit, export and import, instance mode, bootstrap manager, rate limits, security headers, data protection, pages and docs.

**Why this split:** the upstream part is the security-critical protocol code, which belongs in the shared package so it is reviewed, tested and gated (Codacy) once and reused; it is written on top of the framework's vetted handlers rather than hand-rolled token code, so it is small (about 400 lines) and does not change any existing public member. The app part depends on this archive's data model, which a shared package must not know about. The gate interface is the seam: the package never sees users, roles or shows.

**Sequence (Google; GitHub is the same with its endpoints):**

```
Browser                         Web app (this repo + AuthMagic.AspNetCore)            Google
  | GET /shows/x  (no cookie)       |                                                    |
  |------------------------------->| fallback policy: 302 /signin?returnUrl=/shows/x    |
  | GET /signin                     | static SSR page, antiforgery cookie + form token   |
  | POST /auth/signin/google        | antiforgery ok, returnUrl local, old session ended |
  |<-------------------------------| 302 authorize?client_id&redirect_uri&state&nonce&code_challenge
  |----------------------------------------------------------------------------------->| sign in, consent
  |<-----------------------------------------------------------------------------------| 302 /signin-google?code&state
  | GET /signin-google?code&state   | correlation cookie matches state; POST token (code, verifier, secret)
  |                                 |--------------------------------------------------->| id_token
  |                                 | validate signature, iss, aud, exp, nonce; read sub, email, email_verified
  |                                 | gate.AdmitAsync -> allow-list, bind sub, session row, claims
  |<-------------------------------| Set-Cookie __Host-authmagic (only ids); 302 /shows/x
  | GET /shows/x (cookie)           | cookie -> ValidateSessionAsync (row, idle, user, role) -> page
```

## Decisions made

| # | Decision | Alternatives rejected, and why |
|---|---|---|
| D1 | Extend AuthMagic with a new server-side package built on ASP.NET Core's OpenID Connect and OAuth handlers. | (a) Use AuthMagic's existing providers: identity lives in the circuit and in browser storage, so `/media/clip` and `/api/clip` would have no identity, and the reasons in the private note (O1). (b) Fix the existing providers in place: would change behaviour of public members used by WebAssembly consumers, and a WebAssembly-shaped flow cannot set an HttpOnly cookie. (c) ASP.NET Core Identity with external logins and cookies: brings a user store, password hashing, account UI scaffolding and EF Core, most of which would be switched off because there are no local passwords; our allow-list plus server sessions is smaller, and it uses the same two handlers Identity uses underneath. (d) `Microsoft.AspNetCore.Authentication.Google`: plain OAuth 2 plus the userinfo endpoint, no signed id token or nonce; OpenID Connect is the stronger and standard choice. (e) `AspNet.Security.OAuth.GitHub`: a further third-party dependency for about 40 lines on the OAuth handler. (f) A separate proxy (oauth2-proxy) in front: another container, and the app must still authorise every request per show, so it does not remove the app work. |
| D2 | A new package id `PanoramicData.AuthMagic.AspNetCore`, versioned with the others (`version.json` 2.1). | Putting it into `PanoramicData.AuthMagic.Blazor` or Core: both declare the browser platform and are used by WebAssembly apps, which cannot reference `Microsoft.AspNetCore.App`. Reusing the old `PanoramicData.AuthMagic` id (1.0): anyone pinned to it would get unrelated content. The owner confirms the id (O3). |
| D3 | Server-side session rows in SQLite; the cookie carries only a random session id, the user id, provider and subject. | A stateless cookie only: sign-out could not end a copied cookie, idle timeout and revocation would wait for expiry. |
| D4 | Allow-list by **verified** email; on first sign-in the provider account (Google `sub`, GitHub numeric id) is bound to the person, and later sign-ins match on that binding. A different account presenting the same email later is refused (`account-changed`) until a manager resets the sign-in. | Email only: a recycled or reassigned address would take over the person's access. Subject only: the manager cannot know a `sub` in advance. |
| D5 | `Microsoft.Data.Sqlite` 10.0.13 with numbered SQL migration scripts and `PRAGMA user_version`. | EF Core: a larger dependency, design-time tooling and generated migrations for five small tables; plain SQL is easier to review and test. |
| D6 | The database and keys live in `${WAA_DATA_DIR}/web` on the host, mounted read-write at `/data/web` inside the read-only `/data` mount; `WAA_APP_DIR` defaults to `/data/web`. | `/data/app.db` as the spec says: `/data` is mounted read-only in the web container on purpose, so the web app can never change a recording, and SQLite needs a writable directory for its WAL files. A named volume: easy to lose on `docker compose down -v`, and kept apart from the data the owner backs up. Recorders mount `data/a` and `data/b` only, so they never see `data/web`. |
| D7 | Presenters get exactly the configured margin (5 minutes) either side; the margin control is shown to managers only; presenters get 404 (not 403) for anything not assigned to them; `/api/clip`, Status, Recordings and Player are manager-only. | Letting presenters choose margins: "including the 5 minute margins and nothing else". 403 for another show: confirms that it exists. |
| D8 | `/media/clip/{id}.mp3` is served to a presenter only for a clip id granted to that presenter when their show page made it (an in-memory grant for 24 hours, the cache age). | Persisted grants: the circuit dies with a restart anyway, and the page re-grants when reloaded. Signed URLs: a second secret and expiry handling for no gain over a session check. |
| D9 | Group B is built on a branch `plan-4a-secure-sign-in` in a sibling worktree `..\WebAudioArchive-4a`, merged into `main` (no pull request) in task B15, after the published package is pinned and the owner's checklist passes. | Committing straight to `main` (this repository's normal rule): `main` is what the owner rebuilds the live web container from, and other agents work there concurrently; an intermediate `main` would either be locked (sign-in required, no clients yet) or need a "sign-in off" switch, which is a security footgun. CI runs only on `main` and pull requests, so the branch builds against the local feed without a red CI. |
| D10 | Before the upstream release, the branch restores the package from a local NuGet folder registered in the owner's user-level NuGet config (never in git); B14 switches to the published version, pinned exactly, and removes the source. | A ProjectReference to the AuthMagic checkout: the AuthMagic repository is private, so a public repository must not reference its source, and CI could not build it. An app-side copy of the handler wiring: two copies of security code. |
| D11 | Sign-in, denied, sign-out and signed-out pages are static server-rendered (`[ExcludeFromInteractiveRouting]`); the Blazor hub requires a signed-in user. | Interactive sign-in pages: an anonymous visitor would get a circuit (a SignalR connection and server memory) for nothing. |
| D12 | Session cookie SameSite=Lax; antiforgery cookie SameSite=Strict; correlation and nonce cookies Lax (callbacks are GET because `response_mode=query`). | Strict session cookie: the first request after the provider's redirect is cross-site, so the user would look signed out until a reload. SameSite=None correlation cookies (the handler default for form_post): needs Secure, which plain `http://localhost` cannot always satisfy in every browser. |
| D13 | Idle timeout 4 hours, absolute session 24 hours, both settings; activity is any HTTP request or circuit event (touch at most once a minute). | Longer sessions: the archive holds personal data and signing in again is one click. Sliding cookies without a server row: no real idle timeout. |
| D14 | `INSTANCE_MODE` defaults to `manager-only` (the restrictive mode); `BOOTSTRAP_MANAGER_EMAIL` and `INSTANCE_MODE` keep the spec's names; every other new setting has the `WAA_` prefix. | Defaulting to `primary`: an unconfigured instance would accept presenters. Renaming the two spec settings: would make the spec, issue #92 and the docs disagree. |
| D15 | Google OAuth consent screen kept in **Testing** with the owner and each presenter added as test users. | Publishing to production: not needed for a handful of people; Testing adds a second allow-list at Google's side. GitHub has no equivalent, so the app's allow-list is what counts. |
| D16 | Secrets (client secrets) live **outside the working tree** in `%USERPROFILE%\.webaudioarchive\secrets\`, mounted read-only into the container (`/run/secrets/waa`); client ids in `docker/.env` (already ignored by git). | Secret files under `docker/`: an editor tool here may auto-commit the working tree, and other agents work in it; outside the tree nothing can commit them. Environment variables for secrets: visible in `docker inspect`. |
| D17 | The theme script moves from inline to `wwwroot/js/theme-boot.js` so the Content Security Policy has no `'unsafe-inline'` for scripts. | A per-request nonce: more moving parts; a hash: breaks whenever the script is edited. |
| D18 | Rate limits per client IP: 20 sign-in attempts a minute (start and callbacks), 30 clip preparations a minute, 1200 media range requests a minute; at most 2 clip builds at once (wait up to 20 s, then 503). `WAA_TRUSTED_PROXIES` names the ingress so the real client IP is used on the primary. | Per-user limits: sign-in happens before there is a user, and the limiter must run before the authentication middleware handles callbacks. |
| D19 | "Slot owner" in #1 is the `presenter` role. Editing rights (song details) come with song identification, not in this plan. | A third role now: nothing to edit yet. |
| D20 | Sign-out is a confirm page with a POST form (`/signout`), linked from the header. | A one-click POST from the interactive header: depends on the antiforgery token flowing into an interactive component; the confirm page is certain to carry it. |

## Security review

| Concern | How it is handled | Proved by |
|---|---|---|
| OAuth state | Generated and protected by the handler (data protection), bound to a per-attempt correlation cookie; single use (the cookie is deleted on callback). | A4 `Callback_TamperedOrMissingState_IsRefused`, `Callback_Replayed_IsRefused`; B6 `Replayed_callback_is_refused` |
| Nonce | OpenID Connect handler sends a nonce, stores it in a nonce cookie, `RequireNonce` true, validated against the id token. | A4 `Callback_WrongNonce_IsRefused`; B6 `Google_id_token_with_wrong_nonce_is_refused` |
| PKCE | S256 for Google and GitHub (`UsePkce = true`); the fake provider refuses a token request without the matching verifier. GitHub supports PKCE for OAuth apps; the state check still binds the flow if it ever ignored it. | A4 `Challenge_CarriesPkceNonceStateAndThePublicRedirectUri`, `TokenRequest_ProvesPkce`; A5 `Challenge_CarriesPkceStateAndEmailScope` |
| Redirect URI | Built from `WAA_PUBLIC_URL` (the canonical-origin guard makes the request host equal it), registered exactly at Google and GitHub, which reject any other. Callback paths are fixed: `/signin-google`, `/signin-github`. | A3 `CanonicalOrigin_OtherHost_IsRedirectedOrRefused`; B11 `Canonical_origin_redirects_other_hosts` |
| Id token validation | Signature against Google's JWKS from discovery, issuer in {`https://accounts.google.com`, `accounts.google.com`}, audience equal to the client id, `exp` required and checked (2 min skew), signed tokens required. | A4 `Callback_WrongIssuer_IsRefused`, `Callback_WrongAudience_IsRefused`, `Callback_ExpiredToken_IsRefused`, `Callback_TokenSignedByAnotherKey_IsRefused`; B6 `Google_id_token_for_another_audience_is_refused` |
| email_verified | Google: the email counts only when `email_verified` is true; GitHub: only emails marked verified by `/user/emails`. No verified email means `email-not-verified`. | A4 `Callback_UnverifiedEmail_IsNotPassedOn`; A5 `Callback_UsesNumericIdAndVerifiedEmailsPrimaryFirst`; B5 `AccountGateTests` |
| Allow-list | Only an existing, enabled user record admits; presenters refused on a manager-only instance; first use binds the provider account. | B5 `AccountGateTests` |
| Open redirect | Return URLs are local paths only: must start with one `/`, no `//`, no `\`, no control characters, at most 2048 characters; otherwise `/`. | A2 `Sanitise_KeepsOnlyLocalPaths` (theory); B6 `Open_redirect_is_not_possible` |
| Session fixation | A sign-in start ends any existing session (row and cookie); a new random session id per sign-in; the cookie handler issues a new ticket. | A3 `SignIn_WhenAlreadySignedIn_EndsTheOldSession`; B6 `A_new_sign_in_replaces_the_session_row` |
| Cookie flags | `__Host-authmagic`, HttpOnly, Secure, SameSite=Lax, Path=/, no Domain, no Expires (browser session), ticket expiry 24 h; antiforgery cookie `__Host-waa-af` Strict. Over loopback HTTP: no prefix and not Secure (traffic never leaves the machine). | A3 `SessionCookie_OverHttps_IsHostPrefixedSecureHttpOnlyLax`, `SessionCookie_OverLoopbackHttp_IsHttpOnlyLaxWithoutPrefix`; B11 `Cookies_are_hardened` |
| CSRF | Sign-in start and sign-out are POST with antiforgery tokens validated in the endpoint; SameSite=Lax session; an Origin check refuses cross-origin POSTs and cross-origin connections to `/_blazor`; Blazor SSR forms get antiforgery automatically; interactive events travel over the same-origin circuit. | A3 `SignIn_WithoutAntiforgeryToken_IsRejected`, `SignOut_WithoutAntiforgeryToken_IsRejected`; B11 `Cross_origin_posts_and_hub_connections_are_refused` |
| Logout | Deletes the session row (so a copied cookie stops working), deletes the cookie; the circuit is re-validated within a minute and drops to signed out. Provider sessions (Google, GitHub) are not ended, which the signed-out page says. | B6 `Sign_out_ends_the_session_everywhere` |
| Brute force | There are no passwords. Sign-in starts and callbacks are rate limited per IP (20 a minute); codes are single use at the provider and at the fake. | B11 `Sign_in_is_rate_limited` |
| Replay | State and correlation cookie single use; nonce single use; authorization code single use; PKCE binds the code to this browser; a stolen session cookie is limited by HttpOnly, Secure, idle and absolute timeouts, and sign-out or disable deletes the row. | A4, B6 replay tests |
| Token storage | `SaveTokens = false`: no access, id or refresh token is stored anywhere; the cookie holds only identifiers and is encrypted and signed with data protection keys. Nothing is written to browser storage. | A4 `Cookie_HoldsNoTokens` |
| Authorisation on every request | Fallback policy (signed in with a role) on every endpoint without an explicit policy, including the Blazor hub; page attributes plus `AuthorizeRouteView` for in-circuit navigation; data-level checks in `ShowAccess` and `ClipGrants`; revalidation of circuits every minute. | B7 `AuthorizationMatrixTests` |
| Logging | Never logs tokens, codes, secrets, cookies or provider responses; refusal reasons are codes; failures log the handler's message (IdentityModel hides PII by default). | Review in A8 and B15 (grep step) |
| Data protection keys | `/data/web/keys`, directory mode 0700 on Linux; optionally encrypted with a certificate (`WAA_DP_CERT_FILE`). | B11 `Keys_are_kept_in_the_app_directory` |
| Headers | CSP without inline scripts, `frame-ancestors 'none'`, nosniff, `Referrer-Policy: no-referrer` (clip ids never leak in a Referer), `X-Frame-Options: DENY`, Permissions-Policy, COOP, CORP, HSTS on HTTPS, `Cache-Control: no-store` on pages, API and media. | B11 `Security_headers_on_pages_api_and_media` |

### Threat model

Assets: the recordings (people's voices, possibly sensitive speech), transcripts later, the user list (emails), the audit log (emails, IP addresses), OAuth client secrets, data protection keys.

| Actor | Threat | Mitigation | Residual risk |
|---|---|---|---|
| Anonymous internet user (primary instance) | Reads audio or show titles | Deny by default on every endpoint and the hub; static assets only are anonymous; `/healthz` returns detail only to loopback callers | None known |
| Someone with a Google or GitHub account not on the list | Signs in | Gate refuses unless an enabled user with that verified email exists; Google Testing mode limits the Google side too | None known |
| Someone who controls an email that was on the list (recycled address) | Takes over a presenter | First-use binding to the provider account; a different account with the same email is refused | If the address is recycled before the real person ever signs in, the new holder can bind it. Managers add people just before they sign in |
| A presenter | Sees another show, its title, or more margin | 404 for unassigned show ids and clip ids; calendar filtered on the server; margin fixed; `/api/clip`, Recordings, Player, Status manager-only | Back-to-back shows: the 5 minute margins contain the neighbouring show's audio, by design of the spec |
| A malicious website the user visits | CSRF, cross-site WebSocket hijacking of the circuit, clickjacking | SameSite cookies, antiforgery, Origin check on POST and `/_blazor`, `frame-ancestors 'none'` | None known |
| Network attacker | Steals the cookie or code | HTTPS with HSTS on the primary; the secondary is loopback only; PKCE | A secondary reached over plain HTTP on anything but localhost is refused by configuration |
| Script injection (XSS) | Steals the session | HttpOnly cookie; CSP `script-src 'self'`; Blazor encodes output | `style-src 'unsafe-inline'` remains (Blazor and the component library set style attributes) |
| Stolen laptop or copied cookie | Replays the session | Idle 4 h, absolute 24 h, sign-out deletes the row, manager can end all of a user's sessions or disable them | Up to the timeouts if nobody notices |
| Leaked client secret | Impersonates the app to Google or GitHub | Secrets outside git and the working tree, mounted as files; rotate in the console (O7) | Requires the owner to rotate |
| Host compromise | Everything | Out of scope: whoever controls the host has the audio, the database and the keys | Accepted |
| A manager | Misuses access | Trusted by design; every access is audited, including managers' | Accepted |

## Global Constraints

- .NET 10, `net10.0`, `Nullable` enabled, `TreatWarningsAsErrors` true. Every package pinned to an exact version: `Microsoft.Data.Sqlite` **10.0.13**; `PanoramicData.AuthMagic.AspNetCore` the local prerelease from A8 until B14, then the published version, exactly; upstream: `Microsoft.AspNetCore.Authentication.OpenIdConnect` **10.0.13**, `Microsoft.AspNetCore.TestHost` **10.0.13**, `bunit` **2.11.3**.
- **All times UTC.** Stored as text `yyyy-MM-ddTHH:mm:ss.fffZ`; shown with a "UTC" label; the audit page, exports and logs are UTC.
- **No em dashes** anywhere: code, comments, docs, commit messages.
- **The repository is public.** No secrets, tokens, real email addresses, user data or exported user lists in git. Test accounts use the reserved `example.test` domain. Client secrets live outside the working tree (D16). Never paste a real client id or secret into a test, doc or commit message.
- **Never touch the recorders.** No change to `src/WebAudioArchive.Worker`, the recording code in `src/WebAudioArchive.Core`, `docker/Dockerfile` or the `recorder-a` and `recorder-b` services; never run `docker compose` without naming the `web` service; never stop or rebuild a recorder.
- **The web container stays on `127.0.0.1:8090`** in this plan. Exposure beyond loopback is documented (B12) and is a later, separate step.
- **Deny by default.** Every endpoint and page declares its policy or inherits the fallback policy; anything a presenter may not see answers 404 to them; no authorisation decision is made from a hidden link or from client-side state.
- **Never log** tokens, authorization codes, client secrets, cookies or provider responses.
- **Two repositories, two rule sets.** Group A follows PanoramicData.AuthMagic's rules (branch and pull request, Codacy zero new issues, analyzers never suppressed, existing public members unchanged, `Publish.ps1` only by the maintainer). Group B follows this repository's rules. No WebAudioArchive data goes into the company repository and no company code is copied into this one: the fake identity provider is written separately in each (Appendix F is the shared specification).
- No skipped tests. A test that cannot run fails with a message naming what is missing.
- Tests first: every step that adds behaviour writes the failing test, runs it and sees it fail for the stated reason, then implements.
- Group B: at the start of every task, `git -C ..\WebAudioArchive-4a rebase main` (resolve conflicts with the other agents' work by keeping both changes); commit after every task with `git add` of the named paths only, never `git add -A`; messages end with the attribution trailer the harness supplies.
- Do not use the PanoramicData shared skills or personas in this repository (the AuthMagic repository may use its own).

## Settings (environment variables of the web container)

| Name | Default | Meaning |
|---|---|---|
| `WAA_PUBLIC_URL` | required | What people type: `http://localhost:8090` (secondary) or `https://<host>` (primary). Plain `http` only for `localhost` or a loopback address. Scheme, host and port only. |
| `INSTANCE_MODE` | `manager-only` | `primary` (managers and presenters) or `manager-only`. |
| `BOOTSTRAP_MANAGER_EMAIL` | none | The manager's Google or GitHub email. Made, or restored as, an enabled manager at every start. Never first-come-first-served. |
| `WAA_GOOGLE_CLIENT_ID`, `WAA_GOOGLE_CLIENT_SECRET` or `WAA_GOOGLE_CLIENT_SECRET_FILE` | none | Google OAuth client. At least one provider is required. |
| `WAA_GITHUB_CLIENT_ID`, `WAA_GITHUB_CLIENT_SECRET` or `WAA_GITHUB_CLIENT_SECRET_FILE` | none | GitHub OAuth app. |
| `WAA_APP_DIR` | `/data/web` | Writable folder for `app.db`, `keys/` and `backups/`. |
| `WAA_SESSION_IDLE_MINUTES` | `240` | 5 to 1440. |
| `WAA_SESSION_MAX_HOURS` | `24` | 1 to 720, and not shorter than the idle timeout. |
| `WAA_TRUSTED_PROXIES` | none | Comma-separated IPs or CIDR networks of the reverse proxy (primary only). |
| `WAA_DP_CERT_FILE`, `WAA_DP_CERT_PASSWORD_FILE` | none | Optional PFX that encrypts the data protection keys at rest. |

## Review Focus

Input classes the spec implies but nothing else in the plan would exercise. Each has a test in the task that owns the code.

1. An id token that is well formed but wrong in exactly one way: issuer, audience, expiry, nonce, signature key (A4: `Callback_WrongIssuer_IsRefused`, `Callback_WrongAudience_IsRefused`, `Callback_ExpiredToken_IsRefused`, `Callback_WrongNonce_IsRefused`, `Callback_TokenSignedByAnotherKey_IsRefused`; B6 repeats two through the real app).
2. A provider account whose email is present but not verified, and a GitHub account with several emails where only a non-primary one is verified and on the list (A4 `Callback_UnverifiedEmail_IsNotPassedOn`, A5 `Callback_UsesNumericIdAndVerifiedEmailsPrimaryFirst`, B5 `A_verified_secondary_github_email_matches`).
3. Return URLs crafted to leave the site: `//evil.example`, `/\evil.example`, `/%09/evil.example` as typed and as decoded, `https://evil.example`, very long (A2 `Sanitise_KeepsOnlyLocalPaths`, `Sanitise_RefusesVeryLongPaths`; B6 `Open_redirect_is_not_possible`).
4. A presenter guessing: another show's id, a show that does not exist, another clip's 32 hex id, `?margin=60`, `/api/clip` for their own show's window (B7 `AuthorizationMatrixTests`, B8 `Presenter_margin_is_fixed`).
5. A session made invalid while a circuit is open: user disabled, role changed, signed out in another tab (B6 `Revalidation_drops_a_disabled_user`, `Sign_out_ends_the_session_everywhere`).
6. Configuration that would weaken security silently: `http` on a LAN address, a public URL with a path, a secret in both the variable and the file, a client id without a secret, `INSTANCE_MODE` typo (B2 `AuthSettingsTests`).
7. The same email on the list twice in an import, mixed case, and a presenter in an import on a manager-only instance (B10 `Import_never_removes_or_downgrades`).
8. A database written by a newer build (B3 `A_newer_database_is_refused_naming_the_versions`).
9. A request for this app on another host name (`127.0.0.1` when the public URL says `localhost`), which would otherwise store cookies for the wrong host and fail the correlation check (A3 `CanonicalOrigin_OtherHost_IsRedirectedOrRefused`, B11 `Canonical_origin_redirects_other_hosts`).

## Prerequisites and owner actions

The agent cannot create OAuth clients, publish packages or approve releases. These are the owner's steps, in the order they are needed. Times are UTC. Nothing here goes into git.

**O1. Read the private note on the existing AuthMagic providers** (sent in the agent's report, not in this repository) and decide whether to record it as a private security advisory on panoramicdata/PanoramicData.AuthMagic (Security tab, Report a vulnerability), as its `SECURITY.md` asks. Not blocking for this plan.

**O2. Before group A:** confirm that the agent may create a branch and open a pull request on panoramicdata/PanoramicData.AuthMagic, and that the maintainer will review it.

**O3. Package id and version:** confirm `PanoramicData.AuthMagic.AspNetCore` and `version.json` 2.1, and that the nuget.org API key used by the AuthMagic release workflow may push a **new** package id (keys can be scoped to named packages; a key scoped to `PanoramicData.AuthMagic.Blazor` only would fail).

**O4. Create the Google OAuth client** (one per instance; this one is for the manager's machine, the manager-only secondary). Use a personal Google account, not a company Workspace account, because the repository and archive are personal.

1. Open https://console.cloud.google.com/ and sign in.
2. Project picker (top bar) > **New project**. Name `webaudioarchive`. Organisation: **No organisation**. **Create**, then select the project.
3. Menu > **Google Auth Platform** (older consoles: APIs and Services > OAuth consent screen, which opens it) > **Get started**.
   - App information: App name `WebAudioArchive (home)`; User support email: your address. **Next**.
   - Audience: **External**. **Next**.
   - Contact information: your address. **Next**. Agree to the user data policy. **Create**.
4. **Data Access** > **Add or remove scopes**: tick `openid`, `.../auth/userinfo.email` and `.../auth/userinfo.profile` only. **Update**, **Save**. These are non-sensitive scopes; no verification is needed.
5. **Audience** > Publishing status: leave **Testing**. **Test users** > **Add users**: your Google address and each presenter's Google address. **Save**. (Only these accounts can get past Google while it is in Testing; the app's own list still decides.) Testing mode may show "Google hasn't verified this app": choose Continue.
6. **Clients** > **Create client**:
   - Application type: **Web application**. Name: `WebAudioArchive home`.
   - Authorised JavaScript origins: leave empty (none are needed).
   - Authorised redirect URIs: **Add URI** `http://localhost:8090/signin-google`. Exactly this: `localhost`, not `127.0.0.1`; no trailing slash.
   - **Create**. Copy the **Client ID** and the **Client secret** now (the console may show the secret only once; use **Download JSON** and keep that file outside the repository, or delete it after copying).
7. For the primary (cluster) later: a second client in the same project, name `WebAudioArchive cluster`, redirect URI `https://<primary host>/signin-google`, and its own pair of values on the cluster. Do not add the cluster URI to the home client.

**O5. Create the GitHub OAuth App** (one per instance).

1. https://github.com/settings/developers (your personal account) > **OAuth Apps** > **New OAuth App**. (An OAuth App, not a GitHub App.)
2. Application name `WebAudioArchive (home)`; Homepage URL `http://localhost:8090`; Application description optional; **Authorization callback URL** `http://localhost:8090/signin-github`. Leave **Enable Device Flow** unticked. **Register application**.
3. Copy the **Client ID**. **Generate a new client secret**, confirm, and copy it now (GitHub shows it once).
4. For the primary later: a separate OAuth App with callback `https://<primary host>/signin-github`.

**O6. Put the values where the app reads them** (never in git, never in the working tree for secrets):

```powershell
# Secrets: outside the repository, readable only by you.
$secrets = "$env:USERPROFILE\.webaudioarchive\secrets"
New-Item -ItemType Directory -Force $secrets | Out-Null
Set-Content -NoNewline -Path "$secrets\google_client_secret.txt" -Value '<paste the Google client secret>'
Set-Content -NoNewline -Path "$secrets\github_client_secret.txt" -Value '<paste the GitHub client secret>'
icacls $secrets /inheritance:r /grant:r "$($env:USERNAME):(OI)(CI)F" | Out-Null
```

Then add to `docker/.env` (beside the compose file; `.env` is already ignored by git, check with `git check-ignore -v docker/.env`):

```
WAA_PUBLIC_URL=http://localhost:8090
INSTANCE_MODE=manager-only
BOOTSTRAP_MANAGER_EMAIL=<your Google or GitHub email>
WAA_GOOGLE_CLIENT_ID=<the Google client id>.apps.googleusercontent.com
WAA_GITHUB_CLIENT_ID=<the GitHub client id>
WAA_SECRETS_DIR=C:/Users/<you>/.webaudioarchive/secrets
```

Create the app folder once: `New-Item -ItemType Directory -Force data\web` in the main checkout (on Linux also `sudo chown 1654:1654 data/web`). `git status` must show none of these files.

**O7. Rotation:** if a secret is ever exposed, create a new secret in the console (Google: Clients > the client > Add secret, then delete the old; GitHub: Generate a new client secret, then delete the old), replace the file, and run `docker compose -f docker/docker-compose.yml up -d web`.

**O8. Review and merge the AuthMagic pull request, then release** (maintainer only): merge on GitHub; on an up-to-date clean `main` run `.\Publish.ps1`; confirm `PanoramicData.AuthMagic.AspNetCore` and `PanoramicData.AuthMagic.Blazor` at the new version on nuget.org; tell the agent the exact version.

**O9. Run the manual checklist** (end of this plan) at B13 (trial before release) and at B15 (container after merge). B15 closes the issues only after it passes.

**O10. Decide the exposure of the primary** (#56) when the cluster deployment is planned: the documented option is an HTTPS ingress (B12). Nothing is exposed in this plan.

### How the agent tests without real credentials

- **Automated, in CI:** an in-memory fake identity provider (Appendix F) is the back-channel of the real handlers (`BackchannelHttpHandler`), answering Google discovery, the signing keys and the token endpoint, and GitHub's token, `/user` and `/user/emails` endpoints. The test plays the browser: it reads the app's redirect to the provider, checks PKCE, nonce, state and redirect URI, mints a single-use code, and follows the callback. Every negative case (wrong issuer, audience, expiry, nonce, key, state, verifier, unverified email, unlisted account, replay) is one test. Test client ids and secrets are obviously fake constants.
- **Manual, by the owner:** the checklist at the end, with the real clients, first against `dotnet run` on `http://localhost:8090` with the web container stopped (B13), then against the rebuilt container (B15).

## Issues

| Issue | Title (short) | Tasks | Closed when |
|---|---|---|---|
| #100 | Use PanoramicData.AuthMagic with Google and GitHub | A1 to A9, B6, B14 | B15 |
| #101 | OAuth clients: owner steps | O4 to O6, B2, B12, B13 | B15 (after O9) |
| #1 | Sign-in and roles (station manager, slot owner) | B4, B5, B6, B7, B9 | B15 closes it with a comment: roles, mapping and audit done; editing rights move to the song identification issue |
| #88 | SQLite store | B3 | B15 |
| #92 | Bootstrap manager and instance mode | B2, B4, B5 | B15 |
| #86 | Assign accounts to shows by title | B8 | B15 |
| #90 | Presenter view | B7, B8 | B15 |
| #87 | Audit log | B9 | B15 |
| #91 | Export and import of the manager list | B10 | B15 |
| #53 | Data protection keys at rest | B11 | B15 |
| #54 | Rate limits and concurrent clip builds | B11 | B15 |
| #55 | Security headers and CSRF | B11 | B15 |
| #56 | Exposure beyond loopback | B12 (decision documented) | B15, with the decision; cluster deployment is a later plan |
| #21 | Hardening (parent) | already closed; split into #53 to #56 | n/a |
| #89 | Weekly report | not in this plan (needs the analysis plan) | n/a |

Commit messages reference issues as `(#88)`, never `Closes #88`, so nothing closes before the owner's checklist passes.

---

## File structure

Group A (in `C:\Users\david\source\repos\panoramicdata\PanoramicData.AuthMagic-server`, a worktree of PanoramicData.AuthMagic on branch `feature/aspnetcore-server-signin`):

```
version.json                                                            2.0 -> 2.1
Directory.Packages.props                                                + OpenIdConnect, TestHost, bunit
PanoramicData.AuthMagic.slnx                                            + new project
README.md                                                               + "Server-side sign-in" section
PanoramicData.AuthMagic.AspNetCore/PanoramicData.AuthMagic.AspNetCore.csproj
PanoramicData.AuthMagic.AspNetCore/GlobalUsings.cs
PanoramicData.AuthMagic.AspNetCore/_Imports.razor
PanoramicData.AuthMagic.AspNetCore/AuthMagicServerDefaults.cs           scheme names, paths, endpoints, claim types
PanoramicData.AuthMagic.AspNetCore/AuthMagicServerOptions.cs            options + Validate
PanoramicData.AuthMagic.AspNetCore/AuthMagicServerProviderOptions.cs    client id and secret
PanoramicData.AuthMagic.AspNetCore/AuthMagicExternalAccount.cs          provider, subject, verified emails, name
PanoramicData.AuthMagic.AspNetCore/AuthMagicAdmission.cs                admit with claims, or refuse with a code
PanoramicData.AuthMagic.AspNetCore/IAuthMagicAccountGate.cs             admit, validate session, signed out
PanoramicData.AuthMagic.AspNetCore/AuthMagicServerServiceCollectionExtensions.cs   AddAuthMagicServer<TGate>
PanoramicData.AuthMagic.AspNetCore/AuthMagicServerApplicationBuilderExtensions.cs  UseAuthMagicCanonicalOrigin
PanoramicData.AuthMagic.AspNetCore/AuthMagicServerEndpointRouteBuilderExtensions.cs MapAuthMagicServer
PanoramicData.AuthMagic.AspNetCore/Internal/AuthMagicServerEvents.cs    handler events, principal, logging
PanoramicData.AuthMagic.AspNetCore/Internal/GitHubAccountReader.cs      /user and /user/emails
PanoramicData.AuthMagic.AspNetCore/Internal/LocalReturnUrl.cs           local path check
PanoramicData.AuthMagic.AspNetCore/Internal/AuthMagicRefusedException.cs
PanoramicData.AuthMagic.AspNetCore/Components/AuthMagicSignInButtons.razor (+ .razor.cs)
PanoramicData.AuthMagic.Test/PanoramicData.AuthMagic.Test.csproj        + references
PanoramicData.AuthMagic.Test/Server/FakeIdentityProvider.cs             Appendix F, in that repository's style
PanoramicData.AuthMagic.Test/Server/ServerTestHost.cs                   TestServer app with a recording gate
PanoramicData.AuthMagic.Test/Server/*Tests.cs
```

Group B (in `C:\Users\david\source\repos\davidnmbond\WebAudioArchive-4a`, branch `plan-4a-secure-sign-in`):

```
.gitignore                                                    + secrets and exported user lists
src/WebAudioArchive.Web/WebAudioArchive.Web.csproj            + AuthMagic.AspNetCore, Microsoft.Data.Sqlite, migrations
src/WebAudioArchive.Web/Program.cs                            settings, AddArchiveAccounts, UseArchiveSecurity, endpoint policies
src/WebAudioArchive.Web/Accounts/AuthSettings.cs              settings and validation
src/WebAudioArchive.Web/Accounts/Emails.cs                    email normalisation
src/WebAudioArchive.Web/Accounts/Sql.cs                       ADO.NET helpers, UTC text times
src/WebAudioArchive.Web/Accounts/AppDb.cs                     connections, migrations, backups
src/WebAudioArchive.Web/Accounts/Migrations/0001_initial.sql
src/WebAudioArchive.Web/Accounts/Roles.cs                     Role, ArchiveClaims, ArchivePolicies, Viewer, Actor
src/WebAudioArchive.Web/Accounts/AuditLog.cs
src/WebAudioArchive.Web/Accounts/UserStore.cs                 users, logins, bootstrap, export and import
src/WebAudioArchive.Web/Accounts/SessionStore.cs
src/WebAudioArchive.Web/Accounts/AccountGate.cs               allow-list decision
src/WebAudioArchive.Web/Accounts/ArchiveGate.cs               IAuthMagicAccountGate adapter (the only AuthMagic-aware type besides hosting)
src/WebAudioArchive.Web/Accounts/AssignmentStore.cs           assignments, ShowAccess
src/WebAudioArchive.Web/Accounts/ClipGrants.cs
src/WebAudioArchive.Web/Accounts/SessionRevalidator.cs        circuit revalidation and activity
src/WebAudioArchive.Web/Accounts/AccountsHosting.cs           services, pipeline, endpoints, housekeeping
src/WebAudioArchive.Web/Accounts/SecurityHeaders.cs           headers, Origin check
src/WebAudioArchive.Web/Clips/ClipBuildGate.cs                at most 2 builds at once
src/WebAudioArchive.Web/Components/App.razor                  render mode per page, external theme script
src/WebAudioArchive.Web/Components/Routes.razor               AuthorizeRouteView
src/WebAudioArchive.Web/Components/_Imports.razor             + authorization usings
src/WebAudioArchive.Web/Components/Layout/MainLayout.razor    nav by role, signed-in badge, sign-out link
src/WebAudioArchive.Web/Components/Shared/RedirectToSignIn.razor
src/WebAudioArchive.Web/Components/Pages/SignIn.razor, Denied.razor, SignOut.razor, SignedOut.razor
src/WebAudioArchive.Web/Components/Pages/Users.razor (+ .razor.cs), Assignments.razor (+ .razor.cs), Audit.razor (+ .razor.cs)
src/WebAudioArchive.Web/Components/Pages/*.razor              policy attributes; Shows and ShowPage filtered
src/WebAudioArchive.Web/wwwroot/js/theme-boot.js              the former inline script
tests/WebAudioArchive.Tests/Support/FakeIdentityProvider.cs   Appendix F
tests/WebAudioArchive.Tests/Support/TestSignIn.cs             host settings and the sign-in flow for tests
tests/WebAudioArchive.Tests/AuthSettingsTests.cs, AppDbTests.cs, UserStoreTests.cs, AccountGateTests.cs, SessionStoreTests.cs,
  SignInFlowTests.cs, AuthorizationMatrixTests.cs, PresenterViewTests.cs, AuditTests.cs, UserExportTests.cs, HardeningTests.cs
tests/WebAudioArchive.Tests/WebAppTests.cs, ShowsPageTests.cs sign in as the manager first
docker/docker-compose.yml                                     web: env, secrets, data/web mount (recorders unchanged)
docs/operations.md, README.md, docs/superpowers/specs/2026-10-15-webaudioarchive-design.md
```

Commands run from `C:\Users\david\source\repos\panoramicdata\PanoramicData.AuthMagic-server` (group A) or `C:\Users\david\source\repos\davidnmbond\WebAudioArchive-4a` (group B), PowerShell, unless a step says otherwise.

---
## Group A: PanoramicData.AuthMagic (company repository, its rules)

Everything in group A happens in a worktree of `panoramicdata/PanoramicData.AuthMagic` on a feature branch and ends in a pull request. Never commit to its `main`, never push to `main`, never run `Publish.ps1` or `Tag.ps1`, never suppress an analyzer (no `#pragma`, no `SuppressMessage`, no `NoWarn`, no `.editorconfig` severity change): fix the code instead, and if a rule truly cannot be met, stop and ask the maintainer. Do not change any existing public type or member. The listings below are indented with spaces for readability; the repository uses tabs and CRLF, so every task ends with `dotnet format whitespace` and `dotnet format style --verify-no-changes` on the new project.

### Task A1: Worktree, new project and packages

**Files:**
- Create: `PanoramicData.AuthMagic.AspNetCore/PanoramicData.AuthMagic.AspNetCore.csproj`, `GlobalUsings.cs`, `_Imports.razor`
- Modify: `Directory.Packages.props`, `PanoramicData.AuthMagic.slnx`, `PanoramicData.AuthMagic.Test/PanoramicData.AuthMagic.Test.csproj`

**Interfaces:**
- Produces: project `PanoramicData.AuthMagic.AspNetCore` (namespace `PanoramicData.AuthMagic.AspNetCore`), packable, internals visible to `PanoramicData.AuthMagic.Test`.

- [ ] **Step 1: Make the worktree (the main checkout is not touched)**

```powershell
cd C:\Users\david\source\repos\panoramicdata\PanoramicData.AuthMagic
git fetch origin
git worktree add ..\PanoramicData.AuthMagic-server -b feature/aspnetcore-server-signin origin/main
cd ..\PanoramicData.AuthMagic-server
dotnet build -c Release
dotnet test -c Release
```
Expected: build with 0 warnings and 0 errors; all existing tests pass. Record the test count. If the baseline is not clean, stop and report it to the owner; do not fix unrelated code in this branch.

- [ ] **Step 2: Pin the new packages centrally**

In `Directory.Packages.props`, inside the existing `<ItemGroup>`, add (alphabetical with the others):
```xml
    <PackageVersion Include="bunit" Version="2.11.3" />
    <PackageVersion Include="Microsoft.AspNetCore.Authentication.OpenIdConnect" Version="10.0.13" />
    <PackageVersion Include="Microsoft.AspNetCore.TestHost" Version="10.0.13" />
```

- [ ] **Step 3: Create the project**

`PanoramicData.AuthMagic.AspNetCore/PanoramicData.AuthMagic.AspNetCore.csproj`:
```xml
<?xml version="1.0" encoding="utf-8"?>
<Project Sdk="Microsoft.NET.Sdk.Razor">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <NeutralResourcesLanguage>en</NeutralResourcesLanguage>
    <GeneratePackageOnBuild>true</GeneratePackageOnBuild>
    <PackageId>PanoramicData.AuthMagic.AspNetCore</PackageId>
    <Description>Server-side sign-in for ASP.NET Core and Blazor Server: Google (OpenID Connect) and GitHub (OAuth) with PKCE, a hardened cookie session, antiforgery sign-in and sign-out endpoints and an account gate.</Description>
    <PackageReleaseNotes>New package: server-side Google and GitHub sign-in with cookie sessions.</PackageReleaseNotes>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <PackageProjectUrl>https://github.com/panoramicdata/PanoramicData.AuthMagic</PackageProjectUrl>
    <RepositoryUrl>https://github.com/panoramicdata/PanoramicData.AuthMagic</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <PackageIcon>Logo.png</PackageIcon>
  </PropertyGroup>
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
  </ItemGroup>
  <ItemGroup>
    <!--Analysers-->
    <PackageReference Include="IDisposableAnalyzers">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="SecurityCodeScan.VS2019">
      <PrivateAssets>all</PrivateAssets>
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
    </PackageReference>
    <PackageReference Include="Microsoft.AspNetCore.Authentication.OpenIdConnect" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="PanoramicData.AuthMagic.Test" />
  </ItemGroup>
  <ItemGroup>
    <None Include="..\README.md" Pack="true" PackagePath="\" />
    <None Include="..\PanoramicData.AuthMagic.Blazor\Logo.png" Pack="true" PackagePath="\" Link="Logo.png" />
  </ItemGroup>
</Project>
```

`PanoramicData.AuthMagic.AspNetCore/GlobalUsings.cs`:
```csharp
global using System.Globalization;
global using System.Net.Http.Headers;
global using System.Security.Claims;
global using System.Text.Json;
global using Microsoft.AspNetCore.Antiforgery;
global using Microsoft.AspNetCore.Authentication;
global using Microsoft.AspNetCore.Authentication.Cookies;
global using Microsoft.AspNetCore.Authentication.OAuth;
global using Microsoft.AspNetCore.Authentication.OpenIdConnect;
global using Microsoft.AspNetCore.Builder;
global using Microsoft.AspNetCore.Components;
global using Microsoft.AspNetCore.Http;
global using Microsoft.AspNetCore.Routing;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Logging;
global using Microsoft.IdentityModel.Protocols.OpenIdConnect;
global using Microsoft.IdentityModel.Tokens;
global using PanoramicData.AuthMagic.AspNetCore.Internal;
```

`PanoramicData.AuthMagic.AspNetCore/_Imports.razor`:
```razor
@using Microsoft.AspNetCore.Components.Forms
@using Microsoft.AspNetCore.Components.Web
@using PanoramicData.AuthMagic.AspNetCore
@using PanoramicData.AuthMagic.AspNetCore.Internal
```

Add to `PanoramicData.AuthMagic.slnx` after the Blazor project line:
```xml
  <Project Path="PanoramicData.AuthMagic.AspNetCore/PanoramicData.AuthMagic.AspNetCore.csproj" />
```

In `PanoramicData.AuthMagic.Test/PanoramicData.AuthMagic.Test.csproj` add:
```xml
  <ItemGroup>
    <FrameworkReference Include="Microsoft.AspNetCore.App" />
    <PackageReference Include="Microsoft.AspNetCore.TestHost" />
    <PackageReference Include="bunit" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\PanoramicData.AuthMagic.AspNetCore\PanoramicData.AuthMagic.AspNetCore.csproj" />
  </ItemGroup>
```
The new tests go in this existing test project (not a new one), because the CI coverage job builds only `PanoramicData.AuthMagic.Test`; a separate project would leave the new code uncovered on Codacy.

- [ ] **Step 4: Build**

```powershell
dotnet build -c Release
```
Expected: 0 warnings, 0 errors (the project is empty apart from usings; if an unused global using is reported, remove it). Commit:
```powershell
git add Directory.Packages.props PanoramicData.AuthMagic.slnx PanoramicData.AuthMagic.AspNetCore PanoramicData.AuthMagic.Test/PanoramicData.AuthMagic.Test.csproj
git commit -m "Add the PanoramicData.AuthMagic.AspNetCore project for server-side sign-in"
```

### Task A2: Options, account, admission and local return URLs

**Files:**
- Create: `AuthMagicServerDefaults.cs`, `AuthMagicServerOptions.cs`, `AuthMagicServerProviderOptions.cs`, `AuthMagicExternalAccount.cs`, `AuthMagicAdmission.cs`, `IAuthMagicAccountGate.cs`, `Internal/LocalReturnUrl.cs`, `Internal/AuthMagicRefusedException.cs`
- Test: `PanoramicData.AuthMagic.Test/Server/OptionsTests.cs`

**Interfaces:**
- Produces: the public contract used by every later task and by WebAudioArchive:
  - `AuthMagicServerDefaults` constants: `CookieScheme = "AuthMagic.Cookie"`, `GoogleScheme = "AuthMagic.Google"`, `GitHubScheme = "AuthMagic.GitHub"`, `ProviderGoogle = "google"`, `ProviderGitHub = "github"`, `GoogleCallbackPath = "/signin-google"`, `GitHubCallbackPath = "/signin-github"`, `ProviderClaim = "authmagic:provider"`, `SubjectClaim = "authmagic:subject"`.
  - `AuthMagicServerOptions { Uri? PublicOrigin; AuthMagicServerProviderOptions? Google, GitHub; string SignInPath = "/signin", DeniedPath = "/denied", SignedOutPath = "/signed-out", EndpointPrefix = "/auth"; TimeSpan SessionLifetime = 24 h; bool IsHttps; string CookieName; void Validate() }`
  - `AuthMagicServerProviderOptions { required string ClientId; required string ClientSecret }`
  - `record AuthMagicExternalAccount(string Provider, string Subject, IReadOnlyList<string> VerifiedEmails, string? DisplayName)`
  - `AuthMagicAdmission.Admit(IEnumerable<Claim>)`, `AuthMagicAdmission.Refuse(string code)`, `IsAdmitted`, `Claims`, `RefusalCode`
  - `IAuthMagicAccountGate { AdmitAsync, ValidateSessionAsync, SignedOutAsync }`

- [ ] **Step 1: Write the failing tests**

`PanoramicData.AuthMagic.Test/Server/OptionsTests.cs`:
```csharp
using PanoramicData.AuthMagic.AspNetCore;
using PanoramicData.AuthMagic.AspNetCore.Internal;

namespace PanoramicData.AuthMagic.Test.Server;

public class OptionsTests
{
    private static AuthMagicServerOptions Valid(string origin = "https://archive.example.test") => new()
    {
        PublicOrigin = new Uri(origin),
        Google = new AuthMagicServerProviderOptions { ClientId = "id", ClientSecret = "secret" },
    };

    [Fact]
    public void Validate_WithoutPublicOrigin_Throws()
    {
        var options = Valid();
        options.PublicOrigin = null;
        Assert.Contains("PublicOrigin", Assert.Throws<InvalidOperationException>(options.Validate).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://archive.example.test/app")]
    [InlineData("https://archive.example.test/?a=1")]
    [InlineData("https://user@archive.example.test")]
    public void Validate_WithPathQueryOrUserInfo_Throws(string origin)
        => Assert.Contains("scheme, host and optional port only", Assert.Throws<InvalidOperationException>(Valid(origin).Validate).Message, StringComparison.Ordinal);

    [Theory]
    [InlineData("https://archive.example.test", true)]
    [InlineData("http://localhost:8090", true)]
    [InlineData("http://127.0.0.1:8090", true)]
    [InlineData("http://[::1]:8090", true)]
    [InlineData("http://archive.example.test", false)]
    [InlineData("http://192.168.1.20:8090", false)]
    [InlineData("ftp://localhost", false)]
    public void Validate_AllowsHttpOnlyOnLoopback(string origin, bool valid)
    {
        var options = Valid(origin);
        if (valid)
        {
            options.Validate();
        }
        else
        {
            Assert.Throws<InvalidOperationException>(options.Validate);
        }
    }

    [Fact]
    public void Validate_WithoutAProvider_Throws()
    {
        var options = Valid();
        options.Google = null;
        Assert.Contains("Google, GitHub or both", Assert.Throws<InvalidOperationException>(options.Validate).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "secret")]
    [InlineData("id", " ")]
    public void Validate_WithoutClientIdOrSecret_Throws(string id, string secret)
    {
        var options = Valid();
        options.GitHub = new AuthMagicServerProviderOptions { ClientId = id, ClientSecret = secret };
        Assert.Contains("GitHub", Assert.Throws<InvalidOperationException>(options.Validate).Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("signin")]
    [InlineData("//evil.example.test")]
    [InlineData("https://evil.example.test/denied")]
    public void Validate_WithANonLocalPagePath_Throws(string path)
    {
        var options = Valid();
        options.DeniedPath = path;
        Assert.Contains("DeniedPath", Assert.Throws<InvalidOperationException>(options.Validate).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_WithASessionLifetimeOutsideOneMinuteToThirtyDays_Throws()
    {
        var options = Valid();
        options.SessionLifetime = TimeSpan.FromDays(31);
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("https://archive.example.test", "__Host-authmagic")]
    [InlineData("http://localhost:8090", "authmagic")]
    public void CookieName_HasTheHostPrefixOnlyOverHttps(string origin, string name)
        => Assert.Equal(name, Valid(origin).CookieName);

    [Theory]
    [InlineData("not-listed", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("Not-Listed", false)]
    [InlineData("not listed", false)]
    [InlineData("x<script>", false)]
    [InlineData("a-code-that-is-much-longer-than-forty-characters", false)]
    public void Refuse_AcceptsOnlyShortLowerCaseCodes(string code, bool valid)
    {
        if (valid)
        {
            Assert.Equal(code, AuthMagicAdmission.Refuse(code).RefusalCode);
        }
        else
        {
            Assert.Throws<ArgumentException>(() => AuthMagicAdmission.Refuse(code));
        }
    }

    [Fact]
    public void Admit_CopiesTheClaims()
    {
        var admission = AuthMagicAdmission.Admit([new Claim(ClaimTypes.Role, "manager")]);
        Assert.True(admission.IsAdmitted);
        Assert.Null(admission.RefusalCode);
        Assert.Equal("manager", Assert.Single(admission.Claims).Value);
    }

    [Theory]
    [InlineData(null, "/")]
    [InlineData("", "/")]
    [InlineData("/", "/")]
    [InlineData("/shows/burst-20261015T1900Z?margin=5", "/shows/burst-20261015T1900Z?margin=5")]
    [InlineData("//evil.example.test", "/")]
    [InlineData("/\\evil.example.test", "/")]
    [InlineData("/\t/evil.example.test", "/")]
    [InlineData("/ok\\..\\x", "/")]
    [InlineData("https://evil.example.test/", "/")]
    [InlineData("evil.example.test", "/")]
    [InlineData("/%2F%2Fevil.example.test", "/%2F%2Fevil.example.test")]
    public void Sanitise_KeepsOnlyLocalPaths(string? value, string expected)
        => Assert.Equal(expected, LocalReturnUrl.Sanitise(value));

    [Fact]
    public void Sanitise_RefusesVeryLongPaths()
        => Assert.Equal("/", LocalReturnUrl.Sanitise("/" + new string('a', 2048)));
}
```
(`/%2F%2Fevil...` stays as typed: it is a path on this site, never an authority, because the browser does not decode it before resolving.)

- [ ] **Step 2: Run them and see them fail**

```powershell
dotnet build -c Release PanoramicData.AuthMagic.Test
```
Expected: compile errors, the types do not exist.

- [ ] **Step 3: Implement**

`AuthMagicServerDefaults.cs`:
```csharp
namespace PanoramicData.AuthMagic.AspNetCore;

/// <summary>
/// Names and endpoints used by server-side sign-in.
/// </summary>
public static class AuthMagicServerDefaults
{
    /// <summary>The cookie authentication scheme that holds the session.</summary>
    public const string CookieScheme = "AuthMagic.Cookie";

    /// <summary>The OpenID Connect scheme for Google.</summary>
    public const string GoogleScheme = "AuthMagic.Google";

    /// <summary>The OAuth scheme for GitHub.</summary>
    public const string GitHubScheme = "AuthMagic.GitHub";

    /// <summary>The provider name for Google, as used in URLs and in <see cref="AuthMagicExternalAccount.Provider"/>.</summary>
    public const string ProviderGoogle = "google";

    /// <summary>The provider name for GitHub, as used in URLs and in <see cref="AuthMagicExternalAccount.Provider"/>.</summary>
    public const string ProviderGitHub = "github";

    /// <summary>The path Google redirects back to. Register PublicOrigin plus this path with Google.</summary>
    public const string GoogleCallbackPath = "/signin-google";

    /// <summary>The path GitHub redirects back to. Register PublicOrigin plus this path with GitHub.</summary>
    public const string GitHubCallbackPath = "/signin-github";

    /// <summary>Google's OpenID Connect issuer and discovery authority.</summary>
    public const string GoogleAuthority = "https://accounts.google.com";

    /// <summary>GitHub's authorization endpoint.</summary>
    public const string GitHubAuthorizationEndpoint = "https://github.com/login/oauth/authorize";

    /// <summary>GitHub's token endpoint.</summary>
    public const string GitHubTokenEndpoint = "https://github.com/login/oauth/access_token";

    /// <summary>GitHub's endpoint for the signed-in user.</summary>
    public const string GitHubUserEndpoint = "https://api.github.com/user";

    /// <summary>GitHub's endpoint for the signed-in user's email addresses.</summary>
    public const string GitHubEmailsEndpoint = "https://api.github.com/user/emails";

    /// <summary>The claim holding the provider name.</summary>
    public const string ProviderClaim = "authmagic:provider";

    /// <summary>The claim holding the provider's stable account identifier.</summary>
    public const string SubjectClaim = "authmagic:subject";
}
```

`AuthMagicServerProviderOptions.cs`:
```csharp
namespace PanoramicData.AuthMagic.AspNetCore;

/// <summary>
/// The OAuth client registered with a provider. The secret is never logged.
/// </summary>
public sealed class AuthMagicServerProviderOptions
{
    /// <summary>The client id issued by the provider.</summary>
    public required string ClientId { get; init; }

    /// <summary>The client secret issued by the provider.</summary>
    public required string ClientSecret { get; init; }

    internal void Validate(string provider)
    {
        if (string.IsNullOrWhiteSpace(ClientId) || string.IsNullOrWhiteSpace(ClientSecret))
        {
            throw new InvalidOperationException($"AuthMagic: {provider} needs both a ClientId and a ClientSecret.");
        }
    }
}
```

`AuthMagicServerOptions.cs`:
```csharp
namespace PanoramicData.AuthMagic.AspNetCore;

/// <summary>
/// Configuration for server-side sign-in with Google and GitHub.
/// </summary>
public sealed class AuthMagicServerOptions
{
    /// <summary>
    /// The scheme, host and port people use to reach the application, for example https://archive.example.org.
    /// Plain http is accepted only for a loopback host. Redirect URIs and cookie rules follow from it.
    /// </summary>
    public Uri? PublicOrigin { get; set; }

    /// <summary>Google credentials, or null to offer no Google sign-in.</summary>
    public AuthMagicServerProviderOptions? Google { get; set; }

    /// <summary>GitHub credentials, or null to offer no GitHub sign-in.</summary>
    public AuthMagicServerProviderOptions? GitHub { get; set; }

    /// <summary>The application's sign-in page; unauthenticated browser navigations are sent here.</summary>
    public string SignInPath { get; set; } = "/signin";

    /// <summary>The application's page for a refused or failed sign-in; it receives ?reason=code.</summary>
    public string DeniedPath { get; set; } = "/denied";

    /// <summary>Where the browser goes after signing out.</summary>
    public string SignedOutPath { get; set; } = "/signed-out";

    /// <summary>The prefix of the sign-in and sign-out endpoints.</summary>
    public string EndpointPrefix { get; set; } = "/auth";

    /// <summary>The absolute lifetime of a session cookie. The gate may end a session sooner.</summary>
    public TimeSpan SessionLifetime { get; set; } = TimeSpan.FromHours(24);

    /// <summary>True when <see cref="PublicOrigin"/> is HTTPS.</summary>
    public bool IsHttps => PublicOrigin?.Scheme == Uri.UriSchemeHttps;

    /// <summary>The session cookie name: __Host-authmagic over HTTPS, authmagic over loopback HTTP.</summary>
    public string CookieName => IsHttps ? "__Host-authmagic" : "authmagic";

    /// <summary>
    /// Checks the options and throws <see cref="InvalidOperationException"/> naming the first problem.
    /// </summary>
    public void Validate()
    {
        if (PublicOrigin is null || !PublicOrigin.IsAbsoluteUri)
        {
            throw new InvalidOperationException("AuthMagic: PublicOrigin must be an absolute URI such as https://archive.example.org.");
        }

        if (PublicOrigin.AbsolutePath != "/" || PublicOrigin.Query.Length > 0 || PublicOrigin.Fragment.Length > 0 || PublicOrigin.UserInfo.Length > 0)
        {
            throw new InvalidOperationException("AuthMagic: PublicOrigin must be a scheme, host and optional port only.");
        }

        var loopbackHttp = PublicOrigin.Scheme == Uri.UriSchemeHttp && PublicOrigin.IsLoopback;
        if (!IsHttps && !loopbackHttp)
        {
            throw new InvalidOperationException("AuthMagic: PublicOrigin must use https, or http on a loopback host only.");
        }

        if (Google is null && GitHub is null)
        {
            throw new InvalidOperationException("AuthMagic: configure Google, GitHub or both.");
        }

        Google?.Validate("Google");
        GitHub?.Validate("GitHub");
        RequireLocal(nameof(SignInPath), SignInPath);
        RequireLocal(nameof(DeniedPath), DeniedPath);
        RequireLocal(nameof(SignedOutPath), SignedOutPath);
        RequireLocal(nameof(EndpointPrefix), EndpointPrefix);
        if (SessionLifetime < TimeSpan.FromMinutes(1) || SessionLifetime > TimeSpan.FromDays(30))
        {
            throw new InvalidOperationException("AuthMagic: SessionLifetime must be between one minute and thirty days.");
        }
    }

    private static void RequireLocal(string name, string path)
    {
        if (LocalReturnUrl.Sanitise(path) != path || path.Contains('?', StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"AuthMagic: {name} must be a local path starting with a single /.");
        }
    }
}
```

`AuthMagicExternalAccount.cs`:
```csharp
namespace PanoramicData.AuthMagic.AspNetCore;

/// <summary>
/// An account the provider has authenticated, offered to the application's gate.
/// </summary>
/// <param name="Provider">"google" or "github".</param>
/// <param name="Subject">The provider's stable account id: Google's sub, GitHub's numeric user id.</param>
/// <param name="VerifiedEmails">Addresses the provider states are verified, primary first; empty when there are none.</param>
/// <param name="DisplayName">The account's display name, if any.</param>
public sealed record AuthMagicExternalAccount(string Provider, string Subject, IReadOnlyList<string> VerifiedEmails, string? DisplayName);
```

`AuthMagicAdmission.cs`:
```csharp
namespace PanoramicData.AuthMagic.AspNetCore;

/// <summary>
/// The gate's answer for an authenticated account: admit with the application's claims, or refuse with a code.
/// </summary>
public sealed class AuthMagicAdmission
{
    private AuthMagicAdmission(bool isAdmitted, IReadOnlyList<Claim> claims, string? refusalCode)
    {
        IsAdmitted = isAdmitted;
        Claims = claims;
        RefusalCode = refusalCode;
    }

    /// <summary>True when the account may sign in.</summary>
    public bool IsAdmitted { get; }

    /// <summary>The claims to put in the session, in addition to the provider and subject.</summary>
    public IReadOnlyList<Claim> Claims { get; }

    /// <summary>The refusal code passed to the denied page, or null when admitted.</summary>
    public string? RefusalCode { get; }

    /// <summary>Admits the account with these claims.</summary>
    /// <param name="claims">The application's claims.</param>
    /// <returns>An admission.</returns>
    public static AuthMagicAdmission Admit(IEnumerable<Claim> claims)
    {
        ArgumentNullException.ThrowIfNull(claims);
        return new AuthMagicAdmission(true, [.. claims], null);
    }

    /// <summary>Refuses the account. The code is shown to the user's browser in the denied page URL.</summary>
    /// <param name="refusalCode">1 to 40 lower-case letters, digits or dashes, for example not-listed.</param>
    /// <returns>A refusal.</returns>
    public static AuthMagicAdmission Refuse(string refusalCode)
    {
        if (!IsCode(refusalCode))
        {
            throw new ArgumentException("A refusal code is 1 to 40 lower-case letters, digits or dashes.", nameof(refusalCode));
        }

        return new AuthMagicAdmission(false, [], refusalCode);
    }

    internal static bool IsCode(string? value)
        => value is { Length: > 0 and <= 40 } && value.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');
}
```

`IAuthMagicAccountGate.cs`:
```csharp
namespace PanoramicData.AuthMagic.AspNetCore;

/// <summary>
/// The application's decisions about accounts and sessions. Registered by AddAuthMagicServer as a scoped service.
/// </summary>
public interface IAuthMagicAccountGate
{
    /// <summary>
    /// Called once per sign-in, after the provider has authenticated the account and its token has been validated.
    /// </summary>
    /// <param name="account">The authenticated account.</param>
    /// <param name="httpContext">The callback request.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>Admit with the session's claims, or refuse with a code.</returns>
    Task<AuthMagicAdmission> AdmitAsync(AuthMagicExternalAccount account, HttpContext httpContext, CancellationToken cancellationToken);

    /// <summary>
    /// Called on every request that carries a session cookie. Return the principal to use (the same one or a rebuilt
    /// one, for example with the current roles), or null to end the session.
    /// </summary>
    /// <param name="principal">The principal from the cookie.</param>
    /// <param name="httpContext">The request.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The principal to use, or null.</returns>
    Task<ClaimsPrincipal?> ValidateSessionAsync(ClaimsPrincipal principal, HttpContext httpContext, CancellationToken cancellationToken);

    /// <summary>
    /// Called when a session ends by sign-out or because a new sign-in starts in the same browser.
    /// </summary>
    /// <param name="principal">The principal of the session that ends.</param>
    /// <param name="httpContext">The request.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>A task.</returns>
    Task SignedOutAsync(ClaimsPrincipal principal, HttpContext httpContext, CancellationToken cancellationToken);
}
```

`Internal/LocalReturnUrl.cs`:
```csharp
namespace PanoramicData.AuthMagic.AspNetCore.Internal;

/// <summary>
/// Keeps a return URL only when it is a path on this site; anything else becomes "/".
/// </summary>
internal static class LocalReturnUrl
{
    internal const int MaxLength = 2048;

    internal static string Sanitise(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > MaxLength || value[0] != '/')
        {
            return "/";
        }

        if (value.Length > 1 && (value[1] == '/' || value[1] == '\\'))
        {
            return "/";
        }

        // Browsers strip tabs and newlines and treat \ as /, so any of them could turn the path into //host.
        return value.Any(c => c == '\\' || char.IsControl(c)) ? "/" : value;
    }
}
```

`Internal/AuthMagicRefusedException.cs`:
```csharp
namespace PanoramicData.AuthMagic.AspNetCore.Internal;

/// <summary>
/// Carries a refusal code out of a handler event, so the remote failure event can send it to the denied page.
/// </summary>
internal sealed class AuthMagicRefusedException : Exception
{
    public AuthMagicRefusedException()
        : this("failed")
    {
    }

    public AuthMagicRefusedException(string code)
        : base("Sign-in refused: " + code)
    {
        Code = AuthMagicAdmission.IsCode(code) ? code : "failed";
    }

    public AuthMagicRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = "failed";
    }

    public string Code { get; }
}
```
(The three constructors satisfy CA1032; the class is internal so it is not part of the public surface.)

- [ ] **Step 4: Run the tests**

```powershell
dotnet test -c Release --filter-class PanoramicData.AuthMagic.Test.Server.OptionsTests
```
(On Microsoft.Testing.Platform with xUnit v3 the filter is `--filter-class`; if this SDK prints that the option is unknown, run `dotnet test -c Release -- --filter-class PanoramicData.AuthMagic.Test.Server.OptionsTests`.)
Expected: all pass. Then `dotnet format whitespace PanoramicData.AuthMagic.AspNetCore` and `dotnet format whitespace PanoramicData.AuthMagic.Test`, `dotnet build -c Release` with 0 warnings.

- [ ] **Step 5: Commit**

```powershell
git add PanoramicData.AuthMagic.AspNetCore PanoramicData.AuthMagic.Test/Server/OptionsTests.cs
git commit -m "Server sign-in: options, account, admission, gate contract and local return URLs"
```

### Task A3: Cookie session, gate hooks, sign-in and sign-out endpoints, canonical origin

**Files:**
- Create: `AuthMagicServerServiceCollectionExtensions.cs`, `AuthMagicServerApplicationBuilderExtensions.cs`, `AuthMagicServerEndpointRouteBuilderExtensions.cs`, `Internal/AuthMagicServerEvents.cs`
- Test: `PanoramicData.AuthMagic.Test/Server/FakeIdentityProvider.cs` (Appendix F, written in this repository's style), `Server/ServerTestHost.cs`, `Server/SessionTests.cs`

**Interfaces:**
- Consumes: A2.
- Produces: `IServiceCollection.AddAuthMagicServer<TGate>(Action<AuthMagicServerOptions>) : AuthenticationBuilder`; `IApplicationBuilder.UseAuthMagicCanonicalOrigin(params string[] exemptPaths)`; `IEndpointRouteBuilder.MapAuthMagicServer() : IEndpointConventionBuilder`. Order the app must use: `UseAuthMagicCanonicalOrigin`, `UseRouting`, `UseAuthentication`, `UseAuthorization`, `UseAntiforgery`, then endpoints.

- [ ] **Step 1: Write the test host**

`PanoramicData.AuthMagic.Test/Server/ServerTestHost.cs`:
```csharp
using System.Collections.Concurrent;
using System.Net;
using System.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PanoramicData.AuthMagic.AspNetCore;

namespace PanoramicData.AuthMagic.Test.Server;

/// <summary>What the test gate answers, and what it was asked.</summary>
public sealed class GateScript
{
    public Func<AuthMagicExternalAccount, AuthMagicAdmission> Admit { get; set; }
        = account => AuthMagicAdmission.Admit([new Claim(ClaimTypes.Name, account.DisplayName ?? account.Subject), new Claim(ClaimTypes.Role, "member")]);

    public Func<ClaimsPrincipal, ClaimsPrincipal?> Validate { get; set; } = principal => principal;

    public ConcurrentQueue<AuthMagicExternalAccount> Admitted { get; } = new();

    public int SignedOutCount;
}

public sealed class RecordingGate(GateScript script) : IAuthMagicAccountGate
{
    public Task<AuthMagicAdmission> AdmitAsync(AuthMagicExternalAccount account, HttpContext httpContext, CancellationToken cancellationToken)
    {
        script.Admitted.Enqueue(account);
        return Task.FromResult(script.Admit(account));
    }

    public Task<ClaimsPrincipal?> ValidateSessionAsync(ClaimsPrincipal principal, HttpContext httpContext, CancellationToken cancellationToken)
        => Task.FromResult(script.Validate(principal));

    public Task SignedOutAsync(ClaimsPrincipal principal, HttpContext httpContext, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref script.SignedOutCount);
        return Task.CompletedTask;
    }
}

/// <summary>Sends and stores cookies like a browser would, over a TestServer handler.</summary>
internal sealed class CookieHandler(CookieContainer cookies) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var header = cookies.GetCookieHeader(request.RequestUri!);
        if (header.Length > 0)
        {
            request.Headers.Add("Cookie", header);
        }

        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.Headers.TryGetValues("Set-Cookie", out var values))
        {
            foreach (var value in values)
            {
                cookies.SetCookies(request.RequestUri!, value);
            }
        }

        return response;
    }
}

/// <summary>A minimal app using the package: the sign-in endpoints, a protected /me and a /token helper.</summary>
public sealed class ServerTestHost : IAsyncDisposable
{
    private WebApplication? _app;

    public FakeIdentityProvider Idp { get; } = new();

    public GateScript Gate { get; } = new();

    public CookieContainer Cookies { get; } = new();

    public Uri Origin { get; private set; } = null!;

    public HttpClient Client { get; private set; } = null!;

    public static async Task<ServerTestHost> StartAsync(string origin = "https://localhost", bool google = true, bool github = true)
    {
        var host = new ServerTestHost { Origin = new Uri(origin) };
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(host.Gate);
        builder.Services.AddAntiforgery();
        builder.Services.AddAuthorization();
        builder.Services.AddAuthMagicServer<RecordingGate>(o =>
        {
            o.PublicOrigin = host.Origin;
            o.Google = google ? new AuthMagicServerProviderOptions { ClientId = FakeIdentityProvider.GoogleClientId, ClientSecret = FakeIdentityProvider.GoogleClientSecret } : null;
            o.GitHub = github ? new AuthMagicServerProviderOptions { ClientId = FakeIdentityProvider.GitHubClientId, ClientSecret = FakeIdentityProvider.GitHubClientSecret } : null;
        });

        // Configure (not PostConfigure): the handlers' own post-configuration builds the back-channel from this handler.
        builder.Services.Configure<OpenIdConnectOptions>(AuthMagicServerDefaults.GoogleScheme, o => o.BackchannelHttpHandler = host.Idp);
        builder.Services.Configure<OAuthOptions>(AuthMagicServerDefaults.GitHubScheme, o => o.BackchannelHttpHandler = host.Idp);

        var app = builder.Build();
        app.UseAuthMagicCanonicalOrigin("/healthz");
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
        app.MapAuthMagicServer();
        app.MapGet("/healthz", () => "ok");
        app.MapGet("/token", (HttpContext context, IAntiforgery antiforgery) => antiforgery.GetAndStoreTokens(context).RequestToken);
        app.MapGet("/me", (ClaimsPrincipal user) => Results.Json(user.Claims.ToDictionary(c => c.Type, c => c.Value))).RequireAuthorization();
        await app.StartAsync().ConfigureAwait(false);
        host._app = app;
        host.Client = new HttpClient(new CookieHandler(host.Cookies) { InnerHandler = app.GetTestServer().CreateHandler() }) { BaseAddress = host.Origin };
        return host;
    }

    public async Task<HttpResponseMessage> PostAsync(string path, string? returnUrl, bool withToken = true)
    {
        var fields = new List<KeyValuePair<string, string>>();
        if (withToken)
        {
            fields.Add(new("__RequestVerificationToken", await Client.GetStringAsync(new Uri("/token", UriKind.Relative)).ConfigureAwait(false)));
        }

        if (returnUrl is not null)
        {
            fields.Add(new("returnUrl", returnUrl));
        }

        using var content = new FormUrlEncodedContent(fields);
        return await Client.PostAsync(new Uri(path, UriKind.Relative), content).ConfigureAwait(false);
    }

    public Task<HttpResponseMessage> PostSignInAsync(string provider, string? returnUrl = "/") => PostAsync($"/auth/signin/{provider}", returnUrl);

    /// <summary>The whole flow: start, "sign in" at the fake provider, follow the callback. Returns the callback response.</summary>
    public async Task<HttpResponseMessage> CompleteAsync(string provider, FakeAccount account, Tamper? tamper = null, string returnUrl = "/")
    {
        using var challenge = await PostSignInAsync(provider, returnUrl).ConfigureAwait(false);
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var callback = Idp.Authorize(challenge.Headers.Location!, account, tamper);
        return await Client.GetAsync(callback).ConfigureAwait(false);
    }

    public async Task<Dictionary<string, string>?> MeAsync()
    {
        using var response = await Client.GetAsync(new Uri("/me", UriKind.Relative)).ConfigureAwait(false);
        return response.StatusCode == HttpStatusCode.OK
            ? JsonSerializer.Deserialize<Dictionary<string, string>>(await response.Content.ReadAsStringAsync().ConfigureAwait(false))
            : null;
    }

    public Cookie? SessionCookie() => Cookies.GetAllCookies().FirstOrDefault(c => c.Name.EndsWith("authmagic", StringComparison.Ordinal));

    public static Dictionary<string, string> Query(Uri uri)
    {
        var parsed = HttpUtility.ParseQueryString(uri.Query);
        return parsed.AllKeys.OfType<string>().ToDictionary(k => k, k => parsed[k]!);
    }

    public async ValueTask DisposeAsync()
    {
        Client?.Dispose();
        Idp.Dispose();
        if (_app is not null)
        {
            await _app.DisposeAsync().ConfigureAwait(false);
        }
    }
}
```
Write `FakeIdentityProvider.cs` from Appendix F in this repository's namespace (`PanoramicData.AuthMagic.Test.Server`) and style.

- [ ] **Step 2: Write the failing tests**

`PanoramicData.AuthMagic.Test/Server/SessionTests.cs`:
```csharp
using System.Net;
using PanoramicData.AuthMagic.AspNetCore;

namespace PanoramicData.AuthMagic.Test.Server;

public class SessionTests
{
    private static readonly FakeAccount Ada = new("google-sub-ada", "ada@example.test", true, "Ada");

    [Fact]
    public async Task SignIn_WithoutAntiforgeryToken_IsRejected()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var response = await host.PostAsync("/auth/signin/google", "/", withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SignOut_WithoutAntiforgeryToken_IsRejected()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var _ = await host.CompleteAsync("google", Ada);
        using var response = await host.PostAsync("/auth/signout", null, withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.NotNull(await host.MeAsync());
    }

    [Fact]
    public async Task SignOut_EndsTheSessionAndTellsTheGate()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var _ = await host.CompleteAsync("google", Ada);
        using var response = await host.PostAsync("/auth/signout", null);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/signed-out", response.Headers.Location!.OriginalString);
        Assert.Equal(1, host.Gate.SignedOutCount);
        Assert.Null(await host.MeAsync());
    }

    [Fact]
    public async Task SignIn_UnknownProvider_Returns404()
    {
        await using var host = await ServerTestHost.StartAsync(github: false);
        using var response = await host.PostSignInAsync("github");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task SignIn_WhenAlreadySignedIn_EndsTheOldSession()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var first = await host.CompleteAsync("google", Ada);
        using var second = await host.PostSignInAsync("google");
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.Equal(1, host.Gate.SignedOutCount);
        Assert.Null(await host.MeAsync());
    }

    [Fact]
    public async Task SessionCookie_OverHttps_IsHostPrefixedSecureHttpOnlyLax()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var back = await host.CompleteAsync("google", Ada);
        var header = Assert.Single(back.Headers.GetValues("Set-Cookie"), v => v.StartsWith("__Host-authmagic=", StringComparison.Ordinal));
        Assert.Contains("; path=/", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; secure", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=lax", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("domain=", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("expires=", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SessionCookie_OverLoopbackHttp_IsHttpOnlyLaxWithoutPrefix()
    {
        await using var host = await ServerTestHost.StartAsync("http://localhost:8090");
        using var back = await host.CompleteAsync("google", Ada);
        var header = Assert.Single(back.Headers.GetValues("Set-Cookie"), v => v.StartsWith("authmagic=", StringComparison.Ordinal));
        Assert.Contains("; httponly", header, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("; samesite=lax", header, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("; secure", header, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ProtectedEndpoint_ForABrowser_RedirectsToSignIn()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var request = new HttpRequestMessage(HttpMethod.Get, "/me?x=1");
        request.Headers.Accept.ParseAdd("text/html");
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://localhost/signin?returnUrl=%2Fme%3Fx%3D1", response.Headers.Location!.AbsoluteUri);
    }

    [Fact]
    public async Task ProtectedEndpoint_ForAScript_Returns401()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var response = await host.Client.GetAsync(new Uri("/me", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task ValidateSession_ReturningNull_SignsOut()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var _ = await host.CompleteAsync("google", Ada);
        host.Gate.Validate = _ => null;
        Assert.Null(await host.MeAsync());
        host.Gate.Validate = p => p;
        Assert.Null(await host.MeAsync()); // the cookie was deleted, not just ignored once
    }

    [Fact]
    public async Task ValidateSession_Replacement_IsUsed()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var _ = await host.CompleteAsync("google", Ada);
        host.Gate.Validate = p => new ClaimsPrincipal(new ClaimsIdentity([.. p.Claims.Where(c => c.Type != ClaimTypes.Role), new Claim(ClaimTypes.Role, "manager")], AuthMagicServerDefaults.CookieScheme));
        Assert.Equal("manager", (await host.MeAsync())![ClaimTypes.Role]);
    }

    [Fact]
    public async Task CanonicalOrigin_OtherHost_IsRedirectedOrRefused()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var get = await host.Client.GetAsync(new Uri("https://127.0.0.1/me?x=1"));
        Assert.Equal(HttpStatusCode.TemporaryRedirect, get.StatusCode);
        Assert.Equal("https://localhost/me?x=1", get.Headers.Location!.AbsoluteUri);
        using var content = new FormUrlEncodedContent([]);
        using var post = await host.Client.PostAsync(new Uri("http://localhost/auth/signout"), content);
        Assert.Equal(HttpStatusCode.MisdirectedRequest, post.StatusCode);
        using var exempt = await host.Client.GetAsync(new Uri("http://127.0.0.1:8080/healthz"));
        Assert.Equal(HttpStatusCode.OK, exempt.StatusCode);
    }
}
```
These use a Google sign-in only to obtain a session, so the tests that call `CompleteAsync` pass once A4 is done; until then they fail (they are never skipped). The others must pass in this task.

- [ ] **Step 3: Run them and see them fail**

```powershell
dotnet build -c Release PanoramicData.AuthMagic.Test
```
Expected: compile errors for `AddAuthMagicServer`, `UseAuthMagicCanonicalOrigin`, `MapAuthMagicServer`.

- [ ] **Step 4: Implement**

`AuthMagicServerServiceCollectionExtensions.cs`:
```csharp
namespace PanoramicData.AuthMagic.AspNetCore;

/// <summary>
/// Registers server-side sign-in.
/// </summary>
public static class AuthMagicServerServiceCollectionExtensions
{
    /// <summary>
    /// Adds the cookie session and the configured providers, and registers <typeparamref name="TGate"/> as the
    /// scoped <see cref="IAuthMagicAccountGate"/>.
    /// </summary>
    /// <typeparam name="TGate">The application's gate.</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the options; they are validated before anything is registered.</param>
    /// <returns>The authentication builder, for further schemes.</returns>
    public static AuthenticationBuilder AddAuthMagicServer<TGate>(this IServiceCollection services, Action<AuthMagicServerOptions> configure)
        where TGate : class, IAuthMagicAccountGate
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var options = new AuthMagicServerOptions();
        configure(options);
        options.Validate();

        _ = services.AddSingleton(options);
        _ = services.AddScoped<IAuthMagicAccountGate, TGate>();
        _ = services.AddAntiforgery();

        var builder = services.AddAuthentication(o =>
        {
            o.DefaultScheme = AuthMagicServerDefaults.CookieScheme;
            o.DefaultSignInScheme = AuthMagicServerDefaults.CookieScheme;
            o.DefaultChallengeScheme = AuthMagicServerDefaults.CookieScheme;
        });

        _ = builder.AddCookie(AuthMagicServerDefaults.CookieScheme, o => ConfigureCookie(o, options));
        if (options.Google is { } google)
        {
            _ = builder.AddOpenIdConnect(AuthMagicServerDefaults.GoogleScheme, "Google", o => AuthMagicServerEvents.ConfigureGoogle(o, google, options));
        }

        if (options.GitHub is { } github)
        {
            _ = builder.AddOAuth(AuthMagicServerDefaults.GitHubScheme, "GitHub", o => AuthMagicServerEvents.ConfigureGitHub(o, github, options));
        }

        return builder;
    }

    private static void ConfigureCookie(CookieAuthenticationOptions o, AuthMagicServerOptions options)
    {
        o.Cookie.Name = options.CookieName;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = options.IsHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.Cookie.Path = "/";
        o.Cookie.IsEssential = true;
        o.ExpireTimeSpan = options.SessionLifetime;
        o.SlidingExpiration = false;
        o.LoginPath = options.SignInPath;
        o.AccessDeniedPath = options.DeniedPath;
        o.ReturnUrlParameter = "returnUrl";
        o.Events.OnValidatePrincipal = AuthMagicServerEvents.ValidatePrincipalAsync;
        o.Events.OnRedirectToLogin = context => AuthMagicServerEvents.RedirectOrStatusAsync(context, StatusCodes.Status401Unauthorized);
        o.Events.OnRedirectToAccessDenied = context => AuthMagicServerEvents.RedirectOrStatusAsync(context, StatusCodes.Status403Forbidden);
    }
}
```
(`IsPersistent` stays false, so the cookie has no `Expires`; the ticket inside expires after `SessionLifetime`.)

`Internal/AuthMagicServerEvents.cs` (the Google and GitHub parts are added in A4 and A5; this task adds the shared part):
```csharp
namespace PanoramicData.AuthMagic.AspNetCore.Internal;

/// <summary>
/// Handler configuration and events shared by the providers.
/// </summary>
internal static partial class AuthMagicServerEvents
{
    internal const string LoggerCategory = "PanoramicData.AuthMagic.AspNetCore";

    internal static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var gate = context.HttpContext.RequestServices.GetRequiredService<IAuthMagicAccountGate>();
        var replacement = context.Principal is null
            ? null
            : await gate.ValidateSessionAsync(context.Principal, context.HttpContext, context.HttpContext.RequestAborted).ConfigureAwait(false);
        if (replacement is null)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(AuthMagicServerDefaults.CookieScheme).ConfigureAwait(false);
            return;
        }

        context.ReplacePrincipal(replacement);
    }

    internal static Task RedirectOrStatusAsync(RedirectContext<CookieAuthenticationOptions> context, int statusCode)
    {
        // Only a browser navigating to a page is redirected; scripts, media elements and APIs get the status code.
        var request = context.Request;
        var navigation = HttpMethods.IsGet(request.Method)
            && request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase);
        if (navigation)
        {
            context.Response.Redirect(context.RedirectUri);
        }
        else
        {
            context.Response.StatusCode = statusCode;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Runs the gate in OnTicketReceived, after the handler has finished every check of the provider's answer. For
    /// Google that matters: ASP.NET Core validates the nonce and the rest of the protocol after OnTokenValidated, so a
    /// gate called there would see accounts from answers that are then rejected (and create sessions for them).
    /// </summary>
    internal static async Task TicketReceivedAsync(TicketReceivedContext context, AuthMagicServerOptions options, Func<ClaimsPrincipal, AuthMagicExternalAccount?> read)
    {
        var account = context.Principal is null ? null : read(context.Principal);
        if (account is null)
        {
            Deny(context, options, "failed");
            return;
        }

        var gate = context.HttpContext.RequestServices.GetRequiredService<IAuthMagicAccountGate>();
        var admission = await gate.AdmitAsync(account, context.HttpContext, context.HttpContext.RequestAborted).ConfigureAwait(false);
        if (!admission.IsAdmitted)
        {
            Deny(context, options, admission.RefusalCode ?? "failed");
            return;
        }

        context.Principal = CreatePrincipal(account, admission.Claims);
    }

    private static void Deny(TicketReceivedContext context, AuthMagicServerOptions options, string code)
    {
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
        LogSignInRefused(logger, context.Scheme.Name, code);
        context.Response.Redirect(options.DeniedPath + "?reason=" + code);
        context.HandleResponse();
    }

    internal static ClaimsPrincipal CreatePrincipal(AuthMagicExternalAccount account, IReadOnlyList<Claim> claims)
    {
        // Only the provider, the subject and the gate's claims go into the cookie: no tokens, no provider claims.
        var identity = new ClaimsIdentity(AuthMagicServerDefaults.CookieScheme, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(AuthMagicServerDefaults.ProviderClaim, account.Provider));
        identity.AddClaim(new Claim(AuthMagicServerDefaults.SubjectClaim, account.Subject));
        foreach (var claim in claims.Where(c => c.Type is not (AuthMagicServerDefaults.ProviderClaim or AuthMagicServerDefaults.SubjectClaim)))
        {
            identity.AddClaim(new Claim(claim.Type, claim.Value, claim.ValueType));
        }

        return new ClaimsPrincipal(identity);
    }

    internal static void ConfigureRemote(RemoteAuthenticationOptions o, AuthMagicServerOptions options)
    {
        o.SignInScheme = AuthMagicServerDefaults.CookieScheme;
        o.SaveTokens = false;
        o.RemoteAuthenticationTimeout = TimeSpan.FromMinutes(10);
        // Callbacks are top-level GET navigations (response_mode=query), so Lax cookies are sent on the way back.
        o.CorrelationCookie.SameSite = SameSiteMode.Lax;
        o.CorrelationCookie.HttpOnly = true;
        o.CorrelationCookie.SecurePolicy = options.IsHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
    }

    internal static Task RemoteFailureAsync(RemoteFailureContext context, AuthMagicServerOptions options)
    {
        var code = context.Failure is AuthMagicRefusedException refused ? refused.Code : "failed";
        var logger = context.HttpContext.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(LoggerCategory);
        LogSignInNotCompleted(logger, context.Scheme.Name, code, context.Failure?.Message ?? string.Empty);
        context.Response.Redirect(options.DeniedPath + "?reason=" + code);
        context.HandleResponse();
        return Task.CompletedTask;
    }

    internal static Task AccessDeniedAsync(AccessDeniedContext context, AuthMagicServerOptions options)
    {
        context.Response.Redirect(options.DeniedPath + "?reason=cancelled");
        context.HandleResponse();
        return Task.CompletedTask;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Sign-in through {Scheme} did not complete ({Code}): {Detail}")]
    private static partial void LogSignInNotCompleted(ILogger logger, string scheme, string code, string detail);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Sign-in through {Scheme} refused by the application ({Code})")]
    private static partial void LogSignInRefused(ILogger logger, string scheme, string code);
}
```
In this task leave out the two `if` blocks that register Google and GitHub (they call `ConfigureGoogle` and `ConfigureGitHub`, which A4 and A5 write and which add those blocks back), so the project compiles with the cookie scheme only. `SignIn_UnknownProvider_Returns404` then still passes, because an unconfigured provider is a 404.

`AuthMagicServerApplicationBuilderExtensions.cs`:
```csharp
namespace PanoramicData.AuthMagic.AspNetCore;

/// <summary>
/// Middleware for server-side sign-in.
/// </summary>
public static class AuthMagicServerApplicationBuilderExtensions
{
    /// <summary>
    /// Sends requests for any other scheme, host or port to the configured PublicOrigin (GET and HEAD get a 307
    /// redirect; other methods get 421), so cookies, redirect URIs and Origin checks always agree. Put it first,
    /// after forwarded headers.
    /// </summary>
    /// <param name="app">The application.</param>
    /// <param name="exemptPaths">Paths served on any host, for example a container health check.</param>
    /// <returns>The application.</returns>
    public static IApplicationBuilder UseAuthMagicCanonicalOrigin(this IApplicationBuilder app, params string[] exemptPaths)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(exemptPaths);
        var origin = app.ApplicationServices.GetRequiredService<AuthMagicServerOptions>().PublicOrigin!;
        var exempt = exemptPaths.Select(p => new PathString(p)).ToArray();
        return app.Use(async (context, next) =>
        {
            var request = context.Request;
            var port = request.Host.Port ?? (string.Equals(request.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ? 443 : 80);
            var same = string.Equals(request.Scheme, origin.Scheme, StringComparison.OrdinalIgnoreCase)
                && string.Equals(request.Host.Host, origin.Host, StringComparison.OrdinalIgnoreCase)
                && port == origin.Port;
            if (same || exempt.Any(p => request.Path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase)))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            if (HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method))
            {
                context.Response.StatusCode = StatusCodes.Status307TemporaryRedirect;
                context.Response.Headers.Location = new Uri(origin, request.PathBase + request.Path + request.QueryString).AbsoluteUri;
                return;
            }

            context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
        });
    }
}
```

`AuthMagicServerEndpointRouteBuilderExtensions.cs`:
```csharp
namespace PanoramicData.AuthMagic.AspNetCore;

/// <summary>
/// The sign-in and sign-out endpoints.
/// </summary>
public static class AuthMagicServerEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps POST {prefix}/signin/{provider} and POST {prefix}/signout. Both validate the antiforgery token
    /// themselves; the form field returnUrl is used only when it is a local path.
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The group, for further conventions such as rate limiting.</returns>
    public static IEndpointConventionBuilder MapAuthMagicServer(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var options = endpoints.ServiceProvider.GetRequiredService<AuthMagicServerOptions>();
        var group = endpoints.MapGroup(options.EndpointPrefix);
        _ = group.MapPost("/signin/{provider}", SignInAsync);
        _ = group.MapPost("/signout", SignOutAsync);
        return group.AllowAnonymous().DisableAntiforgery();
    }

    private static async Task<IResult> SignInAsync(string provider, HttpContext context, IAntiforgery antiforgery, AuthMagicServerOptions options, IAuthMagicAccountGate gate)
    {
        if (!await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
        {
            return Results.BadRequest();
        }

        var scheme = provider switch
        {
            AuthMagicServerDefaults.ProviderGoogle when options.Google is not null => AuthMagicServerDefaults.GoogleScheme,
            AuthMagicServerDefaults.ProviderGitHub when options.GitHub is not null => AuthMagicServerDefaults.GitHubScheme,
            _ => null,
        };
        if (scheme is null)
        {
            return Results.NotFound();
        }

        var form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
        var returnPath = LocalReturnUrl.Sanitise(form["returnUrl"].ToString());

        // No session survives into a new sign-in (session fixation).
        await EndSessionAsync(context, gate).ConfigureAwait(false);
        return Results.Challenge(new AuthenticationProperties { RedirectUri = returnPath }, [scheme]);
    }

    private static async Task<IResult> SignOutAsync(HttpContext context, IAntiforgery antiforgery, AuthMagicServerOptions options, IAuthMagicAccountGate gate)
    {
        if (!await antiforgery.IsRequestValidAsync(context).ConfigureAwait(false))
        {
            return Results.BadRequest();
        }

        await EndSessionAsync(context, gate).ConfigureAwait(false);
        return Results.LocalRedirect(options.SignedOutPath);
    }

    private static async Task EndSessionAsync(HttpContext context, IAuthMagicAccountGate gate)
    {
        if (context.User.Identity?.IsAuthenticated == true)
        {
            await gate.SignedOutAsync(context.User, context, context.RequestAborted).ConfigureAwait(false);
        }

        await context.SignOutAsync(AuthMagicServerDefaults.CookieScheme).ConfigureAwait(false);
    }
}
```

- [ ] **Step 5: Run and commit**

```powershell
dotnet test -c Release --filter-class PanoramicData.AuthMagic.Test.Server.SessionTests
```
Expected now: `SignIn_WithoutAntiforgeryToken_IsRejected`, `SignIn_UnknownProvider_Returns404`, `ProtectedEndpoint_ForABrowser_RedirectsToSignIn`, `ProtectedEndpoint_ForAScript_Returns401` and `CanonicalOrigin_OtherHost_IsRedirectedOrRefused` pass; the tests that need a completed Google sign-in fail until A4. Format, build with 0 warnings, then:
```powershell
git add PanoramicData.AuthMagic.AspNetCore PanoramicData.AuthMagic.Test/Server
git commit -m "Server sign-in: hardened cookie session, gate hooks, antiforgery sign-in and sign-out, canonical origin"
```

### Task A4: Google (OpenID Connect) with PKCE, nonce and validated id tokens

**Files:**
- Modify: `Internal/AuthMagicServerEvents.cs` (add `ConfigureGoogle` and `GoogleAccount`)
- Test: `PanoramicData.AuthMagic.Test/Server/GoogleSignInTests.cs`

**Interfaces:**
- Consumes: A3, the fake provider (Appendix F).
- Produces: a Google sign-in whose only outputs are an `AuthMagicExternalAccount("google", sub, [email if email_verified], name)` to the gate and, if admitted, a cookie with the provider, subject and the gate's claims.

- [ ] **Step 1: Write the failing tests**

`PanoramicData.AuthMagic.Test/Server/GoogleSignInTests.cs`:
```csharp
using System.Net;
using PanoramicData.AuthMagic.AspNetCore;

namespace PanoramicData.AuthMagic.Test.Server;

public class GoogleSignInTests
{
    private static readonly FakeAccount Ada = new("google-sub-ada", "ada@example.test", true, "Ada");

    [Fact]
    public async Task Challenge_CarriesPkceNonceStateAndThePublicRedirectUri()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var challenge = await host.PostSignInAsync("google", "/shows");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        var location = challenge.Headers.Location!;
        Assert.Equal("https://accounts.google.com/o/oauth2/v2/auth", location.GetLeftPart(UriPartial.Path));
        var q = ServerTestHost.Query(location);
        Assert.Equal(FakeIdentityProvider.GoogleClientId, q["client_id"]);
        Assert.Equal("https://localhost/signin-google", q["redirect_uri"]);
        Assert.Equal("code", q["response_type"]);
        Assert.False(q.ContainsKey("response_mode")); // code flow with response_mode=query: the handler omits the default
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal(43, q["code_challenge"].Length);
        Assert.False(string.IsNullOrEmpty(q["nonce"]));
        Assert.False(string.IsNullOrEmpty(q["state"]));
        Assert.Equal("openid email profile", q["scope"]);
        Assert.Equal("select_account", q["prompt"]);
    }

    [Fact]
    public async Task Callback_WithValidIdToken_SignsInWithGateClaims()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var back = await host.CompleteAsync("google", Ada, returnUrl: "/shows");
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.Equal("/shows", back.Headers.Location!.OriginalString);
        var account = Assert.Single(host.Gate.Admitted);
        Assert.Equal("google", account.Provider);
        Assert.Equal("google-sub-ada", account.Subject);
        Assert.Equal(new[] { "ada@example.test" }, account.VerifiedEmails);
        Assert.Equal("Ada", account.DisplayName);
        var me = (await host.MeAsync())!;
        Assert.Equal("google", me[AuthMagicServerDefaults.ProviderClaim]);
        Assert.Equal("google-sub-ada", me[AuthMagicServerDefaults.SubjectClaim]);
        Assert.Equal("member", me[ClaimTypes.Role]);
        Assert.Equal(4, me.Count); // provider, subject, name, role: nothing from the id token
    }

    [Fact]
    public async Task Cookie_HoldsNoTokens()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var back = await host.CompleteAsync("google", Ada);
        var cookie = host.SessionCookie()!;
        Assert.True(cookie.Value.Length < 1500, $"The session cookie is {cookie.Value.Length} characters; an id token would make it much longer.");
        var me = (await host.MeAsync())!;
        Assert.DoesNotContain(me.Keys, k => k.Contains("token", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TokenRequest_ProvesPkce()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var _ = await host.CompleteAsync("google", Ada);
        Assert.Equal(1, host.Idp.PkceVerified);
    }

    [Fact]
    public Task Callback_WrongIssuer_IsRefused() => AssertRefusedAsync(new Tamper(Issuer: "https://accounts.example.test"));

    [Fact]
    public Task Callback_WrongAudience_IsRefused() => AssertRefusedAsync(new Tamper(Audience: "someone-else.apps.googleusercontent.com"));

    [Fact]
    public Task Callback_ExpiredToken_IsRefused() => AssertRefusedAsync(new Tamper(ExpiresIn: TimeSpan.FromMinutes(-10)));

    [Fact]
    public Task Callback_WrongNonce_IsRefused() => AssertRefusedAsync(new Tamper(Nonce: "not-the-nonce"));

    [Fact]
    public Task Callback_TokenSignedByAnotherKey_IsRefused() => AssertRefusedAsync(new Tamper(SignWithOtherKey: true));

    [Fact]
    public async Task Callback_TamperedOrMissingState_IsRefused()
    {
        foreach (var tamper in new[] { new Tamper(ReplaceState: "forged"), new Tamper(DropState: true) })
        {
            await using var host = await ServerTestHost.StartAsync();
            using var challenge = await host.PostSignInAsync("google");
            using var back = await host.Client.GetAsync(host.Idp.Authorize(challenge.Headers.Location!, Ada, tamper));
            Assert.StartsWith("/denied?reason=", back.Headers.Location!.OriginalString, StringComparison.Ordinal);
            Assert.Empty(host.Gate.Admitted);
            Assert.Null(await host.MeAsync());
        }
    }

    [Fact]
    public async Task Callback_Replayed_IsRefused()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var challenge = await host.PostSignInAsync("google");
        var callback = host.Idp.Authorize(challenge.Headers.Location!, Ada);
        using var first = await host.Client.GetAsync(callback);
        Assert.Equal("/", first.Headers.Location!.OriginalString);
        using var second = await host.Client.GetAsync(callback);
        Assert.Equal("/denied?reason=failed", second.Headers.Location!.OriginalString);
        Assert.Single(host.Gate.Admitted);
    }

    [Fact]
    public async Task Callback_UnverifiedEmail_IsNotPassedOn()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var _ = await host.CompleteAsync("google", Ada with { EmailVerified = false });
        Assert.Empty(Assert.Single(host.Gate.Admitted).VerifiedEmails);
    }

    [Fact]
    public async Task Callback_GateRefusal_RedirectsToDeniedWithTheCode()
    {
        await using var host = await ServerTestHost.StartAsync();
        host.Gate.Admit = _ => AuthMagicAdmission.Refuse("not-listed");
        using var back = await host.CompleteAsync("google", Ada);
        Assert.Equal("/denied?reason=not-listed", back.Headers.Location!.OriginalString);
        Assert.Null(host.SessionCookie());
    }

    [Fact]
    public async Task Callback_UserCancelled_RedirectsToDeniedCancelled()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var challenge = await host.PostSignInAsync("google");
        var q = ServerTestHost.Query(challenge.Headers.Location!);
        using var back = await host.Client.GetAsync(new Uri($"/signin-google?error=access_denied&state={Uri.EscapeDataString(q["state"])}", UriKind.Relative));
        Assert.Equal("/denied?reason=cancelled", back.Headers.Location!.OriginalString);
    }

    private static async Task AssertRefusedAsync(Tamper tamper)
    {
        await using var host = await ServerTestHost.StartAsync();
        using var back = await host.CompleteAsync("google", Ada, tamper);
        Assert.Equal(HttpStatusCode.Redirect, back.StatusCode);
        Assert.Equal("/denied?reason=failed", back.Headers.Location!.OriginalString);
        Assert.Empty(host.Gate.Admitted);
        Assert.Null(host.SessionCookie());
        Assert.Null(await host.MeAsync());
    }
}
```

- [ ] **Step 2: Run them and see them fail**

```powershell
dotnet test -c Release --filter-class PanoramicData.AuthMagic.Test.Server.GoogleSignInTests
```
Expected: fail (the scheme is not configured yet, or `ConfigureGoogle` is missing).

- [ ] **Step 3: Implement** (add the Google `if` block to `AddAuthMagicServer` as listed in A3, and append to `Internal/AuthMagicServerEvents.cs`)

```csharp
    internal static void ConfigureGoogle(OpenIdConnectOptions o, AuthMagicServerProviderOptions google, AuthMagicServerOptions options)
    {
        ConfigureRemote(o, options);
        o.Authority = AuthMagicServerDefaults.GoogleAuthority;
        o.RequireHttpsMetadata = true;
        o.ClientId = google.ClientId;
        o.ClientSecret = google.ClientSecret;
        o.CallbackPath = AuthMagicServerDefaults.GoogleCallbackPath;
        o.ResponseType = OpenIdConnectResponseType.Code;
        o.ResponseMode = OpenIdConnectResponseMode.Query;
        o.UsePkce = true;
        o.Prompt = "select_account";
        o.Scope.Clear();
        o.Scope.Add("openid");
        o.Scope.Add("email");
        o.Scope.Add("profile");
        o.GetClaimsFromUserInfoEndpoint = false;
        o.MapInboundClaims = false;
        o.UseTokenLifetime = false;
        o.DisableTelemetry = true;
        o.ProtocolValidator.RequireNonce = true;
        o.NonceCookie.SameSite = SameSiteMode.Lax;
        o.NonceCookie.HttpOnly = true;
        o.NonceCookie.SecurePolicy = options.IsHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuers = ["https://accounts.google.com", "accounts.google.com"],
            ValidAudience = google.ClientId,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = "name",
        };
        o.Events.OnTicketReceived = context => TicketReceivedAsync(context, options, GoogleAccount);
        o.Events.OnRemoteFailure = context => RemoteFailureAsync(context, options);
        o.Events.OnAccessDenied = context => AccessDeniedAsync(context, options);
    }

    /// <summary>The account in a fully validated Google answer; null when it has no subject.</summary>
    internal static AuthMagicExternalAccount? GoogleAccount(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirst("sub")?.Value;
        if (string.IsNullOrEmpty(subject))
        {
            return null;
        }

        var email = principal.FindFirst("email")?.Value;
        var verified = string.Equals(principal.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase);
        return new AuthMagicExternalAccount(
            AuthMagicServerDefaults.ProviderGoogle,
            subject,
            verified && !string.IsNullOrWhiteSpace(email) ? [email] : [],
            principal.FindFirst("name")?.Value);
    }
```
By the time `OnTicketReceived` runs, the handler has checked the signature, issuer, audience and expiry of the id token and then the nonce and the rest of the protocol (`ValidateTokenResponse`, which runs after `OnTokenValidated`); a refusal redirects to the denied page with its code and no cookie is issued; the correlation cookie is already consumed, so the callback cannot be replayed. (Running the gate in `OnTokenValidated` instead was tried while writing this plan: `Callback_WrongNonce_IsRefused` caught the gate being called for an answer the handler then rejected.)

- [ ] **Step 4: Run all server tests, format, commit**

```powershell
dotnet test -c Release --filter-class PanoramicData.AuthMagic.Test.Server.GoogleSignInTests
dotnet test -c Release --filter-class PanoramicData.AuthMagic.Test.Server.SessionTests
dotnet format whitespace PanoramicData.AuthMagic.AspNetCore
dotnet format whitespace PanoramicData.AuthMagic.Test
dotnet build -c Release
```
Expected: all Google and Session tests pass; 0 warnings. If `MeAsync` shows a claim count other than 4, the handler is leaking id token claims: check `CreatePrincipal` replaced the principal.
```powershell
git add PanoramicData.AuthMagic.AspNetCore PanoramicData.AuthMagic.Test/Server
git commit -m "Server sign-in: Google via OpenID Connect with PKCE, nonce and validated id tokens"
```

### Task A5: GitHub (OAuth) with PKCE and verified emails

**Files:**
- Create: `Internal/GitHubAccountReader.cs`
- Modify: `Internal/AuthMagicServerEvents.cs` (add `ConfigureGitHub`)
- Test: `PanoramicData.AuthMagic.Test/Server/GitHubSignInTests.cs`

**Interfaces:**
- Produces: `AuthMagicExternalAccount("github", "<numeric id>", verified emails primary first, name or login)`.

- [ ] **Step 1: Write the failing tests**

`PanoramicData.AuthMagic.Test/Server/GitHubSignInTests.cs`:
```csharp
using System.Net;
using PanoramicData.AuthMagic.AspNetCore;
using PanoramicData.AuthMagic.AspNetCore.Internal;

namespace PanoramicData.AuthMagic.Test.Server;

public class GitHubSignInTests
{
    private static readonly FakeAccount Grace = new("1001", null, true, "Grace",
        [("old@example.test", false, false), ("grace@example.test", true, true), ("work@example.test", false, true)]);

    [Fact]
    public async Task Challenge_CarriesPkceStateAndEmailScope()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var challenge = await host.PostSignInAsync("github");
        var location = challenge.Headers.Location!;
        Assert.Equal("https://github.com/login/oauth/authorize", location.GetLeftPart(UriPartial.Path));
        var q = ServerTestHost.Query(location);
        Assert.Equal(FakeIdentityProvider.GitHubClientId, q["client_id"]);
        Assert.Equal("https://localhost/signin-github", q["redirect_uri"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.False(string.IsNullOrEmpty(q["state"]));
        Assert.Equal(new[] { "read:user", "user:email" }, q["scope"].Split(' '));
    }

    [Fact]
    public async Task Callback_UsesNumericIdAndVerifiedEmailsPrimaryFirst()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var back = await host.CompleteAsync("github", Grace);
        Assert.Equal("/", back.Headers.Location!.OriginalString);
        var account = Assert.Single(host.Gate.Admitted);
        Assert.Equal("github", account.Provider);
        Assert.Equal("1001", account.Subject);
        Assert.Equal(new[] { "grace@example.test", "work@example.test" }, account.VerifiedEmails);
        Assert.Equal("Grace", account.DisplayName);
        Assert.Equal(1, host.Idp.PkceVerified);
        Assert.NotNull(await host.MeAsync());
    }

    [Fact]
    public async Task Callback_NoVerifiedEmail_PassesAnEmptyList()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var _ = await host.CompleteAsync("github", Grace with { GitHubEmails = [("x@example.test", true, false)] });
        Assert.Empty(Assert.Single(host.Gate.Admitted).VerifiedEmails);
    }

    [Fact]
    public async Task Callback_TokenError_IsRefused()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var back = await host.CompleteAsync("github", Grace, new Tamper(GitHubTokenError: true));
        Assert.Equal("/denied?reason=failed", back.Headers.Location!.OriginalString);
        Assert.Empty(host.Gate.Admitted);
    }

    [Fact]
    public async Task Callback_UserEndpointFails_IsRefusedAsProviderError()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var back = await host.CompleteAsync("github", Grace, new Tamper(GitHubUserFails: true));
        Assert.Equal("/denied?reason=provider-error", back.Headers.Location!.OriginalString);
        Assert.Null(host.SessionCookie());
    }

    [Fact]
    public async Task Callback_Replayed_IsRefused()
    {
        await using var host = await ServerTestHost.StartAsync();
        using var challenge = await host.PostSignInAsync("github");
        var callback = host.Idp.Authorize(challenge.Headers.Location!, Grace);
        using var first = await host.Client.GetAsync(callback);
        using var second = await host.Client.GetAsync(callback);
        Assert.Equal("/denied?reason=failed", second.Headers.Location!.OriginalString);
        Assert.Single(host.Gate.Admitted);
    }

    [Theory]
    [InlineData("""{ "login": "x" }""")]
    [InlineData("""{ "id": "1001" }""")]
    [InlineData("""{ "id": 1.5 }""")]
    public void Read_WithoutANumericId_IsAProviderError(string user)
    {
        using var u = JsonDocument.Parse(user);
        using var e = JsonDocument.Parse("[]");
        Assert.Equal("provider-error", Assert.Throws<AuthMagicRefusedException>(() => GitHubAccountReader.Read(u.RootElement, e.RootElement)).Code);
    }

    [Fact]
    public void Read_FallsBackToTheLoginAndIgnoresMalformedEmails()
    {
        using var u = JsonDocument.Parse("""{ "id": 7, "login": "octo", "name": "" }""");
        using var e = JsonDocument.Parse("""[ { "email": 5, "verified": true }, { "verified": true }, { "email": "a@example.test", "verified": "true" }, { "email": "b@example.test", "verified": true } ]""");
        var account = GitHubAccountReader.Read(u.RootElement, e.RootElement);
        Assert.Equal("7", account.Subject);
        Assert.Equal("octo", account.DisplayName);
        Assert.Equal(new[] { "b@example.test" }, account.VerifiedEmails);
    }
}
```

- [ ] **Step 2: Run them and see them fail**

```powershell
dotnet build -c Release PanoramicData.AuthMagic.Test
```
Expected: `GitHubAccountReader` does not exist.

- [ ] **Step 3: Implement**

Add the GitHub `if` block to `AddAuthMagicServer` (as listed in A3), and append to `Internal/AuthMagicServerEvents.cs`:
```csharp
    internal static void ConfigureGitHub(OAuthOptions o, AuthMagicServerProviderOptions github, AuthMagicServerOptions options)
    {
        ConfigureRemote(o, options);
        o.ClientId = github.ClientId;
        o.ClientSecret = github.ClientSecret;
        o.CallbackPath = AuthMagicServerDefaults.GitHubCallbackPath;
        o.AuthorizationEndpoint = AuthMagicServerDefaults.GitHubAuthorizationEndpoint;
        o.TokenEndpoint = AuthMagicServerDefaults.GitHubTokenEndpoint;
        o.UserInformationEndpoint = AuthMagicServerDefaults.GitHubUserEndpoint;
        o.UsePkce = true;
        o.Scope.Clear();
        o.Scope.Add("read:user");
        o.Scope.Add("user:email");
        o.Events.OnCreatingTicket = GitHubAccountReader.CreatingTicketAsync;
        o.Events.OnTicketReceived = context => TicketReceivedAsync(context, options, GitHubAccountReader.FromPrincipal);
        o.Events.OnRemoteFailure = context => RemoteFailureAsync(context, options);
        o.Events.OnAccessDenied = context => AccessDeniedAsync(context, options);
    }
```

`Internal/GitHubAccountReader.cs`:
```csharp
namespace PanoramicData.AuthMagic.AspNetCore.Internal;

/// <summary>
/// Reads the GitHub account (numeric id, name) and its verified email addresses with the access token, which is
/// then discarded.
/// </summary>
internal static class GitHubAccountReader
{
    internal static async Task CreatingTicketAsync(OAuthCreatingTicketContext context)
    {
        var cancellationToken = context.HttpContext.RequestAborted;
        using var user = await GetAsync(context, AuthMagicServerDefaults.GitHubUserEndpoint, cancellationToken).ConfigureAwait(false);
        using var emails = await GetAsync(context, AuthMagicServerDefaults.GitHubEmailsEndpoint, cancellationToken).ConfigureAwait(false);
        // Carried to OnTicketReceived, where the gate runs for both providers once the handler is done.
        context.Principal = ToPrincipal(Read(user.RootElement, emails.RootElement));
    }

    private const string VerifiedEmailClaim = "authmagic:verified-email";
    private const string DisplayNameClaim = "authmagic:display-name";

    internal static ClaimsPrincipal ToPrincipal(AuthMagicExternalAccount account)
    {
        var identity = new ClaimsIdentity(AuthMagicServerDefaults.GitHubScheme);
        identity.AddClaim(new Claim(AuthMagicServerDefaults.ProviderClaim, account.Provider));
        identity.AddClaim(new Claim(AuthMagicServerDefaults.SubjectClaim, account.Subject));
        identity.AddClaims(account.VerifiedEmails.Select(e => new Claim(VerifiedEmailClaim, e)));
        if (account.DisplayName is not null)
        {
            identity.AddClaim(new Claim(DisplayNameClaim, account.DisplayName));
        }

        return new ClaimsPrincipal(identity);
    }

    internal static AuthMagicExternalAccount? FromPrincipal(ClaimsPrincipal principal)
    {
        var subject = principal.FindFirst(AuthMagicServerDefaults.SubjectClaim)?.Value;
        return string.IsNullOrEmpty(subject)
            ? null
            : new AuthMagicExternalAccount(
                AuthMagicServerDefaults.ProviderGitHub,
                subject,
                [.. principal.FindAll(VerifiedEmailClaim).Select(c => c.Value)],
                principal.FindFirst(DisplayNameClaim)?.Value);
    }

    internal static AuthMagicExternalAccount Read(JsonElement user, JsonElement emails)
    {
        if (user.ValueKind != JsonValueKind.Object
            || !user.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.Number
            || !id.TryGetInt64(out var number))
        {
            throw new AuthMagicRefusedException("provider-error");
        }

        var verified = new List<(string Email, bool Primary)>();
        if (emails.ValueKind == JsonValueKind.Array)
        {
            for (var i = 0; i < emails.GetArrayLength(); i++)
            {
                var e = emails[i]; // indexed, not EnumerateArray: its enumerator is IDisposable (IDISP004)
                if (e.ValueKind == JsonValueKind.Object && IsTrue(e, "verified") && Text(e, "email") is { Length: > 0 } address)
                {
                    verified.Add((address, IsTrue(e, "primary")));
                }
            }
        }

        var name = Text(user, "name");
        return new AuthMagicExternalAccount(
            AuthMagicServerDefaults.ProviderGitHub,
            number.ToString(CultureInfo.InvariantCulture),
            [.. verified.OrderByDescending(v => v.Primary).Select(v => v.Email)], // primary first; the sort is stable
            string.IsNullOrWhiteSpace(name) ? Text(user, "login") : name);
    }

    private static bool IsTrue(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<JsonDocument> GetAsync(OAuthCreatingTicketContext context, string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(url));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", context.AccessToken);
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await context.Backchannel.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new AuthMagicRefusedException("provider-error");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
    }
}
```
(The two GETs run on the handler's own back-channel, which carries its timeout, user agent and, in tests, the fake. CONTRIBUTING prefers Refit for HTTP client interfaces; for two calls on a handler-owned `HttpClient` the PR description says why plain `HttpClient` was used, and the maintainer decides.)

- [ ] **Step 4: Run, format, commit**

```powershell
dotnet test -c Release --filter-class PanoramicData.AuthMagic.Test.Server.GitHubSignInTests
dotnet format whitespace PanoramicData.AuthMagic.AspNetCore
dotnet format whitespace PanoramicData.AuthMagic.Test
dotnet build -c Release
git add PanoramicData.AuthMagic.AspNetCore PanoramicData.AuthMagic.Test/Server
git commit -m "Server sign-in: GitHub via OAuth with PKCE and verified emails"
```
Expected: all pass, 0 warnings.

### Task A6: Sign-in buttons component (bUnit)

**Files:**
- Create: `Components/AuthMagicSignInButtons.razor`, `Components/AuthMagicSignInButtons.razor.cs`
- Test: `PanoramicData.AuthMagic.Test/Server/SignInButtonsTests.cs`

**Interfaces:**
- Produces: `<AuthMagicSignInButtons ReturnPath="..." ButtonCssClass="..." />` in namespace `PanoramicData.AuthMagic.AspNetCore.Components`. Use it in a statically rendered page.

- [ ] **Step 1: Write the failing tests**

`PanoramicData.AuthMagic.Test/Server/SignInButtonsTests.cs`:
```csharp
using Bunit;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.DependencyInjection;
using PanoramicData.AuthMagic.AspNetCore;
using PanoramicData.AuthMagic.AspNetCore.Components;

namespace PanoramicData.AuthMagic.Test.Server;

public class SignInButtonsTests : BunitContext
{
    private sealed class FixedAntiforgery : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-value", "__RequestVerificationToken");
    }

    private IRenderedComponent<AuthMagicSignInButtons> Render(bool google, bool github, string? returnPath)
    {
        Services.AddSingleton(new AuthMagicServerOptions
        {
            PublicOrigin = new Uri("https://localhost"),
            Google = google ? new AuthMagicServerProviderOptions { ClientId = "g", ClientSecret = "s" } : null,
            GitHub = github ? new AuthMagicServerProviderOptions { ClientId = "h", ClientSecret = "s" } : null,
        });
        Services.AddSingleton<AntiforgeryStateProvider, FixedAntiforgery>();
        return Render<AuthMagicSignInButtons>(p => p.Add(c => c.ReturnPath, returnPath).Add(c => c.ButtonCssClass, "btn btn-primary w-100"));
    }

    [Fact]
    public void Buttons_RenderOneFormPerProvider()
    {
        var cut = Render(true, true, "/shows");
        var forms = cut.FindAll("form");
        Assert.Equal(new[] { "/auth/signin/google", "/auth/signin/github" }, forms.Select(f => f.GetAttribute("action")));
        Assert.All(forms, f => Assert.Equal("post", f.GetAttribute("method")));
        Assert.Equal(new[] { "Sign in with Google", "Sign in with GitHub" }, cut.FindAll("button").Select(b => b.TextContent.Trim()));
        Assert.All(cut.FindAll("button"), b => Assert.Equal("btn btn-primary w-100", b.GetAttribute("class")));
    }

    [Fact]
    public void Buttons_OnlyGoogle_RendersOneForm()
        => Assert.Single(Render(true, false, null).FindAll("form"));

    [Theory]
    [InlineData("/shows", "/shows")]
    [InlineData("//evil.example.test", "/")]
    [InlineData(null, "/")]
    public void Buttons_SanitiseTheReturnPath(string? given, string expected)
        => Assert.All(Render(true, true, given).FindAll("input[name=returnUrl]"), i => Assert.Equal(expected, i.GetAttribute("value")));

    [Fact]
    public void Buttons_IncludeTheAntiforgeryToken()
        => Assert.All(Render(true, true, "/").FindAll("form"), f => Assert.Equal("token-value", f.QuerySelector("input[name=__RequestVerificationToken]")!.GetAttribute("value")));
}
```

- [ ] **Step 2: Run them and see them fail** (`dotnet build -c Release PanoramicData.AuthMagic.Test`: the component does not exist.)

- [ ] **Step 3: Implement**

`Components/AuthMagicSignInButtons.razor`:
```razor
@namespace PanoramicData.AuthMagic.AspNetCore.Components
@foreach (var provider in Providers)
{
    <form method="post" action="@($"{Options.EndpointPrefix}/signin/{provider.Id}")" class="authmagic-signin">
        <AntiforgeryToken />
        <input type="hidden" name="returnUrl" value="@SafeReturnPath" />
        <button type="submit" class="@ButtonCssClass" data-provider="@provider.Id">@provider.Text</button>
    </form>
}
```

`Components/AuthMagicSignInButtons.razor.cs`:
```csharp
namespace PanoramicData.AuthMagic.AspNetCore.Components;

/// <summary>
/// One sign-in form per configured provider. Render it in a statically rendered page; each form posts to the
/// sign-in endpoint with the antiforgery token and the return path.
/// </summary>
public partial class AuthMagicSignInButtons : ComponentBase
{
    [Inject]
    private AuthMagicServerOptions Options { get; set; } = null!;

    /// <summary>The local path to return to after signing in; anything else becomes "/".</summary>
    [Parameter]
    public string? ReturnPath { get; set; }

    /// <summary>The CSS class of each button.</summary>
    [Parameter]
    public string ButtonCssClass { get; set; } = "btn btn-primary";

    private string SafeReturnPath => LocalReturnUrl.Sanitise(ReturnPath);

    private IEnumerable<(string Id, string Text)> Providers
    {
        get
        {
            if (Options.Google is not null)
            {
                yield return (AuthMagicServerDefaults.ProviderGoogle, "Sign in with Google");
            }

            if (Options.GitHub is not null)
            {
                yield return (AuthMagicServerDefaults.ProviderGitHub, "Sign in with GitHub");
            }
        }
    }
}
```

- [ ] **Step 4: Run, format, commit**

```powershell
dotnet test -c Release --filter-class PanoramicData.AuthMagic.Test.Server.SignInButtonsTests
dotnet format whitespace PanoramicData.AuthMagic.AspNetCore
dotnet format whitespace PanoramicData.AuthMagic.Test
dotnet build -c Release
git add PanoramicData.AuthMagic.AspNetCore PanoramicData.AuthMagic.Test/Server
git commit -m "Server sign-in: sign-in buttons component"
```

### Task A7: README, version, full build, coverage and Codacy readiness

**Files:**
- Modify: `README.md`, `version.json`

- [ ] **Step 1: Document the package**

Add to `README.md` after the badges and before `## Microsoft`:
````markdown
## Server-side sign-in (PanoramicData.AuthMagic.AspNetCore)

For ASP.NET Core and Blazor Server apps that need an HttpOnly cookie session, so that every HTTP request (pages,
APIs, media) carries the signed-in user. Google uses OpenID Connect (PKCE, nonce, id token validated against
Google's keys, issuer, audience and expiry); GitHub uses OAuth (PKCE, verified emails from /user/emails). No token
is stored. The application decides who may sign in through `IAuthMagicAccountGate`.

```csharp
builder.Services.AddAuthMagicServer<MyGate>(o =>
{
    o.PublicOrigin = new Uri("https://app.example.org");   // http is allowed only for localhost
    o.Google = new() { ClientId = googleId, ClientSecret = googleSecret };
    o.GitHub = new() { ClientId = gitHubId, ClientSecret = gitHubSecret };
});

app.UseAuthMagicCanonicalOrigin("/healthz");
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapAuthMagicServer();   // POST /auth/signin/{google|github}, POST /auth/signout
```

Register `https://app.example.org/signin-google` with Google and `https://app.example.org/signin-github` with
GitHub. On a statically rendered sign-in page: `<AuthMagicSignInButtons ReturnPath="@ReturnUrl" />`.
`MyGate.AdmitAsync` receives the provider, the stable subject and only verified email addresses, and returns
`AuthMagicAdmission.Admit(claims)` or `AuthMagicAdmission.Refuse("not-listed")`; `ValidateSessionAsync` runs on
every request and may rebuild or end the session.
````

- [ ] **Step 2: Version**

`version.json`: change `"version": "2.0"` to `"version": "2.1"` (new public package and API). Nothing else.

- [ ] **Step 3: Full build, tests and coverage**

```powershell
dotnet build -c Release
dotnet test -c Release
dotnet format style PanoramicData.AuthMagic.AspNetCore --verify-no-changes
dotnet format analyzers PanoramicData.AuthMagic.AspNetCore --verify-no-changes
dotnet build PanoramicData.AuthMagic.Test/PanoramicData.AuthMagic.Test.csproj -c Debug
dotnet run --project PanoramicData.AuthMagic.Test -c Debug --no-build -- --coverage --coverage-output-format cobertura --coverage-output coverage.cobertura.xml
```
Expected: 0 warnings; every test passes (existing count plus the new ones); line coverage of `PanoramicData.AuthMagic.AspNetCore` at least 90 percent (read it from the cobertura file: `Select-String -Path PanoramicData.AuthMagic.Test\bin\Debug\net10.0\TestResults\coverage.cobertura.xml -Pattern 'package name="PanoramicData.AuthMagic.AspNetCore"'`). If a branch is uncovered, add a test, do not exclude code.

- [ ] **Step 4: Check nothing sensitive is logged or committed**

```powershell
git diff origin/main --stat
Select-String -Path PanoramicData.AuthMagic.AspNetCore\**\*.cs -Pattern 'Log.*(Token|Secret|code\b|Cookie)' 
```
Expected: the stat lists only the files of this group; the search finds nothing that logs a token, secret, code or cookie (the one log message logs scheme, refusal code and the handler's failure message).

- [ ] **Step 5: Commit**

```powershell
git add README.md version.json
git commit -m "Server sign-in: README and version 2.1"
```

### Task A8: Local package for WebAudioArchive, push the branch, open the pull request

**Files:** none in the repository; packages go to `C:\Users\david\source\repos\local-nuget` (outside every repository).

- [ ] **Step 1: Pack locally**

```powershell
New-Item -ItemType Directory -Force C:\Users\david\source\repos\local-nuget | Out-Null
dotnet pack PanoramicData.AuthMagic.AspNetCore -c Release -o C:\Users\david\source\repos\local-nuget
Get-ChildItem C:\Users\david\source\repos\local-nuget\PanoramicData.AuthMagic.AspNetCore.*.nupkg
```
Expected: one `PanoramicData.AuthMagic.AspNetCore.2.1.<height>-g<commit>.nupkg` (a prerelease version, because this is not a public release branch). Write the exact version down; group B pins it in B1.

- [ ] **Step 2: Push and open the pull request**

```powershell
git push -u origin feature/aspnetcore-server-signin
gh pr create --repo panoramicdata/PanoramicData.AuthMagic --base main --head feature/aspnetcore-server-signin `
  --title "Server-side sign-in: PanoramicData.AuthMagic.AspNetCore (Google and GitHub, cookie sessions)" `
  --body-file C:\Users\david\AppData\Local\Temp\authmagic-pr.md
```
Write the body file first (outside both repositories) with: what the package does; that it is additive (no existing type or member changed; existing packages unchanged apart from the shared version 2.1); the security properties (the Security review table rows for state, nonce, PKCE, redirect URI, token validation, verified email, open redirect, session fixation, cookie flags, CSRF, logout, replay, token storage); the test list and coverage figure; the note on plain `HttpClient` versus Refit; the package id question (O3); and the attribution line the harness supplies for pull requests.

- [ ] **Step 3: Wait for checks and Codacy**

```powershell
gh pr checks --repo panoramicdata/PanoramicData.AuthMagic --watch
```
Expected: build, test, pack and coverage green; Codacy reports zero new issues. Fix any Codacy finding with a code change on this branch (never by configuration), push, and re-check. Then tell the owner the PR is ready (O8). Do not merge it.

### Task A9 (owner): Review, merge and release

- [ ] Maintainer reviews and merges the PR (O8).
- [ ] Maintainer runs `.\Publish.ps1` on an up-to-date, clean `main` of PanoramicData.AuthMagic. The agent never runs it.
- [ ] Confirm on https://www.nuget.org/packages/PanoramicData.AuthMagic.AspNetCore that the version exists and is listed, and give the exact version to the agent for B14. If the release run fails or the package does not appear (the tags already run ahead of nuget.org, risk R2), the maintainer investigates the release workflow; group B waits at B13.

---
## Group B: WebAudioArchive integration

B1 to B5 do not need the AuthMagic package and can run while group A is in review. B6 needs the local package from A8. B14 needs the release from A9.

### Task B1: Worktree, branch, SQLite package and ignore rules

**Files:**
- Modify: `.gitignore`, `src/WebAudioArchive.Web/WebAudioArchive.Web.csproj`

- [ ] **Step 1: Make the worktree (the main working tree, where other agents work, is not touched)**

```powershell
cd C:\Users\david\source\repos\davidnmbond\WebAudioArchive
git fetch origin
git worktree add ..\WebAudioArchive-4a -b plan-4a-secure-sign-in origin/main
cd ..\WebAudioArchive-4a
dotnet build -c Release
dotnet test -c Release
```
Expected: 0 warnings, all tests pass; record the count. Never run `docker compose` from this worktree: its `docker` folder has the same compose project name as the main checkout and its `../data` is empty.

- [ ] **Step 2: Ignore anything secret or personal that sign-in could produce**

Append to `.gitignore`:
```
# Sign-in (plan 4a): secrets and exported user lists never go into git. Secrets normally live outside the tree.
docker/secrets/
*client_secret*
webaudioarchive-users*.json
```

- [ ] **Step 3: Add SQLite and the migration scripts to the web project**

In `src/WebAudioArchive.Web/WebAudioArchive.Web.csproj`, in the package `ItemGroup`:
```xml
    <PackageReference Include="Microsoft.Data.Sqlite" Version="10.0.13" />
```
and a new `ItemGroup`:
```xml
  <ItemGroup>
    <!-- Schema scripts, applied in order by AppDb; LogicalName keeps the resource names predictable. -->
    <EmbeddedResource Include="Accounts\Migrations\*.sql" LogicalName="migrations/%(Filename)%(Extension)" />
  </ItemGroup>
```

- [ ] **Step 4: Build and commit**

```powershell
dotnet build -c Release
git add .gitignore src/WebAudioArchive.Web/WebAudioArchive.Web.csproj
git commit -m "Sign-in groundwork: SQLite package and ignore rules for secrets and user exports (#88)"
```

### Task B2: Sign-in settings

**Files:**
- Create: `src/WebAudioArchive.Web/Accounts/AuthSettings.cs`, `src/WebAudioArchive.Web/Accounts/Emails.cs`
- Test: `tests/WebAudioArchive.Tests/AuthSettingsTests.cs`

**Interfaces:**
- Produces: `AuthSettings.Load(Func<string, string?>)` (throws `SettingsException` naming the setting, never echoing a secret); `AuthSettings { Uri PublicUrl; InstanceMode Mode; string? BootstrapManagerEmail; OAuthClient? Google, GitHub; string AppDir; TimeSpan IdleTimeout, MaxSessionAge; IReadOnlyList<IPNetwork> TrustedProxies; string? DataProtectionCertFile, DataProtectionCertPassword; bool IsHttps; string Origin; string DbPath, KeysDir }`; `enum InstanceMode { ManagerOnly, Primary }`; `record OAuthClient(string ClientId, string ClientSecret)` whose `ToString` hides the secret; `Emails.Normalise(string?) : string?`.

- [ ] **Step 1: Write the failing tests**

`tests/WebAudioArchive.Tests/AuthSettingsTests.cs`:
```csharp
extern alias web;

using web::WebAudioArchive.Web;
using web::WebAudioArchive.Web.Accounts;

namespace WebAudioArchive.Tests;

public sealed class AuthSettingsTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "waa-authsettings-" + Guid.NewGuid().ToString("N"));

    public AuthSettingsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    static readonly (string, string)[] Minimal =
    [
        ("WAA_PUBLIC_URL", "http://localhost:8090"),
        ("WAA_GOOGLE_CLIENT_ID", "id-123.apps.googleusercontent.com"),
        ("WAA_GOOGLE_CLIENT_SECRET", "s3cret-value"),
    ];

    static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var d = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (k, v) in pairs) d[k] = v;
        return k => d.TryGetValue(k, out var v) ? v : null;
    }

    static Func<string, string?> With(params (string Key, string Value)[] extra) => Env([.. Minimal, .. extra]);

    static string Fails(Func<string, string?> env) => Assert.Throws<SettingsException>(() => AuthSettings.Load(env)).Message;

    [Fact]
    public void Defaults_are_manager_only_with_four_hour_idle_and_one_day_sessions()
    {
        var a = AuthSettings.Load(Env(Minimal));

        Assert.Equal(InstanceMode.ManagerOnly, a.Mode);
        Assert.Equal(TimeSpan.FromHours(4), a.IdleTimeout);
        Assert.Equal(TimeSpan.FromHours(24), a.MaxSessionAge);
        Assert.Equal("/data/web", a.AppDir);
        Assert.Equal("http://localhost:8090", a.Origin);
        Assert.False(a.IsHttps);
        Assert.Equal("id-123.apps.googleusercontent.com", a.Google!.ClientId);
        Assert.Null(a.GitHub);
        Assert.Null(a.BootstrapManagerEmail);
        Assert.Empty(a.TrustedProxies);
    }

    [Theory]
    [InlineData(null, "WAA_PUBLIC_URL is not set")]
    [InlineData("localhost:8090", "must start with http:// or https://")]
    [InlineData("ftp://localhost", "must start with http:// or https://")]
    [InlineData("http://192.168.1.10:8090", "plain http is allowed only for localhost")]
    [InlineData("http://archive.example.test", "plain http is allowed only for localhost")]
    [InlineData("https://archive.example.test/app", "scheme, host and optional port only")]
    [InlineData("https://archive.example.test/?x=1", "scheme, host and optional port only")]
    public void Public_url_must_be_https_or_loopback_http_with_no_path(string? url, string message)
    {
        var env = url is null ? Env(Minimal[1..]) : With(("WAA_PUBLIC_URL", url));
        Assert.Contains(message, Fails(env));
    }

    [Fact]
    public void Https_public_url_is_accepted_and_trailing_slash_is_dropped()
    {
        var a = AuthSettings.Load(With(("WAA_PUBLIC_URL", "https://archive.example.test/")));
        Assert.True(a.IsHttps);
        Assert.Equal("https://archive.example.test", a.Origin);
    }

    [Fact]
    public void A_secret_file_is_read_and_no_secret_appears_in_errors_or_ToString()
    {
        var file = Path.Combine(_root, "google_client_secret.txt");
        File.WriteAllText(file, "file-secret-value\n");
        var a = AuthSettings.Load(Env(("WAA_PUBLIC_URL", "http://localhost:8090"), ("WAA_GOOGLE_CLIENT_ID", "id"), ("WAA_GOOGLE_CLIENT_SECRET_FILE", file)));

        Assert.Equal("file-secret-value", a.Google!.ClientSecret);
        Assert.DoesNotContain("file-secret-value", a.Google.ToString());
        Assert.DoesNotContain("file-secret-value", a.ToString());

        var both = Fails(With(("WAA_GOOGLE_CLIENT_SECRET_FILE", file)));
        Assert.Contains("only one of WAA_GOOGLE_CLIENT_SECRET and WAA_GOOGLE_CLIENT_SECRET_FILE", both);
        Assert.DoesNotContain("s3cret-value", both);

        Assert.Contains("does not exist", Fails(Env(("WAA_PUBLIC_URL", "http://localhost:8090"), ("WAA_GOOGLE_CLIENT_ID", "id"), ("WAA_GOOGLE_CLIENT_SECRET_FILE", Path.Combine(_root, "missing.txt")))));
    }

    [Fact]
    public void Client_id_and_secret_must_come_together_and_one_provider_is_required()
    {
        Assert.Contains("WAA_GITHUB_CLIENT_ID and its secret must be set together", Fails(With(("WAA_GITHUB_CLIENT_ID", "gh-id"))));
        Assert.Contains("WAA_GITHUB_CLIENT_ID and its secret must be set together", Fails(With(("WAA_GITHUB_CLIENT_SECRET", "gh-secret"))));
        // The compose file always passes the secret file paths; without a client id the provider is simply off.
        Assert.Null(AuthSettings.Load(With(("WAA_GITHUB_CLIENT_SECRET_FILE", Path.Combine(_root, "none.txt")))).GitHub);
        Assert.Contains("No sign-in provider is configured", Fails(Env(("WAA_PUBLIC_URL", "http://localhost:8090"))));
        var both = AuthSettings.Load(With(("WAA_GITHUB_CLIENT_ID", "gh-id"), ("WAA_GITHUB_CLIENT_SECRET", "gh-secret")));
        Assert.Equal("gh-id", both.GitHub!.ClientId);
    }

    [Theory]
    [InlineData(" Manager@Example.TEST ", "manager@example.test")]
    [InlineData("manager@example.test", "manager@example.test")]
    public void Bootstrap_email_is_normalised(string given, string expected)
        => Assert.Equal(expected, AuthSettings.Load(With(("BOOTSTRAP_MANAGER_EMAIL", given))).BootstrapManagerEmail);

    [Theory]
    [InlineData("not an email")]
    [InlineData("Manager <manager@example.test>")]
    [InlineData("manager@")]
    public void Bootstrap_email_must_be_an_address(string given)
        => Assert.Contains("BOOTSTRAP_MANAGER_EMAIL", Fails(With(("BOOTSTRAP_MANAGER_EMAIL", given))));

    [Theory]
    [InlineData("primary", "Primary")]
    [InlineData("PRIMARY", "Primary")]
    [InlineData("manager-only", "ManagerOnly")]
    public void Instance_mode_accepts_primary_and_manager_only(string given, string expected)
        => Assert.Equal(expected, AuthSettings.Load(With(("INSTANCE_MODE", given))).Mode.ToString());

    [Fact]
    public void Instance_mode_typo_is_refused()
        => Assert.Contains("INSTANCE_MODE: 'primay' is not primary or manager-only", Fails(With(("INSTANCE_MODE", "primay"))));

    [Fact]
    public void Trusted_proxies_parse_addresses_and_networks()
    {
        var a = AuthSettings.Load(With(("WAA_TRUSTED_PROXIES", "10.42.0.0/16, 192.168.1.5,fd00::/8")));
        Assert.Equal(new[] { "10.42.0.0/16", "192.168.1.5/32", "fd00::/8" }, a.TrustedProxies.Select(n => n.ToString()));
        Assert.Contains("WAA_TRUSTED_PROXIES: 'nonsense'", Fails(With(("WAA_TRUSTED_PROXIES", "nonsense"))));
    }

    [Theory]
    [InlineData("WAA_SESSION_IDLE_MINUTES", "4")]
    [InlineData("WAA_SESSION_IDLE_MINUTES", "1441")]
    [InlineData("WAA_SESSION_MAX_HOURS", "0")]
    [InlineData("WAA_SESSION_MAX_HOURS", "721")]
    [InlineData("WAA_SESSION_IDLE_MINUTES", "abc")]
    public void Session_timings_are_bounded(string name, string value)
        => Assert.Contains(name, Fails(With((name, value))));

    [Fact]
    public void Idle_timeout_may_not_exceed_the_session_age()
        => Assert.Contains("not shorter than WAA_SESSION_IDLE_MINUTES", Fails(With(("WAA_SESSION_IDLE_MINUTES", "180"), ("WAA_SESSION_MAX_HOURS", "2"))));

    [Fact]
    public void Data_protection_certificate_needs_both_files()
    {
        var pfx = Path.Combine(_root, "dp.pfx");
        File.WriteAllBytes(pfx, [1]);
        Assert.Contains("WAA_DP_CERT_PASSWORD_FILE", Fails(With(("WAA_DP_CERT_FILE", pfx))));
    }
}
```

- [ ] **Step 2: Run them and see them fail**

```powershell
dotnet test -c Release --filter "FullyQualifiedName~AuthSettingsTests"
```
Expected: compile errors, `AuthSettings` does not exist.

- [ ] **Step 3: Implement**

`src/WebAudioArchive.Web/Accounts/Emails.cs`:
```csharp
using System.Net.Mail;

namespace WebAudioArchive.Web.Accounts;

/// <summary>Email addresses as the allow-list stores them: trimmed, lower case, a bare address only.</summary>
public static class Emails
{
    public static string? Normalise(string? text)
    {
        var t = text?.Trim();
        if (string.IsNullOrEmpty(t) || t.Length > 254 || t.Any(char.IsWhiteSpace)) return null;
        if (!MailAddress.TryCreate(t, out var address) || !string.Equals(address.Address, t, StringComparison.OrdinalIgnoreCase)) return null;
        return t.ToLowerInvariant();
    }
}
```

`src/WebAudioArchive.Web/Accounts/AuthSettings.cs`:
```csharp
using System.Globalization;
using System.Net;

namespace WebAudioArchive.Web.Accounts;

public enum InstanceMode { ManagerOnly, Primary }

/// <summary>An OAuth client registered with Google or GitHub. The secret never appears in ToString, logs or errors.</summary>
public sealed record OAuthClient(string ClientId, string ClientSecret)
{
    public override string ToString() => $"OAuthClient {{ ClientId = {ClientId}, ClientSecret = (hidden) }}";
}

/// <summary>
/// Sign-in settings, from environment variables (or any configuration source with the same names). See the table
/// in docs/operations.md, "Sign-in". Every problem throws <see cref="SettingsException"/> naming the setting.
/// </summary>
public sealed record AuthSettings(
    Uri PublicUrl,
    InstanceMode Mode,
    string? BootstrapManagerEmail,
    OAuthClient? Google,
    OAuthClient? GitHub,
    string AppDir,
    TimeSpan IdleTimeout,
    TimeSpan MaxSessionAge,
    IReadOnlyList<System.Net.IPNetwork> TrustedProxies,
    string? DataProtectionCertFile,
    string? DataProtectionCertPassword)
{
    public bool IsHttps => PublicUrl.Scheme == Uri.UriSchemeHttps;

    /// <summary>Scheme, host and port, no trailing slash: what browsers send as Origin.</summary>
    public string Origin => PublicUrl.GetLeftPart(UriPartial.Authority);

    public string DbPath => Path.Combine(AppDir, "app.db");

    public string KeysDir => Path.Combine(AppDir, "keys");

    public override string ToString() =>
        $"AuthSettings {{ {Origin}, {Mode}, Google = {Google is not null}, GitHub = {GitHub is not null}, AppDir = {AppDir} }}";

    public static AuthSettings Load(Func<string, string?> get)
    {
        var url = PublicUrlFrom(get("WAA_PUBLIC_URL"));
        var mode = (get("INSTANCE_MODE") ?? "manager-only").Trim().ToLowerInvariant() switch
        {
            "manager-only" => InstanceMode.ManagerOnly,
            "primary" => InstanceMode.Primary,
            var other => throw new SettingsException($"INSTANCE_MODE: '{other}' is not primary or manager-only."),
        };

        string? bootstrap = null;
        var bootstrapText = get("BOOTSTRAP_MANAGER_EMAIL");
        if (!string.IsNullOrWhiteSpace(bootstrapText))
            bootstrap = Emails.Normalise(bootstrapText) ?? throw new SettingsException($"BOOTSTRAP_MANAGER_EMAIL: '{bootstrapText.Trim()}' is not an email address.");

        var google = Client(get, "GOOGLE");
        var github = Client(get, "GITHUB");
        if (google is null && github is null)
            throw new SettingsException("No sign-in provider is configured. Set WAA_GOOGLE_CLIENT_ID with WAA_GOOGLE_CLIENT_SECRET (or WAA_GOOGLE_CLIENT_SECRET_FILE), or the WAA_GITHUB_ pair. See docs/operations.md, Sign-in.");

        var idle = TimeSpan.FromMinutes(Number(get, "WAA_SESSION_IDLE_MINUTES", 240, 5, 1440));
        var max = TimeSpan.FromHours(Number(get, "WAA_SESSION_MAX_HOURS", 24, 1, 720));
        if (max < idle) throw new SettingsException("WAA_SESSION_MAX_HOURS must be not shorter than WAA_SESSION_IDLE_MINUTES.");

        var certFile = ExistingFile(get, "WAA_DP_CERT_FILE");
        var passwordFile = ExistingFile(get, "WAA_DP_CERT_PASSWORD_FILE");
        if ((certFile is null) != (passwordFile is null))
            throw new SettingsException("WAA_DP_CERT_FILE and WAA_DP_CERT_PASSWORD_FILE must be set together.");

        var appDir = get("WAA_APP_DIR");
        return new AuthSettings(
            url, mode, bootstrap, google, github,
            string.IsNullOrWhiteSpace(appDir) ? "/data/web" : appDir.Trim(),
            idle, max,
            Proxies(get("WAA_TRUSTED_PROXIES")),
            certFile,
            passwordFile is null ? null : File.ReadAllText(passwordFile).Trim());
    }

    static Uri PublicUrlFrom(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new SettingsException("WAA_PUBLIC_URL is not set. Give the address people type to reach this app, for example WAA_PUBLIC_URL=http://localhost:8090");
        var t = text.Trim();
        if (!t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !t.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(t, UriKind.Absolute, out var url))
            throw new SettingsException($"WAA_PUBLIC_URL: '{t}' must start with http:// or https://.");
        if (url.AbsolutePath != "/" || url.Query.Length > 0 || url.Fragment.Length > 0 || url.UserInfo.Length > 0)
            throw new SettingsException($"WAA_PUBLIC_URL: '{t}' must be a scheme, host and optional port only, for example https://archive.example.org or http://localhost:8090.");
        if (url.Scheme == Uri.UriSchemeHttp && !url.IsLoopback)
            throw new SettingsException($"WAA_PUBLIC_URL: '{t}': plain http is allowed only for localhost; use https for any other host.");
        return new Uri(url.GetLeftPart(UriPartial.Authority));
    }

    static OAuthClient? Client(Func<string, string?> get, string name)
    {
        var id = get($"WAA_{name}_CLIENT_ID")?.Trim();
        var secret = get($"WAA_{name}_CLIENT_SECRET")?.Trim();
        var file = get($"WAA_{name}_CLIENT_SECRET_FILE")?.Trim();
        if (!string.IsNullOrEmpty(secret) && !string.IsNullOrEmpty(file))
            throw new SettingsException($"Set only one of WAA_{name}_CLIENT_SECRET and WAA_{name}_CLIENT_SECRET_FILE.");
        if (string.IsNullOrEmpty(id))
        {
            // A secret file path alone does not turn a provider on: the compose file always passes both paths.
            if (!string.IsNullOrEmpty(secret)) throw new SettingsException($"WAA_{name}_CLIENT_ID and its secret must be set together.");
            return null;
        }

        if (!string.IsNullOrEmpty(file))
        {
            if (!File.Exists(file)) throw new SettingsException($"WAA_{name}_CLIENT_SECRET_FILE: '{file}' does not exist.");
            secret = File.ReadAllText(file).Trim();
        }

        if (string.IsNullOrEmpty(secret)) throw new SettingsException($"WAA_{name}_CLIENT_ID and its secret must be set together.");
        return new OAuthClient(id, secret);
    }

    static int Number(Func<string, string?> get, string name, int fallback, int min, int max)
    {
        var text = get(name);
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < min || n > max)
            throw new SettingsException($"{name}: '{text.Trim()}' is not a whole number from {min} to {max}.");
        return n;
    }

    static string? ExistingFile(Func<string, string?> get, string name)
    {
        var file = get(name)?.Trim();
        if (string.IsNullOrEmpty(file)) return null;
        return File.Exists(file) ? file : throw new SettingsException($"{name}: '{file}' does not exist.");
    }

    static List<System.Net.IPNetwork> Proxies(string? text)
    {
        var list = new List<System.Net.IPNetwork>();
        foreach (var part in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (System.Net.IPNetwork.TryParse(part, out var network)) list.Add(network);
            else if (IPAddress.TryParse(part, out var ip)) list.Add(new System.Net.IPNetwork(ip, ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128));
            else throw new SettingsException($"WAA_TRUSTED_PROXIES: '{part}' is not an IP address or a network such as 10.42.0.0/16.");
        }

        return list;
    }
}
```
(`System.Net.IPNetwork` is written in full because ASP.NET Core also has an older `IPNetwork` type in `Microsoft.AspNetCore.HttpOverrides`, obsolete in .NET 10.)

- [ ] **Step 4: Run and commit**

```powershell
dotnet test -c Release --filter "FullyQualifiedName~AuthSettingsTests"
dotnet build -c Release
git add src/WebAudioArchive.Web/Accounts/AuthSettings.cs src/WebAudioArchive.Web/Accounts/Emails.cs tests/WebAudioArchive.Tests/AuthSettingsTests.cs
git commit -m "Sign-in settings: public URL rules, instance mode, bootstrap manager, provider secrets from files (#92, #101)"
```
Expected: all pass, 0 warnings.

### Task B3: SQLite store, migrations and backups

**Files:**
- Create: `src/WebAudioArchive.Web/Accounts/Sql.cs`, `src/WebAudioArchive.Web/Accounts/AppDb.cs`, `src/WebAudioArchive.Web/Accounts/Migrations/0001_initial.sql`
- Test: `tests/WebAudioArchive.Tests/AppDbTests.cs`

**Interfaces:**
- Produces: `AppDb(string path, IReadOnlyList<string>? scripts = null)` with `Open()`, `Migrate(DateTimeOffset now, ILogger log) : int`, `BackupNow(DateTimeOffset now, int keep = 14) : string`, `SchemaVersion`, `BackupsDir`; `AppDb.PrepareDirectory(string dir, ILogger? log)`; `Sql` helpers `Exec`, `ScalarLong`, `ScalarText`, `Query<T>`, `Text(DateTimeOffset)`, `Time(string)`, `StringOrNull`.
- Schema version 1: tables `users`, `user_logins`, `sessions`, `assignments`, `audit`.

- [ ] **Step 1: Write the failing tests**

`tests/WebAudioArchive.Tests/AppDbTests.cs`:
```csharp
extern alias web;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using web::WebAudioArchive.Web;
using web::WebAudioArchive.Web.Accounts;

namespace WebAudioArchive.Tests;

public sealed class AppDbTests : IDisposable
{
    static readonly DateTimeOffset Now = new(2026, 10, 16, 9, 30, 0, TimeSpan.Zero);
    readonly string _dir = Path.Combine(Path.GetTempPath(), "waa-db-" + Guid.NewGuid().ToString("N"));
    string DbPath => Path.Combine(_dir, "app.db");

    public AppDbTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void A_fresh_database_gets_every_table_at_schema_version_1_in_wal_mode()
    {
        var db = new AppDb(DbPath);
        Assert.Equal(1, db.Migrate(Now, NullLogger.Instance));

        using var c = db.Open();
        var tables = c.Query("SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name", r => r.GetString(0));
        Assert.Equal(new[] { "assignments", "audit", "sessions", "user_logins", "users" }, tables);
        Assert.Equal(1, c.ScalarLong("PRAGMA user_version;"));
        Assert.Equal("wal", c.ScalarText("PRAGMA journal_mode;"));
        Assert.Equal(1, c.ScalarLong("PRAGMA foreign_keys;"));
    }

    [Fact]
    public void Migrating_again_changes_nothing_and_makes_no_backup()
    {
        var db = new AppDb(DbPath);
        db.Migrate(Now, NullLogger.Instance);
        Assert.Equal(1, db.Migrate(Now.AddHours(1), NullLogger.Instance));
        Assert.False(Directory.Exists(db.BackupsDir));
    }

    [Fact]
    public void A_newer_database_is_refused_naming_the_versions()
    {
        var db = new AppDb(DbPath);
        db.Migrate(Now, NullLogger.Instance);
        using (var c = db.Open()) c.Exec("PRAGMA user_version = 99;");

        var message = Assert.Throws<SettingsException>(() => db.Migrate(Now, NullLogger.Instance)).Message;
        Assert.Contains("schema version 99", message);
        Assert.Contains("this build understands (1)", message);
    }

    [Fact]
    public void An_upgrade_backs_up_the_existing_database_first()
    {
        var v1 = AppDb.BuiltInScripts();
        new AppDb(DbPath, v1).Migrate(Now, NullLogger.Instance);
        using (var c = new AppDb(DbPath, v1).Open())
            c.Exec("INSERT INTO users (email, role, created_utc, created_by) VALUES ('kept@example.test', 'manager', $t, 'test')", ("$t", Sql.Text(Now)));

        var v2 = new AppDb(DbPath, [.. v1, "CREATE TABLE extra (id INTEGER PRIMARY KEY);"]);
        Assert.Equal(2, v2.Migrate(Now, NullLogger.Instance));

        var backup = Assert.Single(Directory.GetFiles(v2.BackupsDir));
        Assert.Equal("app-before-v2-20261016T093000Z.db", Path.GetFileName(backup));
        using var b = new SqliteConnection($"Data Source={backup};Mode=ReadOnly");
        b.Open();
        Assert.Equal("kept@example.test", b.ScalarText("SELECT email FROM users"));
    }

    [Fact]
    public void A_failing_script_rolls_back_and_leaves_the_version()
    {
        var v1 = AppDb.BuiltInScripts();
        new AppDb(DbPath, v1).Migrate(Now, NullLogger.Instance);
        var broken = new AppDb(DbPath, [.. v1, "CREATE TABLE half (id INTEGER); THIS IS NOT SQL;"]);

        Assert.ThrowsAny<SqliteException>(() => broken.Migrate(Now, NullLogger.Instance));
        using var c = broken.Open();
        Assert.Equal(1, c.ScalarLong("PRAGMA user_version;"));
        Assert.Equal(0, c.ScalarLong("SELECT count(*) FROM sqlite_master WHERE name = 'half'"));
    }

    [Fact]
    public void Foreign_keys_cascade_from_users()
    {
        var db = new AppDb(DbPath);
        db.Migrate(Now, NullLogger.Instance);
        using var c = db.Open();
        c.Exec("INSERT INTO users (id, email, role, created_utc, created_by) VALUES (1, 'a@example.test', 'presenter', $t, 'test')", ("$t", Sql.Text(Now)));
        c.Exec("INSERT INTO sessions (id_hash, user_id, provider, created_utc, last_seen_utc, expires_utc) VALUES ('h', 1, 'google', $t, $t, $t)", ("$t", Sql.Text(Now)));
        Assert.ThrowsAny<SqliteException>(() => c.Exec("INSERT INTO sessions (id_hash, user_id, provider, created_utc, last_seen_utc, expires_utc) VALUES ('x', 42, 'google', $t, $t, $t)", ("$t", Sql.Text(Now))));
        Assert.ThrowsAny<SqliteException>(() => c.Exec("INSERT INTO users (email, role, created_utc, created_by) VALUES ('b@example.test', 'admin', $t, 'test')", ("$t", Sql.Text(Now))));
        c.Exec("DELETE FROM users WHERE id = 1");
        Assert.Equal(0, c.ScalarLong("SELECT count(*) FROM sessions"));
    }

    [Fact]
    public void Daily_backups_keep_the_newest_fourteen()
    {
        var db = new AppDb(DbPath);
        db.Migrate(Now, NullLogger.Instance);
        for (var day = 0; day < 16; day++) db.BackupNow(Now.AddDays(day));

        var kept = Directory.GetFiles(db.BackupsDir, "app-2*.db").Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(14, kept.Count);
        Assert.Equal("app-20261018T093000Z.db", kept[0]);
        Assert.Equal("app-20261031T093000Z.db", kept[^1]);
    }

    [Fact]
    public void Times_are_stored_as_sortable_utc_text()
    {
        var t = new DateTimeOffset(2026, 10, 16, 10, 30, 0, 250, TimeSpan.FromHours(1));
        Assert.Equal("2026-10-16T09:30:00.250Z", Sql.Text(t));
        Assert.Equal(t, Sql.Time("2026-10-16T09:30:00.250Z"));
        Assert.Equal(TimeSpan.Zero, Sql.Time("2026-10-16T09:30:00.250Z").Offset);
    }

    [Fact]
    public void An_unwritable_app_directory_is_a_settings_error()
    {
        var file = Path.Combine(_dir, "not-a-folder");
        File.WriteAllText(file, "x");
        Assert.Contains("WAA_APP_DIR", Assert.Throws<SettingsException>(() => AppDb.PrepareDirectory(Path.Combine(file, "web"), null)).Message);
    }

    [Fact]
    public void The_app_directory_is_private_on_linux()
    {
        var dir = Path.Combine(_dir, "web");
        AppDb.PrepareDirectory(dir, null);
        Assert.True(Directory.Exists(Path.Combine(dir, "keys")) && Directory.Exists(Path.Combine(dir, "backups")));
        if (OperatingSystem.IsWindows()) return; // CI runs on Linux, where the mode is checked
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(dir));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(Path.Combine(dir, "keys")));
    }
}
```

- [ ] **Step 2: Run them and see them fail** (`dotnet build -c Release`: `AppDb` and `Sql` do not exist.)

- [ ] **Step 3: Implement**

`src/WebAudioArchive.Web/Accounts/Migrations/0001_initial.sql`:
```sql
-- Schema version 1. Times are UTC text, yyyy-MM-ddTHH:mm:ss.fffZ, so they sort as text.
CREATE TABLE users (
    id               INTEGER PRIMARY KEY,
    email            TEXT    NOT NULL UNIQUE,             -- trimmed, lower case
    display_name     TEXT    NULL,
    role             TEXT    NOT NULL CHECK (role IN ('manager', 'presenter')),
    enabled          INTEGER NOT NULL DEFAULT 1 CHECK (enabled IN (0, 1)),
    created_utc      TEXT    NOT NULL,
    created_by       TEXT    NOT NULL,                    -- a manager's email, 'bootstrap' or 'system'
    last_sign_in_utc TEXT    NULL
);

CREATE TABLE user_logins (
    provider      TEXT    NOT NULL CHECK (provider IN ('google', 'github')),
    subject       TEXT    NOT NULL,                       -- Google sub, GitHub numeric id
    user_id       INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    email_at_bind TEXT    NOT NULL,
    bound_utc     TEXT    NOT NULL,
    PRIMARY KEY (provider, subject),
    UNIQUE (user_id, provider)                            -- one Google and one GitHub account per person
);

CREATE TABLE sessions (
    id_hash       TEXT    PRIMARY KEY,                    -- SHA-256 (hex) of the random id in the session cookie
    user_id       INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    provider      TEXT    NOT NULL,
    created_utc   TEXT    NOT NULL,
    last_seen_utc TEXT    NOT NULL,
    expires_utc   TEXT    NOT NULL
);
CREATE INDEX sessions_user ON sessions (user_id);

CREATE TABLE assignments (
    id          INTEGER PRIMARY KEY,
    user_id     INTEGER NOT NULL REFERENCES users (id) ON DELETE CASCADE,
    station     TEXT    NOT NULL,
    show_title  TEXT    NOT NULL,
    from_utc    TEXT    NULL,                             -- inclusive; null: no lower bound
    to_utc      TEXT    NULL,                             -- exclusive; null: no upper bound
    created_utc TEXT    NOT NULL,
    created_by  TEXT    NOT NULL
);
CREATE INDEX assignments_user ON assignments (user_id);

CREATE TABLE audit (
    id        INTEGER PRIMARY KEY,
    at_utc    TEXT    NOT NULL,
    user_id   INTEGER NULL,                               -- no foreign key: the entry outlives the user
    email     TEXT    NULL,
    action    TEXT    NOT NULL,
    target    TEXT    NULL,
    detail    TEXT    NULL,
    remote_ip TEXT    NULL
);
CREATE INDEX audit_at ON audit (at_utc);
```

`src/WebAudioArchive.Web/Accounts/Sql.cs`:
```csharp
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WebAudioArchive.Web.Accounts;

/// <summary>Small helpers over Microsoft.Data.Sqlite: named parameters only (never string-built values), UTC text times.</summary>
public static class Sql
{
    const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public static int Exec(this SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, null, sql, args);
        return cmd.ExecuteNonQuery();
    }

    public static int Exec(this SqliteConnection c, SqliteTransaction tx, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, tx, sql, args);
        return cmd.ExecuteNonQuery();
    }

    public static long? ScalarLong(this SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, null, sql, args);
        return cmd.ExecuteScalar() is long v ? v : null;
    }

    public static string? ScalarText(this SqliteConnection c, string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, null, sql, args);
        return cmd.ExecuteScalar() as string;
    }

    public static List<T> Query<T>(this SqliteConnection c, string sql, Func<SqliteDataReader, T> map, params (string Name, object? Value)[] args)
    {
        using var cmd = Command(c, null, sql, args);
        using var reader = cmd.ExecuteReader();
        var list = new List<T>();
        while (reader.Read()) list.Add(map(reader));
        return list;
    }

    public static string? StringOrNull(this SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);

    public static string Text(DateTimeOffset t) => t.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture);

    public static string? Text(DateTimeOffset? t) => t is { } v ? Text(v) : null;

    public static DateTimeOffset Time(string text) =>
        DateTimeOffset.ParseExact(text, Format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    static SqliteCommand Command(SqliteConnection c, SqliteTransaction? tx, string sql, (string Name, object? Value)[] args)
    {
        var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.Transaction = tx;
        foreach (var (name, value) in args) cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
```

`src/WebAudioArchive.Web/Accounts/AppDb.cs`:
```csharp
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace WebAudioArchive.Web.Accounts;

/// <summary>
/// The instance's SQLite database (users, logins, sessions, assignments, audit) in WAA_APP_DIR: WAL journal, foreign
/// keys on, a 5 second busy timeout. The schema is a numbered list of SQL scripts and PRAGMA user_version counts the
/// ones applied; an upgrade backs up the file first; a database from a newer build is refused.
/// </summary>
public sealed class AppDb
{
    readonly string _connectionString;
    readonly IReadOnlyList<string> _scripts;

    public AppDb(string path, IReadOnlyList<string>? scripts = null)
    {
        DbPath = path;
        _scripts = scripts ?? BuiltInScripts();
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true,
            DefaultTimeout = 5,
        }.ToString();
    }

    public string DbPath { get; }

    public int SchemaVersion => _scripts.Count;

    public string BackupsDir => Path.Combine(Path.GetDirectoryName(DbPath)!, "backups");

    public SqliteConnection Open()
    {
        var c = new SqliteConnection(_connectionString);
        c.Open();
        c.Exec("PRAGMA busy_timeout = 5000;");
        return c;
    }

    public int Migrate(DateTimeOffset now, ILogger log)
    {
        var existed = File.Exists(DbPath) && new FileInfo(DbPath).Length > 0;
        using var c = Open();
        c.Exec("PRAGMA journal_mode = WAL;");
        var version = (int)(c.ScalarLong("PRAGMA user_version;") ?? 0);
        if (version > SchemaVersion)
            throw new SettingsException($"WAA_APP_DIR: {DbPath} has schema version {version}, newer than this build understands ({SchemaVersion}). Run the newer build, or restore a backup from {BackupsDir}.");
        if (version == SchemaVersion) return version;

        if (existed && version > 0) Backup(c, Path.Combine(BackupsDir, $"app-before-v{SchemaVersion}-{Stamp(now)}.db"));
        for (var v = version + 1; v <= SchemaVersion; v++)
        {
            using var tx = c.BeginTransaction();
            c.Exec(tx, _scripts[v - 1]);
            c.Exec(tx, string.Create(CultureInfo.InvariantCulture, $"PRAGMA user_version = {v};"));
            tx.Commit();
            log.LogInformation("Database {Path} is at schema version {Version}", DbPath, v);
        }

        return SchemaVersion;
    }

    /// <summary>A consistent copy (VACUUM INTO) named by the UTC time; keeps the newest <paramref name="keep"/>.</summary>
    public string BackupNow(DateTimeOffset now, int keep = 14)
    {
        var target = Path.Combine(BackupsDir, $"app-{Stamp(now)}.db");
        using (var c = Open()) Backup(c, target);
        foreach (var old in Directory.GetFiles(BackupsDir, "app-2*.db").OrderDescending(StringComparer.Ordinal).Skip(keep))
            File.Delete(old);
        return target;
    }

    /// <summary>Creates WAA_APP_DIR with keys/ and backups/, private to this user on Linux, and checks it is writable.</summary>
    public static void PrepareDirectory(string dir, ILogger? log)
    {
        try
        {
            foreach (var d in new[] { dir, Path.Combine(dir, "keys"), Path.Combine(dir, "backups") })
                Directory.CreateDirectory(d);
            var probe = Path.Combine(dir, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new SettingsException($"WAA_APP_DIR: '{dir}' cannot be created or written by this user ({ex.Message}). Create it and, on Linux, give it to the container user: sudo chown 1654:1654 <data>/web");
        }

        if (OperatingSystem.IsWindows()) return;
        foreach (var d in new[] { dir, Path.Combine(dir, "keys"), Path.Combine(dir, "backups") })
        {
            try
            {
                File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Some bind mounts (Docker Desktop on Windows) ignore or refuse chmod; the host's own permissions apply.
                log?.LogWarning("Could not make {Dir} private (mode 700): {Error}", d, ex.Message);
            }
        }
    }

    public static IReadOnlyList<string> BuiltInScripts()
    {
        var assembly = typeof(AppDb).Assembly;
        var names = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith("migrations/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToList();
        var scripts = new List<string>();
        for (var i = 0; i < names.Count; i++)
        {
            var expected = string.Create(CultureInfo.InvariantCulture, $"migrations/{i + 1:0000}_");
            if (!names[i].StartsWith(expected, StringComparison.Ordinal))
                throw new InvalidOperationException($"Migration scripts must be numbered from 0001 without gaps; found {names[i]} where {expected}* was expected.");
            using var stream = assembly.GetManifestResourceStream(names[i])!;
            using var reader = new StreamReader(stream);
            scripts.Add(reader.ReadToEnd());
        }

        return scripts.Count > 0 ? scripts : throw new InvalidOperationException("No migration scripts are embedded; check the EmbeddedResource item in the web project.");
    }

    static void Backup(SqliteConnection c, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (File.Exists(target)) File.Delete(target);
        c.Exec("VACUUM INTO $target;", ("$target", target));
    }

    static string Stamp(DateTimeOffset t) => t.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
}
```

- [ ] **Step 4: Run and commit**

```powershell
dotnet test -c Release --filter "FullyQualifiedName~AppDbTests"
dotnet build -c Release
git add src/WebAudioArchive.Web/Accounts/Sql.cs src/WebAudioArchive.Web/Accounts/AppDb.cs src/WebAudioArchive.Web/Accounts/Migrations tests/WebAudioArchive.Tests/AppDbTests.cs
git commit -m "Per-instance SQLite store with numbered migrations, backup before upgrade and daily backups (#88)"
```
Expected: all pass (on Windows the mode assertions are not reached; CI on Linux runs them).

### Task B4: Roles, users, bootstrap manager, audit log

**Files:**
- Create: `src/WebAudioArchive.Web/Accounts/Roles.cs`, `src/WebAudioArchive.Web/Accounts/AuditLog.cs`, `src/WebAudioArchive.Web/Accounts/UserStore.cs`
- Test: `tests/WebAudioArchive.Tests/UserStoreTests.cs`, `tests/WebAudioArchive.Tests/Support/AccountsTestDb.cs`

**Interfaces:**
- Produces: `enum Role { Manager, Presenter }`; `Roles.Name(Role)`, `Roles.Parse(string?)`; `ArchiveClaims.SessionId = "waa:sid"`, `ArchiveClaims.UserId = "waa:uid"`; `ArchivePolicies.Manager = "manager"`, `ArchivePolicies.Member = "member"`; `record Actor(long? UserId, string Name)` with `Actor.Bootstrap`, `Actor.System`; `record Viewer(long UserId, string Email, string Name, Role Role)` with `IsManager`, `Actor`, `Viewer.From(ClaimsPrincipal?)`; `AuditActions` constants; `AuditLog.Write`, `AuditLog.WriteOnce`, `AuditLog.Query`; `UserStore` (below); `AccountException`.

- [ ] **Step 1: Test support**

`tests/WebAudioArchive.Tests/Support/AccountsTestDb.cs`:
```csharp
extern alias web;

using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using web::WebAudioArchive.Web.Accounts;

namespace WebAudioArchive.Tests.Support;

/// <summary>A migrated database in a temporary folder, with the stores over it, for unit tests.</summary>
public sealed class AccountsTestDb : IDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 16, 9, 0, 0, TimeSpan.Zero);
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "waa-accounts-" + Guid.NewGuid().ToString("N"));
    public ManualTimeProvider Time { get; } = new(Start);
    public AuthSettings Auth { get; }
    public AppDb Db { get; }
    public AuditLog Audit { get; }
    public UserStore Users { get; }

    public AccountsTestDb(InstanceMode mode = InstanceMode.Primary, string? bootstrap = "manager@example.test")
    {
        Directory.CreateDirectory(Dir);
        Auth = new AuthSettings(new Uri("https://localhost"), mode, bootstrap, new OAuthClient("g", "s"), new OAuthClient("h", "s"),
            Dir, TimeSpan.FromHours(4), TimeSpan.FromHours(24), [], null, null);
        Db = new AppDb(Path.Combine(Dir, "app.db"));
        Db.Migrate(Start, NullLogger.Instance);
        Audit = new AuditLog(Db, Time);
        Users = new UserStore(Db, Auth, Audit, Time);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Dir)) Directory.Delete(Dir, recursive: true);
    }
}
```

- [ ] **Step 2: Write the failing tests**

`tests/WebAudioArchive.Tests/UserStoreTests.cs`:
```csharp
extern alias web;

using System.Security.Claims;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Accounts;

namespace WebAudioArchive.Tests;

public sealed class UserStoreTests : IDisposable
{
    readonly AccountsTestDb _t = new();
    UserStore Users => _t.Users;

    public void Dispose() => _t.Dispose();

    Actor ManagerActor()
    {
        Users.EnsureBootstrapManager("manager@example.test");
        var m = Users.FindByEmail("manager@example.test")!;
        return new Actor(m.Id, m.Email);
    }

    [Fact]
    public void Add_normalises_the_email_refuses_duplicates_and_is_audited()
    {
        var actor = ManagerActor();
        var u = Users.Add("  Presenter@Example.TEST ", "Presenter", Role.Presenter, actor);

        Assert.Equal("presenter@example.test", u.Email);
        Assert.Equal(Role.Presenter, u.Role);
        Assert.True(u.Enabled);
        Assert.Equal(AccountsTestDb.Start, u.CreatedUtc);
        Assert.Equal("manager@example.test", u.CreatedBy);
        Assert.Contains("already on the list", Assert.Throws<AccountException>(() => Users.Add("presenter@example.test", null, Role.Manager, actor)).Message);
        Assert.Contains("not an email address", Assert.Throws<AccountException>(() => Users.Add("nope", null, Role.Manager, actor)).Message);
        var entry = _t.Audit.Query(null, AuditActions.UserAdded, "presenter@").Single();
        Assert.Equal("manager@example.test", entry.Email);
        Assert.Equal("role=presenter", entry.Detail);
    }

    [Fact]
    public void Presenters_cannot_be_added_on_a_manager_only_instance()
    {
        using var t = new AccountsTestDb(InstanceMode.ManagerOnly);
        t.Users.EnsureBootstrapManager("manager@example.test");
        Assert.Contains("manager-only instance", Assert.Throws<AccountException>(() => t.Users.Add("p@example.test", null, Role.Presenter, Actor.System)).Message);
    }

    [Fact]
    public void Bootstrap_manager_is_created_once_and_restored_at_start()
    {
        Users.EnsureBootstrapManager("manager@example.test");
        Users.EnsureBootstrapManager("manager@example.test");
        var m = Assert.Single(Users.All());
        Assert.Equal(Role.Manager, m.Role);
        Assert.Equal("bootstrap", m.CreatedBy);

        // Demoted or disabled by hand in the database: the configured email wins at the next start.
        using (var c = _t.Db.Open()) c.Exec("UPDATE users SET role = 'presenter', enabled = 0");
        Users.EnsureBootstrapManager("manager@example.test");
        m = Users.Get(m.Id)!;
        Assert.Equal(Role.Manager, m.Role);
        Assert.True(m.Enabled);
        Assert.Equal(2, _t.Audit.Query(null, null, "manager@example.test").Count(e => e.Email == "bootstrap"));
    }

    [Fact]
    public void Nobody_changes_their_own_role_or_disables_or_removes_themselves()
    {
        var me = ManagerActor();
        var id = me.UserId!.Value;
        Assert.Contains("your own role", Assert.Throws<AccountException>(() => Users.SetRole(id, Role.Presenter, me)).Message);
        Assert.Contains("yourself", Assert.Throws<AccountException>(() => Users.SetEnabled(id, false, me)).Message);
        Assert.Contains("yourself", Assert.Throws<AccountException>(() => Users.Remove(id, me)).Message);
    }

    [Fact]
    public void The_last_enabled_manager_and_the_bootstrap_manager_are_protected()
    {
        var boot = ManagerActor();
        var other = Users.Add("second@example.test", null, Role.Manager, boot);
        var otherActor = new Actor(other.Id, other.Email);

        Assert.Contains("BOOTSTRAP_MANAGER_EMAIL", Assert.Throws<AccountException>(() => Users.SetRole(boot.UserId!.Value, Role.Presenter, otherActor)).Message);
        Assert.Contains("BOOTSTRAP_MANAGER_EMAIL", Assert.Throws<AccountException>(() => Users.Remove(boot.UserId!.Value, otherActor)).Message);

        using var t = new AccountsTestDb(bootstrap: null);
        var a = t.Users.Add("a@example.test", null, Role.Manager, Actor.System);
        var b = t.Users.Add("b@example.test", null, Role.Manager, Actor.System);
        t.Users.SetEnabled(b.Id, false, new Actor(a.Id, a.Email));
        Assert.Contains("only enabled manager", Assert.Throws<AccountException>(() => t.Users.SetRole(a.Id, Role.Presenter, Actor.System)).Message);
        Assert.Contains("only enabled manager", Assert.Throws<AccountException>(() => t.Users.SetEnabled(a.Id, false, Actor.System)).Message);
        Assert.Contains("only enabled manager", Assert.Throws<AccountException>(() => t.Users.Remove(a.Id, Actor.System)).Message);
    }

    [Fact]
    public void Role_changes_and_disabling_end_the_users_sessions()
    {
        var boot = ManagerActor();
        var p = Users.Add("p@example.test", null, Role.Presenter, boot);
        using (var c = _t.Db.Open())
            c.Exec("INSERT INTO sessions VALUES ('h1', $u, 'google', $t, $t, $t)", ("$u", p.Id), ("$t", Sql.Text(AccountsTestDb.Start)));

        Users.SetRole(p.Id, Role.Manager, boot);

        using var c2 = _t.Db.Open();
        Assert.Equal(0, c2.ScalarLong("SELECT count(*) FROM sessions"));
    }

    [Fact]
    public void Removing_a_user_removes_their_logins_sessions_and_assignments_but_not_their_audit_entries()
    {
        var boot = ManagerActor();
        var p = Users.Add("p@example.test", null, Role.Presenter, boot);
        Users.Bind(p.Id, "google", "sub-p", "p@example.test");
        using (var c = _t.Db.Open())
        {
            c.Exec("INSERT INTO sessions VALUES ('h1', $u, 'google', $t, $t, $t)", ("$u", p.Id), ("$t", Sql.Text(AccountsTestDb.Start)));
            c.Exec("INSERT INTO assignments (user_id, station, show_title, created_utc, created_by) VALUES ($u, 'burst', 'Backseat Aux', $t, 'x')", ("$u", p.Id), ("$t", Sql.Text(AccountsTestDb.Start)));
        }

        Users.Remove(p.Id, boot);

        Assert.Null(Users.FindByLogin("google", "sub-p"));
        using var c2 = _t.Db.Open();
        Assert.Equal(0, c2.ScalarLong("SELECT count(*) FROM sessions") + c2.ScalarLong("SELECT count(*) FROM assignments") + c2.ScalarLong("SELECT count(*) FROM user_logins"));
        Assert.NotEmpty(_t.Audit.Query(null, AuditActions.UserRemoved, "p@example.test"));
    }

    [Fact]
    public void Bind_allows_one_login_per_provider_and_resetting_clears_them()
    {
        var boot = ManagerActor();
        var p = Users.Add("p@example.test", null, Role.Presenter, boot);
        Users.Bind(p.Id, "google", "sub-1", "p@example.test");
        Users.Bind(p.Id, "github", "1001", "p@example.test");

        Assert.Equal(p.Id, Users.FindByLogin("google", "sub-1")!.Id);
        Assert.Equal(new[] { "github", "google" }, Users.Get(p.Id)!.Logins.Select(l => l.Provider).Order());
        Assert.ThrowsAny<Microsoft.Data.Sqlite.SqliteException>(() => Users.Bind(p.Id, "google", "sub-2", "p@example.test"));

        Users.ResetLogins(p.Id, boot);
        Assert.Empty(Users.Get(p.Id)!.Logins);
    }

    [Fact]
    public void Viewer_is_read_from_the_session_claims_and_needs_a_role()
    {
        var identity = new ClaimsIdentity([new(ArchiveClaims.UserId, "7"), new(ClaimTypes.Email, "p@example.test"), new(ClaimTypes.Name, "P"), new(ClaimTypes.Role, "presenter")], "test", ClaimTypes.Name, ClaimTypes.Role);
        var viewer = Viewer.From(new ClaimsPrincipal(identity))!;
        Assert.Equal(new Viewer(7, "p@example.test", "P", Role.Presenter), viewer);
        Assert.False(viewer.IsManager);
        Assert.Null(Viewer.From(new ClaimsPrincipal(new ClaimsIdentity([new(ArchiveClaims.UserId, "7")], "test"))));
        Assert.Null(Viewer.From(new ClaimsPrincipal(new ClaimsIdentity())));
        Assert.Null(Viewer.From(null));
    }

    [Fact]
    public void WriteOnce_collapses_repeats_within_ten_minutes()
    {
        var who = new Actor(1, "m@example.test");
        Assert.True(_t.Audit.WriteOnce(AuditActions.ShowViewed, who, "burst-20261015T1900Z"));
        Assert.False(_t.Audit.WriteOnce(AuditActions.ShowViewed, who, "burst-20261015T1900Z"));
        Assert.True(_t.Audit.WriteOnce(AuditActions.ShowViewed, who, "burst-20261015T2000Z"));
        _t.Time.Advance(TimeSpan.FromMinutes(10));
        Assert.True(_t.Audit.WriteOnce(AuditActions.ShowViewed, who, "burst-20261015T1900Z"));
        var entries = _t.Audit.Query(new DateOnly(2026, 10, 16), AuditActions.ShowViewed, null);
        Assert.Equal(3, entries.Count);
        Assert.Equal(AccountsTestDb.Start.AddMinutes(10), entries[0].AtUtc); // newest first
    }
}
```

- [ ] **Step 3: Run them and see them fail** (`dotnet build -c Release`.)

- [ ] **Step 4: Implement**

`src/WebAudioArchive.Web/Accounts/Roles.cs`:
```csharp
using System.Globalization;
using System.Security.Claims;

namespace WebAudioArchive.Web.Accounts;

/// <summary>Managers see and manage everything; presenters see only the shows assigned to them, with the margins.</summary>
public enum Role { Manager, Presenter }

public static class Roles
{
    public const string Manager = "manager";
    public const string Presenter = "presenter";

    public static string Name(Role role) => role == Role.Manager ? Manager : Presenter;

    public static Role? Parse(string? text) => text switch { Manager => Role.Manager, Presenter => Role.Presenter, _ => null };
}

/// <summary>The archive's claims in the session cookie, besides AuthMagic's provider and subject.</summary>
public static class ArchiveClaims
{
    public const string SessionId = "waa:sid";
    public const string UserId = "waa:uid";
}

/// <summary>Authorization policy names. Pages and endpoints without one get the fallback: signed in with a role.</summary>
public static class ArchivePolicies
{
    public const string Manager = "manager";
    public const string Member = "member";
}

/// <summary>Who is acting, for the guards and the audit log. Name is the email for people.</summary>
public sealed record Actor(long? UserId, string Name)
{
    public static readonly Actor Bootstrap = new(null, "bootstrap");
    public static readonly Actor System = new(null, "system");
}

/// <summary>The signed-in person, read from the session's claims (which the gate rebuilds from the database on every request).</summary>
public sealed record Viewer(long UserId, string Email, string Name, Role Role)
{
    public bool IsManager => Role == Role.Manager;

    public Actor Actor => new(UserId, Email);

    public static Viewer? From(ClaimsPrincipal? user)
    {
        if (user?.Identity?.IsAuthenticated != true) return null;
        if (!long.TryParse(user.FindFirst(ArchiveClaims.UserId)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return null;
        if (Roles.Parse(user.FindFirst(ClaimTypes.Role)?.Value) is not { } role) return null;
        var email = user.FindFirst(ClaimTypes.Email)?.Value ?? "";
        return new Viewer(id, email, user.Identity.Name ?? email, role);
    }
}
```

`src/WebAudioArchive.Web/Accounts/AuditLog.cs`:
```csharp
using System.Collections.Concurrent;
using System.Globalization;

namespace WebAudioArchive.Web.Accounts;

public static class AuditActions
{
    public const string SignIn = "sign-in";
    public const string SignInRefused = "sign-in-refused";
    public const string SignOut = "sign-out";
    public const string ShowViewed = "show-viewed";
    public const string ClipPrepared = "clip-prepared";
    public const string MediaPlayed = "media-played";
    public const string MediaDownloaded = "media-downloaded";
    public const string UserAdded = "user-added";
    public const string UserRole = "user-role";
    public const string UserEnabled = "user-enabled";
    public const string UserRemoved = "user-removed";
    public const string UserLoginsReset = "user-logins-reset";
    public const string SessionsEnded = "sessions-ended";
    public const string UsersExported = "users-exported";
    public const string UsersImported = "users-imported";
    public const string AssignmentAdded = "assignment-added";
    public const string AssignmentRemoved = "assignment-removed";
}

public sealed record AuditEntry(long Id, DateTimeOffset AtUtc, long? UserId, string? Email, string Action, string? Target, string? Detail, string? RemoteIp);

/// <summary>
/// Who did what, when (UTC), from where. Accesses to shows, clips and media are written before the content is
/// served; a failure to write stops the request (fail closed).
/// </summary>
public sealed class AuditLog(AppDb db, TimeProvider time)
{
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);
    readonly ConcurrentDictionary<string, DateTimeOffset> _recent = new(StringComparer.Ordinal);

    public void Write(string action, Actor? actor, string? target, string? detail = null, string? remoteIp = null)
    {
        using var c = db.Open();
        c.Exec("INSERT INTO audit (at_utc, user_id, email, action, target, detail, remote_ip) VALUES ($at, $u, $e, $a, $t, $d, $ip)",
            ("$at", Sql.Text(time.GetUtcNow())), ("$u", actor?.UserId), ("$e", actor?.Name), ("$a", action), ("$t", target), ("$d", detail), ("$ip", remoteIp));
    }

    /// <summary>
    /// Writes unless the same person did the same thing to the same target within <see cref="RepeatWindow"/>: a page's
    /// prerender and its interactive render, or a player's many range requests, are one access.
    /// </summary>
    public bool WriteOnce(string action, Actor actor, string target, string? detail = null, string? remoteIp = null)
    {
        var now = time.GetUtcNow();
        var key = $"{actor.UserId?.ToString(CultureInfo.InvariantCulture) ?? actor.Name}|{action}|{target}";
        if (_recent.TryGetValue(key, out var at) && now - at < RepeatWindow) return false;
        _recent[key] = now;
        if (_recent.Count > 10_000)
            foreach (var (k, v) in _recent)
                if (now - v >= RepeatWindow) _recent.TryRemove(k, out _);
        Write(action, actor, target, detail, remoteIp);
        return true;
    }

    /// <summary>Newest first. Filters: one UTC date, one action, part of the email or target.</summary>
    public IReadOnlyList<AuditEntry> Query(DateOnly? utcDate, string? action, string? contains, int take = 500)
    {
        var where = new List<string>();
        var args = new List<(string, object?)>();
        if (utcDate is { } d)
        {
            where.Add("at_utc >= $from AND at_utc < $to");
            var from = new DateTimeOffset(d.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            args.Add(("$from", Sql.Text(from)));
            args.Add(("$to", Sql.Text(from.AddDays(1))));
        }

        if (!string.IsNullOrWhiteSpace(action))
        {
            where.Add("action = $action");
            args.Add(("$action", action));
        }

        if (!string.IsNullOrWhiteSpace(contains))
        {
            where.Add("(instr(lower(coalesce(email, '')), lower($c)) > 0 OR instr(lower(coalesce(target, '')), lower($c)) > 0)");
            args.Add(("$c", contains.Trim()));
        }

        args.Add(("$take", Math.Clamp(take, 1, 5000)));
        var sql = "SELECT id, at_utc, user_id, email, action, target, detail, remote_ip FROM audit"
            + (where.Count > 0 ? " WHERE " + string.Join(" AND ", where) : "")
            + " ORDER BY at_utc DESC, id DESC LIMIT $take";
        using var c = db.Open();
        return c.Query(sql, r => new AuditEntry(r.GetInt64(0), Sql.Time(r.GetString(1)), r.IsDBNull(2) ? null : r.GetInt64(2),
            r.StringOrNull(3), r.GetString(4), r.StringOrNull(5), r.StringOrNull(6), r.StringOrNull(7)), [.. args]);
    }
}
```
(The `where` fragments are fixed strings; every value is a parameter.)

`src/WebAudioArchive.Web/Accounts/UserStore.cs`:
```csharp
using Microsoft.Data.Sqlite;

namespace WebAudioArchive.Web.Accounts;

public sealed record LoginRecord(string Provider, string Subject, string EmailAtBind, DateTimeOffset BoundUtc);

public sealed record UserRecord(
    long Id, string Email, string? DisplayName, Role Role, bool Enabled,
    DateTimeOffset CreatedUtc, string CreatedBy, DateTimeOffset? LastSignInUtc, IReadOnlyList<LoginRecord> Logins)
{
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Email : DisplayName;
}

/// <summary>A change a manager asked for that the rules do not allow; the message is shown to them.</summary>
public sealed class AccountException(string message) : Exception(message);

/// <summary>
/// The allow-list: people (by verified email), their role, whether they are enabled, and the provider accounts bound
/// to them on first sign-in. Every change is audited. Changes to a person end their sessions.
/// </summary>
public sealed class UserStore(AppDb db, AuthSettings auth, AuditLog audit, TimeProvider time)
{
    const string Columns = "id, email, display_name, role, enabled, created_utc, created_by, last_sign_in_utc";

    public IReadOnlyList<UserRecord> All()
    {
        using var c = db.Open();
        var logins = Logins(c);
        return c.Query($"SELECT {Columns} FROM users ORDER BY role, email", r => Map(r, logins));
    }

    public UserRecord? Get(long id) => One("id = $v", id);

    public UserRecord? FindByEmail(string? email) => Emails.Normalise(email) is { } e ? One("email = $v", e) : null;

    public UserRecord? FindByLogin(string provider, string subject)
    {
        using var c = db.Open();
        var id = c.ScalarLong("SELECT user_id FROM user_logins WHERE provider = $p AND subject = $s", ("$p", provider), ("$s", subject));
        return id is { } v ? Get(v) : null;
    }

    public UserRecord Add(string email, string? displayName, Role role, Actor actor)
    {
        var e = Emails.Normalise(email) ?? throw new AccountException($"'{email.Trim()}' is not an email address.");
        if (role == Role.Presenter && auth.Mode == InstanceMode.ManagerOnly)
            throw new AccountException("This is a manager-only instance, so presenters cannot be added here. Add them on the primary instance.");
        using var c = db.Open();
        if (c.ScalarLong("SELECT id FROM users WHERE email = $e", ("$e", e)) is not null)
            throw new AccountException($"{e} is already on the list.");
        c.Exec("INSERT INTO users (email, display_name, role, enabled, created_utc, created_by) VALUES ($e, $n, $r, 1, $t, $by)",
            ("$e", e), ("$n", Clean(displayName)), ("$r", Roles.Name(role)), ("$t", Sql.Text(time.GetUtcNow())), ("$by", actor.Name));
        audit.Write(AuditActions.UserAdded, actor, e, "role=" + Roles.Name(role));
        return FindByEmail(e)!;
    }

    public void SetRole(long id, Role role, Actor actor)
    {
        var u = Require(id);
        if (u.Role == role) return;
        if (actor.UserId == id) throw new AccountException("You cannot change your own role.");
        if (role == Role.Presenter)
        {
            if (auth.Mode == InstanceMode.ManagerOnly) throw new AccountException("This is a manager-only instance, so there are no presenters here.");
            GuardManager(u);
        }

        Change(id, "UPDATE users SET role = $x WHERE id = $id", Roles.Name(role));
        audit.Write(AuditActions.UserRole, actor, u.Email, "role=" + Roles.Name(role));
    }

    public void SetEnabled(long id, bool enabled, Actor actor)
    {
        var u = Require(id);
        if (u.Enabled == enabled) return;
        if (!enabled)
        {
            if (actor.UserId == id) throw new AccountException("You cannot disable yourself.");
            GuardManager(u);
        }

        Change(id, "UPDATE users SET enabled = $x WHERE id = $id", enabled ? 1 : 0);
        audit.Write(AuditActions.UserEnabled, actor, u.Email, enabled ? "enabled" : "disabled");
    }

    public void Remove(long id, Actor actor)
    {
        var u = Require(id);
        if (actor.UserId == id) throw new AccountException("You cannot remove yourself.");
        GuardManager(u);
        using var c = db.Open();
        c.Exec("DELETE FROM users WHERE id = $id", ("$id", id));
        audit.Write(AuditActions.UserRemoved, actor, u.Email);
    }

    /// <summary>Forgets the provider accounts bound to a person, so their next sign-in binds again (for a changed account).</summary>
    public void ResetLogins(long id, Actor actor)
    {
        var u = Require(id);
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Exec(tx, "DELETE FROM user_logins WHERE user_id = $id", ("$id", id));
        c.Exec(tx, "DELETE FROM sessions WHERE user_id = $id", ("$id", id));
        tx.Commit();
        audit.Write(AuditActions.UserLoginsReset, actor, u.Email);
    }

    /// <summary>Signs the person out everywhere.</summary>
    public int EndSessions(long id, Actor actor)
    {
        var u = Require(id);
        using var c = db.Open();
        var n = c.Exec("DELETE FROM sessions WHERE user_id = $id", ("$id", id));
        audit.Write(AuditActions.SessionsEnded, actor, u.Email, $"sessions={n}");
        return n;
    }

    public void Bind(long userId, string provider, string subject, string email)
    {
        using var c = db.Open();
        c.Exec("INSERT INTO user_logins (provider, subject, user_id, email_at_bind, bound_utc) VALUES ($p, $s, $u, $e, $t)",
            ("$p", provider), ("$s", subject), ("$u", userId), ("$e", email), ("$t", Sql.Text(time.GetUtcNow())));
    }

    public void RecordSignIn(long userId)
    {
        using var c = db.Open();
        c.Exec("UPDATE users SET last_sign_in_utc = $t WHERE id = $id", ("$t", Sql.Text(time.GetUtcNow())), ("$id", userId));
    }

    /// <summary>BOOTSTRAP_MANAGER_EMAIL is an enabled manager after every start: created if missing, restored if changed.</summary>
    public void EnsureBootstrapManager(string email)
    {
        var u = FindByEmail(email);
        if (u is null)
        {
            Add(email, null, Role.Manager, Actor.Bootstrap);
            return;
        }

        if (u.Role == Role.Manager && u.Enabled) return;
        using var c = db.Open();
        c.Exec("UPDATE users SET role = 'manager', enabled = 1 WHERE id = $id", ("$id", u.Id));
        audit.Write(AuditActions.UserRole, Actor.Bootstrap, u.Email, "role=manager enabled (BOOTSTRAP_MANAGER_EMAIL)");
    }

    void GuardManager(UserRecord u)
    {
        if (u.Role != Role.Manager) return;
        if (auth.BootstrapManagerEmail == u.Email)
            throw new AccountException($"{u.Email} is BOOTSTRAP_MANAGER_EMAIL, which is made a manager at every start. Change that setting first.");
        if (u.Enabled && All().Count(x => x.Role == Role.Manager && x.Enabled) == 1)
            throw new AccountException("This is the only enabled manager. Add another manager first.");
    }

    void Change(long id, string update, object value)
    {
        using var c = db.Open();
        using var tx = c.BeginTransaction();
        c.Exec(tx, update, ("$x", value), ("$id", id));
        c.Exec(tx, "DELETE FROM sessions WHERE user_id = $id", ("$id", id)); // they sign in again with the new rights
        tx.Commit();
    }

    UserRecord Require(long id) => Get(id) ?? throw new AccountException("There is no such user.");

    // where is always one of the fixed strings above; the value is a parameter.
    UserRecord? One(string where, object value)
    {
        using var c = db.Open();
        var logins = Logins(c);
        return c.Query($"SELECT {Columns} FROM users WHERE {where}", r => Map(r, logins), ("$v", value)).SingleOrDefault();
    }

    static ILookup<long, LoginRecord> Logins(SqliteConnection c) =>
        c.Query("SELECT user_id, provider, subject, email_at_bind, bound_utc FROM user_logins ORDER BY provider",
                r => (UserId: r.GetInt64(0), Login: new LoginRecord(r.GetString(1), r.GetString(2), r.GetString(3), Sql.Time(r.GetString(4)))))
            .ToLookup(x => x.UserId, x => x.Login);

    static UserRecord Map(SqliteDataReader r, ILookup<long, LoginRecord> logins) => new(
        r.GetInt64(0),
        r.GetString(1),
        r.StringOrNull(2),
        Roles.Parse(r.GetString(3)) ?? throw new InvalidDataException("Unknown role in the users table."),
        r.GetInt64(4) == 1,
        Sql.Time(r.GetString(5)),
        r.GetString(6),
        r.StringOrNull(7) is { } t ? Sql.Time(t) : null,
        [.. logins[r.GetInt64(0)]]);

    static string? Clean(string? text)
    {
        var t = text?.Trim();
        return string.IsNullOrEmpty(t) ? null : t.Length > 100 ? t[..100] : t;
    }
}
```

- [ ] **Step 5: Run and commit**

```powershell
dotnet test -c Release --filter "FullyQualifiedName~UserStoreTests"
dotnet build -c Release
git add src/WebAudioArchive.Web/Accounts/Roles.cs src/WebAudioArchive.Web/Accounts/AuditLog.cs src/WebAudioArchive.Web/Accounts/UserStore.cs tests/WebAudioArchive.Tests/UserStoreTests.cs tests/WebAudioArchive.Tests/Support/AccountsTestDb.cs
git commit -m "Roles manager and presenter, the allow-list with bootstrap manager and guards, audit log (#1, #92, #87)"
```

### Task B5: The account gate and server-side sessions

**Files:**
- Create: `src/WebAudioArchive.Web/Accounts/AccountGate.cs`, `src/WebAudioArchive.Web/Accounts/SessionStore.cs`
- Test: `tests/WebAudioArchive.Tests/AccountGateTests.cs`, `tests/WebAudioArchive.Tests/SessionStoreTests.cs`

**Interfaces:**
- Produces: `record ExternalAccount(string Provider, string Subject, IReadOnlyList<string> VerifiedEmails, string? DisplayName)`; `record GateDecision(UserRecord? User, string? Refusal, string? Email)`; `AccountGate.Decide(ExternalAccount) : GateDecision` with refusal codes `failed`, `email-not-verified`, `not-listed`, `account-changed`, `disabled`, `managers-only`; `SessionStore.Create(long userId, string provider) : string` (the raw id), `Validate(string raw, bool touch) : ValidSession?`, `Touch`, `Delete`, `Prune`, `CountFor`; `record ValidSession(UserRecord User, string Provider, DateTimeOffset ExpiresUtc)`.

- [ ] **Step 1: Write the failing tests**

`tests/WebAudioArchive.Tests/AccountGateTests.cs`:
```csharp
extern alias web;

using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Accounts;

namespace WebAudioArchive.Tests;

public sealed class AccountGateTests : IDisposable
{
    readonly AccountsTestDb _t = new();
    readonly AccountGate _gate;

    public AccountGateTests()
    {
        _gate = new AccountGate(_t.Users, _t.Auth);
        _t.Users.EnsureBootstrapManager("manager@example.test");
        _t.Users.Add("presenter@example.test", "Presenter", Role.Presenter, Actor.System);
    }

    public void Dispose() => _t.Dispose();

    static ExternalAccount Google(string sub, params string[] verified) => new("google", sub, verified, "Someone");

    [Fact]
    public void A_listed_verified_email_is_admitted_and_bound_to_the_account()
    {
        var d = _gate.Decide(Google("sub-m", "Manager@Example.test"));

        Assert.Null(d.Refusal);
        Assert.Equal("manager@example.test", d.User!.Email);
        var login = Assert.Single(d.User.Logins);
        Assert.Equal(("google", "sub-m", "manager@example.test"), (login.Provider, login.Subject, login.EmailAtBind));
        Assert.NotNull(d.User.LastSignInUtc);
    }

    [Fact]
    public void A_bound_account_is_admitted_even_after_its_email_changed()
    {
        _gate.Decide(Google("sub-m", "manager@example.test"));
        var d = _gate.Decide(Google("sub-m", "new-address@example.test"));
        Assert.Equal("manager@example.test", d.User!.Email);
    }

    [Fact]
    public void Unlisted_unverified_and_unknown_provider_accounts_are_refused()
    {
        Assert.Equal("not-listed", _gate.Decide(Google("sub-x", "stranger@example.test")).Refusal);
        Assert.Equal("email-not-verified", _gate.Decide(Google("sub-y")).Refusal);
        Assert.Equal("failed", _gate.Decide(new ExternalAccount("facebook", "1", ["manager@example.test"], null)).Refusal);
        Assert.Equal("failed", _gate.Decide(new ExternalAccount("google", "", ["manager@example.test"], null)).Refusal);
    }

    [Fact]
    public void A_different_account_with_a_bound_email_is_refused_until_reset()
    {
        _gate.Decide(Google("sub-original", "presenter@example.test"));

        var takeover = _gate.Decide(Google("sub-impostor", "presenter@example.test"));
        Assert.Equal("account-changed", takeover.Refusal);
        Assert.Null(takeover.User);

        var p = _t.Users.FindByEmail("presenter@example.test")!;
        _t.Users.ResetLogins(p.Id, Actor.System);
        Assert.Equal(p.Id, _gate.Decide(Google("sub-impostor", "presenter@example.test")).User!.Id);
    }

    [Fact]
    public void A_verified_secondary_github_email_matches()
    {
        var d = _gate.Decide(new ExternalAccount("github", "1001", ["someone-else@example.test", "presenter@example.test"], "P"));
        Assert.Equal("presenter@example.test", d.User!.Email);
        Assert.Equal("presenter@example.test", Assert.Single(d.User.Logins).EmailAtBind);
    }

    [Fact]
    public void The_same_person_may_bind_one_google_and_one_github_account()
    {
        _gate.Decide(Google("sub-p", "presenter@example.test"));
        var d = _gate.Decide(new ExternalAccount("github", "1001", ["presenter@example.test"], null));
        Assert.Equal(2, d.User!.Logins.Count);
    }

    [Fact]
    public void Disabled_people_are_refused_whether_bound_or_not()
    {
        _gate.Decide(Google("sub-p", "presenter@example.test"));
        var p = _t.Users.FindByEmail("presenter@example.test")!;
        _t.Users.SetEnabled(p.Id, false, Actor.System);
        Assert.Equal("disabled", _gate.Decide(Google("sub-p", "presenter@example.test")).Refusal);
        Assert.Equal("disabled", _gate.Decide(new ExternalAccount("github", "1001", ["presenter@example.test"], null)).Refusal);
    }

    [Fact]
    public void Presenters_are_refused_on_a_manager_only_instance()
    {
        using var t = new AccountsTestDb(InstanceMode.Primary);
        t.Users.EnsureBootstrapManager("manager@example.test");
        t.Users.Add("presenter@example.test", null, Role.Presenter, Actor.System);
        var managerOnly = t.Auth with { Mode = InstanceMode.ManagerOnly };

        var gate = new AccountGate(t.Users, managerOnly);
        Assert.Equal("managers-only", gate.Decide(Google("sub-p", "presenter@example.test")).Refusal);
        Assert.NotNull(gate.Decide(Google("sub-m", "manager@example.test")).User);
    }
}
```

`tests/WebAudioArchive.Tests/SessionStoreTests.cs`:
```csharp
extern alias web;

using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Accounts;

namespace WebAudioArchive.Tests;

public sealed class SessionStoreTests : IDisposable
{
    readonly AccountsTestDb _t = new();
    readonly SessionStore _sessions;
    readonly UserRecord _manager;

    public SessionStoreTests()
    {
        _sessions = new SessionStore(_t.Db, _t.Users, _t.Auth, _t.Time);
        _t.Users.EnsureBootstrapManager("manager@example.test");
        _manager = _t.Users.FindByEmail("manager@example.test")!;
    }

    public void Dispose() => _t.Dispose();

    [Fact]
    public void A_new_session_is_valid_and_only_its_hash_is_stored()
    {
        var raw = _sessions.Create(_manager.Id, "google");

        Assert.Equal(43, raw.Length); // 32 random bytes, base64url
        var v = _sessions.Validate(raw, touch: false)!;
        Assert.Equal(_manager.Id, v.User.Id);
        Assert.Equal("google", v.Provider);
        Assert.Equal(AccountsTestDb.Start.AddHours(24), v.ExpiresUtc);
        using var c = _t.Db.Open();
        Assert.Equal(0, c.ScalarLong("SELECT count(*) FROM sessions WHERE id_hash = $r", ("$r", raw)));
        Assert.Equal(64, c.ScalarText("SELECT id_hash FROM sessions")!.Length);
        Assert.Null(_sessions.Validate("not-a-session", touch: true));
    }

    [Fact]
    public void Idle_sessions_end_and_activity_keeps_them_alive()
    {
        var raw = _sessions.Create(_manager.Id, "google");
        for (var i = 0; i < 6; i++)
        {
            _t.Time.Advance(TimeSpan.FromHours(3));
            if (i < 5) Assert.NotNull(_sessions.Validate(raw, touch: true)); // touched every 3 h: stays alive
        }

        // 18 hours in, then 4 hours with no activity: idle.
        _t.Time.Advance(TimeSpan.FromHours(1));
        Assert.Null(_sessions.Validate(raw, touch: false));
        Assert.Equal(0, _sessions.CountFor(_manager.Id));
    }

    [Fact]
    public void Validation_without_touch_does_not_extend_the_idle_time()
    {
        var raw = _sessions.Create(_manager.Id, "google");
        _t.Time.Advance(TimeSpan.FromHours(3));
        Assert.NotNull(_sessions.Validate(raw, touch: false));
        _t.Time.Advance(TimeSpan.FromHours(1));
        Assert.Null(_sessions.Validate(raw, touch: false));
    }

    [Fact]
    public void Sessions_end_at_the_absolute_age_however_active()
    {
        var raw = _sessions.Create(_manager.Id, "google");
        for (var h = 0; h < 23; h++)
        {
            _t.Time.Advance(TimeSpan.FromHours(1));
            Assert.NotNull(_sessions.Validate(raw, touch: true));
        }

        _t.Time.Advance(TimeSpan.FromHours(1));
        Assert.Null(_sessions.Validate(raw, touch: true));
    }

    [Fact]
    public void Disabled_users_and_presenters_on_a_manager_only_instance_lose_their_sessions()
    {
        var p = _t.Users.Add("presenter@example.test", null, Role.Presenter, Actor.System);
        var raw = _sessions.Create(p.Id, "github");
        var managerOnly = new SessionStore(_t.Db, _t.Users, _t.Auth with { Mode = InstanceMode.ManagerOnly }, _t.Time);
        Assert.Null(managerOnly.Validate(raw, touch: true));

        var raw2 = _sessions.Create(_manager.Id, "google");
        using (var c = _t.Db.Open()) c.Exec("UPDATE users SET enabled = 0 WHERE id = $id", ("$id", _manager.Id));
        Assert.Null(_sessions.Validate(raw2, touch: true));
    }

    [Fact]
    public void Delete_and_prune()
    {
        var a = _sessions.Create(_manager.Id, "google");
        var b = _sessions.Create(_manager.Id, "github");
        _sessions.Delete(a);
        Assert.Null(_sessions.Validate(a, touch: false));
        Assert.NotNull(_sessions.Validate(b, touch: false));
        _t.Time.Advance(TimeSpan.FromHours(5));
        Assert.Equal(1, _sessions.Prune());
        Assert.Equal(0, _sessions.CountFor(_manager.Id));
    }
}
```

- [ ] **Step 2: Run them and see them fail** (`dotnet build -c Release`.)

- [ ] **Step 3: Implement**

`src/WebAudioArchive.Web/Accounts/AccountGate.cs`:
```csharp
using Microsoft.Data.Sqlite;

namespace WebAudioArchive.Web.Accounts;

/// <summary>A provider account after the provider authenticated it and its token was validated.</summary>
public sealed record ExternalAccount(string Provider, string Subject, IReadOnlyList<string> VerifiedEmails, string? DisplayName);

/// <summary>Admit (User set) or refuse (Refusal is a short code for the denied page). Email is what matched, for the audit log.</summary>
public sealed record GateDecision(UserRecord? User, string? Refusal, string? Email)
{
    public static GateDecision Refuse(string code, string? email = null) => new(null, code, email);
}

/// <summary>
/// Who may sign in. A provider account already bound to a person is that person. Otherwise a verified email on the
/// list admits, and binds the provider account to the person; a different account of the same provider offering the
/// same email later is refused until a manager resets the person's sign-in. Never first-come-first-served.
/// </summary>
public sealed class AccountGate(UserStore users, AuthSettings auth)
{
    public GateDecision Decide(ExternalAccount account)
    {
        if (account.Provider is not ("google" or "github") || string.IsNullOrWhiteSpace(account.Subject))
            return GateDecision.Refuse("failed");

        if (users.FindByLogin(account.Provider, account.Subject) is { } bound)
            return Check(bound, bound.Email);

        var verified = account.VerifiedEmails.Select(Emails.Normalise).OfType<string>().Distinct(StringComparer.Ordinal).ToList();
        if (verified.Count == 0) return GateDecision.Refuse("email-not-verified");

        var listed = verified.Select(users.FindByEmail).OfType<UserRecord>().FirstOrDefault();
        if (listed is null) return GateDecision.Refuse("not-listed", verified[0]);
        if (listed.Logins.Any(l => l.Provider == account.Provider)) return GateDecision.Refuse("account-changed", listed.Email);

        var decision = Check(listed, listed.Email);
        if (decision.User is null) return decision;
        try
        {
            users.Bind(listed.Id, account.Provider, account.Subject, listed.Email);
        }
        catch (SqliteException)
        {
            return GateDecision.Refuse("failed", listed.Email); // a simultaneous first sign-in bound it first
        }

        return decision with { User = users.Get(listed.Id) };
    }

    GateDecision Check(UserRecord user, string email)
    {
        if (!user.Enabled) return GateDecision.Refuse("disabled", email);
        if (user.Role == Role.Presenter && auth.Mode == InstanceMode.ManagerOnly) return GateDecision.Refuse("managers-only", email);
        users.RecordSignIn(user.Id);
        return new GateDecision(users.Get(user.Id), null, email);
    }
}
```

`src/WebAudioArchive.Web/Accounts/SessionStore.cs`:
```csharp
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace WebAudioArchive.Web.Accounts;

public sealed record ValidSession(UserRecord User, string Provider, DateTimeOffset ExpiresUtc);

/// <summary>
/// Server-side sessions. The cookie holds a random 256-bit id; the table holds only its SHA-256, the user, and the
/// created, last seen and expiry times (UTC). A session ends at the absolute age, after the idle timeout, at sign-out,
/// or when the person is disabled, removed, changes role, or is a presenter on a manager-only instance.
/// </summary>
public sealed class SessionStore(AppDb db, UserStore users, AuthSettings auth, TimeProvider time)
{
    /// <summary>Activity is written at most this often, so range requests do not write on every call.</summary>
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    public string Create(long userId, string provider)
    {
        var raw = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        var now = time.GetUtcNow();
        using var c = db.Open();
        c.Exec("INSERT INTO sessions (id_hash, user_id, provider, created_utc, last_seen_utc, expires_utc) VALUES ($h, $u, $p, $now, $now, $exp)",
            ("$h", Hash(raw)), ("$u", userId), ("$p", provider), ("$now", Sql.Text(now)), ("$exp", Sql.Text(now + auth.MaxSessionAge)));
        return raw;
    }

    public ValidSession? Validate(string raw, bool touch)
    {
        var now = time.GetUtcNow();
        var hash = Hash(raw);
        using var c = db.Open();
        var rows = c.Query("SELECT user_id, provider, last_seen_utc, expires_utc FROM sessions WHERE id_hash = $h",
            r => (UserId: r.GetInt64(0), Provider: r.GetString(1), LastSeen: Sql.Time(r.GetString(2)), Expires: Sql.Time(r.GetString(3))),
            ("$h", hash));
        if (rows.Count == 0) return null;
        var row = rows[0];
        var user = users.Get(row.UserId);
        var allowed = user is { Enabled: true } && (user.Role == Role.Manager || auth.Mode == InstanceMode.Primary);
        if (!allowed || now >= row.Expires || now - row.LastSeen >= auth.IdleTimeout)
        {
            c.Exec("DELETE FROM sessions WHERE id_hash = $h", ("$h", hash));
            return null;
        }

        if (touch && now - row.LastSeen >= TouchInterval)
            c.Exec("UPDATE sessions SET last_seen_utc = $t WHERE id_hash = $h", ("$t", Sql.Text(now)), ("$h", hash));
        return new ValidSession(user!, row.Provider, row.Expires);
    }

    public void Touch(string raw) => Validate(raw, touch: true);

    public void Delete(string raw)
    {
        using var c = db.Open();
        c.Exec("DELETE FROM sessions WHERE id_hash = $h", ("$h", Hash(raw)));
    }

    public int Prune()
    {
        var now = time.GetUtcNow();
        using var c = db.Open();
        return c.Exec("DELETE FROM sessions WHERE expires_utc <= $now OR last_seen_utc <= $idle",
            ("$now", Sql.Text(now)), ("$idle", Sql.Text(now - auth.IdleTimeout)));
    }

    public int CountFor(long userId)
    {
        using var c = db.Open();
        return (int)(c.ScalarLong("SELECT count(*) FROM sessions WHERE user_id = $u", ("$u", userId)) ?? 0);
    }

    static string Hash(string raw) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));
}
```

- [ ] **Step 4: Run and commit**

```powershell
dotnet test -c Release --filter "FullyQualifiedName~AccountGateTests|FullyQualifiedName~SessionStoreTests"
dotnet build -c Release
git add src/WebAudioArchive.Web/Accounts/AccountGate.cs src/WebAudioArchive.Web/Accounts/SessionStore.cs tests/WebAudioArchive.Tests/AccountGateTests.cs tests/WebAudioArchive.Tests/SessionStoreTests.cs
git commit -m "Allow-list gate (verified email, bound account, refusal codes) and server-side sessions with idle and absolute timeouts (#1, #92)"
```

### Task B6: Wire AuthMagic: sign-in, sign-out, sessions, deny by default

**Files:**
- Modify: `src/WebAudioArchive.Web/WebAudioArchive.Web.csproj`, `src/WebAudioArchive.Web/Program.cs`, `src/WebAudioArchive.Web/Components/App.razor`, `Components/Routes.razor`, `Components/_Imports.razor`, `Components/Layout/MainLayout.razor`
- Create: `src/WebAudioArchive.Web/Accounts/ArchiveGate.cs`, `Accounts/SessionRevalidator.cs`, `Accounts/AccountsHosting.cs`, `Components/Shared/RedirectToSignIn.razor`, `Components/Pages/SignIn.razor`, `Components/Pages/Denied.razor`, `Components/Pages/SignOut.razor`, `Components/Pages/SignedOut.razor`
- Test: `tests/WebAudioArchive.Tests/Support/FakeIdentityProvider.cs` (Appendix F), `Support/TestSignIn.cs`, `Support/ArchiveApp.cs`, `SignInFlowTests.cs`; modify `WebAppTests.cs`, `ShowsPageTests.cs`

**Interfaces:**
- Consumes: A8's local package; B2 to B5.
- Produces: `builder.AddArchiveAccounts(AuthSettings)`; `app.StartArchiveAccounts()`; `app.UseArchiveSecurity(AuthSettings)`; `app.MapArchiveAccounts()`; `ArchiveGate : IAuthMagicAccountGate`; `ArchiveClaims.For(UserRecord, sid, provider, subject) : ClaimsPrincipal`; test helpers `TestSignIn.Configure`, `TestSignIn.SignInAsync`, `ArchiveApp`.

- [ ] **Step 1: Use the local package**

```powershell
dotnet nuget add source C:\Users\david\source\repos\local-nuget --name local-authmagic --configfile "$env:APPDATA\NuGet\NuGet.Config"
```
In `src/WebAudioArchive.Web/WebAudioArchive.Web.csproj` add, with the exact version A8 printed (for example `2.1.3-g1a2b3c4d5e`):
```xml
    <PackageReference Include="PanoramicData.AuthMagic.AspNetCore" Version="2.1.3-g1a2b3c4d5e" />
```
Run `dotnet restore`; expected: restored from `local-authmagic`. (The user-level source and this prerelease pin are removed in B14.)

- [ ] **Step 2: Write the test support**

Create `tests/WebAudioArchive.Tests/Support/FakeIdentityProvider.cs` from Appendix F (namespace `WebAudioArchive.Tests.Support`).

`tests/WebAudioArchive.Tests/Support/TestSignIn.cs`:
```csharp
extern alias web;

using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OAuth;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using PanoramicData.AuthMagic.AspNetCore;
using web::WebAudioArchive.Web;

namespace WebAudioArchive.Tests.Support;

/// <summary>Sign-in for the in-memory app: settings, the fake provider as back-channel, and the browser's part of the flow.</summary>
public static partial class TestSignIn
{
    public const string ManagerEmail = "manager@example.test";
    public const string PresenterEmail = "presenter@example.test";
    public static readonly FakeAccount Manager = new("google-sub-manager", ManagerEmail, true, "Manager");
    public static readonly FakeAccount Presenter = new("google-sub-presenter", PresenterEmail, true, "Presenter");
    public static readonly Uri BaseAddress = new("https://localhost");

    public static void Configure(IWebHostBuilder builder, string appDir, FakeIdentityProvider idp, string mode = "primary", string? bootstrap = ManagerEmail)
    {
        builder.UseSetting("WAA_PUBLIC_URL", "https://localhost");
        builder.UseSetting("INSTANCE_MODE", mode);
        if (bootstrap is not null) builder.UseSetting("BOOTSTRAP_MANAGER_EMAIL", bootstrap);
        builder.UseSetting("WAA_GOOGLE_CLIENT_ID", FakeIdentityProvider.GoogleClientId);
        builder.UseSetting("WAA_GOOGLE_CLIENT_SECRET", FakeIdentityProvider.GoogleClientSecret);
        builder.UseSetting("WAA_GITHUB_CLIENT_ID", FakeIdentityProvider.GitHubClientId);
        builder.UseSetting("WAA_GITHUB_CLIENT_SECRET", FakeIdentityProvider.GitHubClientSecret);
        builder.UseSetting("WAA_APP_DIR", appDir);
        builder.ConfigureTestServices(services =>
        {
            // Configure, not PostConfigure: the handlers build their back-channel from this in their own post-configure.
            // The handlers use the real clock: the app's TimeProvider is a fixed 2026 time in these tests, and cookie
            // expiry computed from it would already be in the past for the cookie container.
            services.Configure<OpenIdConnectOptions>(AuthMagicServerDefaults.GoogleScheme, o => { o.BackchannelHttpHandler = idp; o.TimeProvider = TimeProvider.System; });
            services.Configure<OAuthOptions>(AuthMagicServerDefaults.GitHubScheme, o => { o.BackchannelHttpHandler = idp; o.TimeProvider = TimeProvider.System; });
            services.Configure<CookieAuthenticationOptions>(AuthMagicServerDefaults.CookieScheme, o => o.TimeProvider = TimeProvider.System);
        });
    }

    /// <summary>A browser-like client: keeps cookies in <paramref name="jar"/> (so a test can copy them), never follows redirects.</summary>
    public static HttpClient Client(WebApplicationFactory<WebEntryPoint> factory, CookieContainer? jar = null) =>
        factory.CreateDefaultClient(BaseAddress, new BrowserCookies(jar ?? new CookieContainer()));

    public static async Task<HttpClient> SignInAsync(WebApplicationFactory<WebEntryPoint> factory, FakeIdentityProvider idp, FakeAccount account, string provider = "google", CookieContainer? jar = null)
    {
        var client = Client(factory, jar);
        using var back = await StartAndCompleteAsync(client, idp, account, provider);
        if (back.StatusCode != HttpStatusCode.Redirect || back.Headers.Location?.OriginalString != "/")
            throw new InvalidOperationException($"Sign-in as {account.Email} did not complete: {(int)back.StatusCode} {back.Headers.Location}");
        return client;
    }

    public static async Task<HttpResponseMessage> StartAsync(HttpClient client, string provider, string returnUrl = "/")
    {
        var page = await client.GetStringAsync("/signin?returnUrl=" + Uri.EscapeDataString(returnUrl));
        return await client.PostAsync($"/auth/signin/{provider}", new FormUrlEncodedContent(
            [new("__RequestVerificationToken", Antiforgery(page)), new("returnUrl", returnUrl)]));
    }

    public static async Task<HttpResponseMessage> StartAndCompleteAsync(HttpClient client, FakeIdentityProvider idp, FakeAccount account, string provider = "google", string returnUrl = "/", Tamper? tamper = null)
    {
        using var challenge = await StartAsync(client, provider, returnUrl);
        if (challenge.StatusCode != HttpStatusCode.Redirect)
            throw new InvalidOperationException($"Starting sign-in answered {(int)challenge.StatusCode}, not a redirect to the provider.");
        return await client.GetAsync(idp.Authorize(challenge.Headers.Location!, account, tamper));
    }

    public static string Antiforgery(string html) =>
        AntiforgeryField().Match(html) is { Success: true } m
            ? WebUtility.HtmlDecode(m.Groups[1].Value)
            : throw new InvalidOperationException("No antiforgery token in the page.");

    [GeneratedRegex("<input[^>]*name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"")]
    private static partial Regex AntiforgeryField();

    /// <summary>Sends and stores cookies as a browser would (Secure cookies only over https, which the base address is).</summary>
    sealed class BrowserCookies(CookieContainer jar) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var header = jar.GetCookieHeader(request.RequestUri!);
            if (header.Length > 0) request.Headers.Add("Cookie", header);
            var response = await base.SendAsync(request, cancellationToken);
            if (response.Headers.TryGetValues("Set-Cookie", out var values))
                foreach (var value in values) jar.SetCookies(request.RequestUri!, value);
            return response;
        }
    }
}
```

`tests/WebAudioArchive.Tests/Support/ArchiveApp.cs` (the shared in-memory app for the sign-in, authorisation, presenter, audit and hardening tests; same data as `ShowsAppFixture`):
```csharp
extern alias web;

using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using web::WebAudioArchive.Web;
using web::WebAudioArchive.Web.Accounts;
using web::WebAudioArchive.Web.Schedule;
using web::WebAudioArchive.Web.Status;

namespace WebAudioArchive.Tests.Support;

/// <summary>
/// The web app with sign-in, a schedule and recordings: Thursday 15 October 2026, 21:30 UTC; recorder A has audio
/// from 18:55 to 20:05 UTC, so Backseat Aux (19:00 to 20:00 UTC) is recorded with both 5 minute margins.
/// </summary>
public sealed class ArchiveApp : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 15, 21, 30, 0, TimeSpan.Zero);
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "waa-archive-" + Guid.NewGuid().ToString("N"));
    public FakeIdentityProvider Idp { get; } = new();
    public ManualTimeProvider Time { get; } = new(Now);
    public WebApplicationFactory<WebEntryPoint> Factory { get; }
    public string AppDir => Path.Combine(Root, "app");

    public ArchiveApp(string mode = "primary", Action<IWebHostBuilder>? configure = null)
    {
        var a = Path.Combine(Root, "data", "a");
        var b = Path.Combine(Root, "data", "b");
        Directory.CreateDirectory(b);
        var t = new DateTimeOffset(2026, 10, 15, 18, 55, 0, TimeSpan.Zero);
        var s1 = SegmentFiles.Closed(a, "burst", t, 300, firstSeq: 0, burstSeconds: 5);
        var s2 = SegmentFiles.Closed(a, "burst", t.AddMinutes(5), 3600, firstSeq: s1.Frames);
        SegmentFiles.Closed(a, "burst", t.AddMinutes(65), 300, firstSeq: s1.Frames + s2.Frames, endedBy: "shutdown");
        var config = Path.Combine(Root, "stations.json");
        File.WriteAllText(config, """
            { "stations": [ { "id": "burst", "name": "Burst Radio", "listenUrl": "https://burst.test/listen",
              "scheduleUrl": "https://burst.test/schedule", "timezone": "Europe/London" } ] }
            """);
        var schedule = new StubHandler(req => req.RequestUri!.Host == "burst.test" ? StubHandler.Html(ScheduleParserTests.Fixture) : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        Factory = new WebApplicationFactory<WebEntryPoint>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("WAA_STATION", "burst");
            builder.UseSetting("WAA_RECORDERS", $"a={a},b={b}");
            builder.UseSetting("WAA_CACHE", Path.Combine(Root, "cache"));
            builder.UseSetting("WAA_STATIONS_CONFIG", config);
            TestSignIn.Configure(builder, AppDir, Idp, mode);
            builder.ConfigureTestServices(services =>
            {
                services.AddSingleton<TimeProvider>(Time);
                services.AddHttpClient(ScheduleService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => schedule);
                services.AddHttpClient(StatusService.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => new StubHandler(_ => StubHandler.NotFound()));
            });
            configure?.Invoke(builder);
        });
    }

    public T Service<T>() where T : notnull => Factory.Services.GetRequiredService<T>();

    public HttpClient Anonymous() => TestSignIn.Client(Factory);

    public Task<HttpClient> ManagerAsync() => TestSignIn.SignInAsync(Factory, Idp, TestSignIn.Manager);

    /// <summary>Adds the presenter (once), assigns them these show titles, and signs them in.</summary>
    public async Task<HttpClient> PresenterAsync(params string[] titles)
    {
        var users = Service<UserStore>(); // building the host migrates the database and adds the bootstrap manager
        var p = users.FindByEmail(TestSignIn.PresenterEmail) ?? users.Add(TestSignIn.PresenterEmail, "Presenter", Role.Presenter, Actor.System);
        foreach (var title in titles) Service<AssignmentStore>().Add(p.Id, "burst", title, null, null, Actor.System);
        return await TestSignIn.SignInAsync(Factory, Idp, TestSignIn.Presenter);
    }

    public static async Task<string> Page(HttpClient client, string url) => WebUtility.HtmlDecode(await client.GetStringAsync(url));

    public async ValueTask DisposeAsync()
    {
        await Factory.DisposeAsync();
        Idp.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
```
(`AssignmentStore` arrives in B8; until then leave the `foreach` line out of `PresenterAsync` and add it in B8 Step 3.)

- [ ] **Step 3: Write the failing tests**

`tests/WebAudioArchive.Tests/SignInFlowTests.cs`:
```csharp
extern alias web;

using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components.Authorization;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Accounts;

namespace WebAudioArchive.Tests;

public sealed class SignInFlowTests : IAsyncLifetime
{
    readonly ArchiveApp _app = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _app.DisposeAsync();

    static HttpRequestMessage Navigate(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        return request;
    }

    [Fact]
    public async Task An_anonymous_page_visit_goes_to_sign_in_with_the_return_url()
    {
        using var response = await _app.Anonymous().SendAsync(Navigate("/shows/burst-20261015T1900Z"));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("https://localhost/signin?returnUrl=%2Fshows%2Fburst-20261015T1900Z", response.Headers.Location!.AbsoluteUri);
    }

    [Fact]
    public async Task The_sign_in_page_offers_both_providers_and_opens_no_circuit()
    {
        var html = await ArchiveApp.Page(_app.Anonymous(), "/signin?returnUrl=%2Fshows");
        Assert.Contains("action=\"/auth/signin/google\"", html);
        Assert.Contains("action=\"/auth/signin/github\"", html);
        Assert.Contains("name=\"returnUrl\" value=\"/shows\"", html);
        Assert.DoesNotContain("\"type\":\"server\"", html); // no interactive component, so no Blazor circuit
        Assert.DoesNotContain("No sign-in", html);
    }

    [Fact]
    public async Task The_manager_signs_in_with_google_and_lands_on_the_return_url()
    {
        var client = _app.Anonymous();
        using var back = await TestSignIn.StartAndCompleteAsync(client, _app.Idp, TestSignIn.Manager, returnUrl: "/recordings");
        Assert.Equal("/recordings", back.Headers.Location!.OriginalString);

        var html = await ArchiveApp.Page(client, "/recordings");
        Assert.Contains("id=\"signed-in-as\"", html);
        Assert.Contains("manager@example.test (manager)", html); // no display name yet: the email
        var signIn = _app.Service<AuditLog>().Query(null, AuditActions.SignIn, TestSignIn.ManagerEmail).Single();
        Assert.Equal("provider=google", signIn.Detail);
    }

    [Fact]
    public async Task GitHub_sign_in_binds_the_numeric_account_id()
    {
        var github = new FakeAccount("4242", null, true, "Manager on GitHub", [(TestSignIn.ManagerEmail, true, true)]);
        var client = await TestSignIn.SignInAsync(_app.Factory, _app.Idp, github, "github");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/recordings")).StatusCode);
        Assert.Equal(TestSignIn.ManagerEmail, _app.Service<UserStore>().FindByLogin("github", "4242")!.Email);
    }

    [Theory]
    [InlineData("stranger@example.test", true, "not-listed")]
    [InlineData(TestSignIn.ManagerEmail, false, "email-not-verified")]
    public async Task Unlisted_and_unverified_accounts_are_refused_and_audited(string email, bool verified, string reason)
    {
        var client = _app.Anonymous();
        using var back = await TestSignIn.StartAndCompleteAsync(client, _app.Idp, new FakeAccount("sub-" + reason, email, verified, "Someone"));
        Assert.Equal("/denied?reason=" + reason, back.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/clip")).StatusCode);
        Assert.Contains(_app.Service<AuditLog>().Query(null, AuditActions.SignInRefused, null), e => e.Detail == $"provider=google reason={reason}");
        Assert.Contains("not on this archive's list", await ArchiveApp.Page(client, "/denied?reason=not-listed"));
    }

    [Fact]
    public async Task Google_id_token_with_wrong_nonce_is_refused()
    {
        using var back = await TestSignIn.StartAndCompleteAsync(_app.Anonymous(), _app.Idp, TestSignIn.Manager, tamper: new Tamper(Nonce: "forged"));
        Assert.Equal("/denied?reason=failed", back.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Google_id_token_for_another_audience_is_refused()
    {
        using var back = await TestSignIn.StartAndCompleteAsync(_app.Anonymous(), _app.Idp, TestSignIn.Manager, tamper: new Tamper(Audience: "another-app.apps.googleusercontent.com"));
        Assert.Equal("/denied?reason=failed", back.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Replayed_callback_is_refused()
    {
        var client = _app.Anonymous();
        using var challenge = await TestSignIn.StartAsync(client, "google");
        var callback = _app.Idp.Authorize(challenge.Headers.Location!, TestSignIn.Manager);
        using var first = await client.GetAsync(callback);
        Assert.Equal("/", first.Headers.Location!.OriginalString);

        var attacker = _app.Anonymous();
        using var replay = await attacker.GetAsync(callback);
        Assert.Equal("/denied?reason=failed", replay.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await attacker.GetAsync("/api/clip")).StatusCode);
    }

    [Theory]
    [InlineData("//evil.example.test/")]
    [InlineData("https://evil.example.test/")]
    [InlineData("/\\evil.example.test")]
    public async Task Open_redirect_is_not_possible(string returnUrl)
    {
        using var back = await TestSignIn.StartAndCompleteAsync(_app.Anonymous(), _app.Idp, TestSignIn.Manager, returnUrl: returnUrl);
        Assert.Equal("/", back.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_new_sign_in_replaces_the_session_row()
    {
        var client = await _app.ManagerAsync();
        var manager = _app.Service<UserStore>().FindByEmail(TestSignIn.ManagerEmail)!;
        Assert.Equal(1, _app.Service<SessionStore>().CountFor(manager.Id));

        using var again = await TestSignIn.StartAndCompleteAsync(client, _app.Idp, TestSignIn.Manager);
        Assert.Equal(1, _app.Service<SessionStore>().CountFor(manager.Id));
    }

    [Fact]
    public async Task Sign_out_ends_the_session_everywhere()
    {
        var jar = new CookieContainer();
        var client = await TestSignIn.SignInAsync(_app.Factory, _app.Idp, TestSignIn.Manager, jar: jar);
        var stolen = new CookieContainer();
        stolen.Add(jar.GetAllCookies()); // a copy of the session cookie, as a thief would have it
        var copied = TestSignIn.Client(_app.Factory, stolen);
        Assert.Equal(HttpStatusCode.OK, (await copied.GetAsync("/recordings")).StatusCode);

        var page = await client.GetStringAsync("/signout");
        using var signOut = await client.PostAsync("/auth/signout", new FormUrlEncodedContent([new("__RequestVerificationToken", TestSignIn.Antiforgery(page))]));
        Assert.Equal("/signed-out", signOut.Headers.Location!.OriginalString);

        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/recordings")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await copied.GetAsync("/recordings")).StatusCode);
        Assert.Contains("You have signed out", await ArchiveApp.Page(client, "/signed-out"));
        Assert.Single(_app.Service<AuditLog>().Query(null, AuditActions.SignOut, null));
    }

    [Fact]
    public async Task Revalidation_drops_a_disabled_user()
    {
        var users = _app.Service<UserStore>();
        var other = users.Add("second@example.test", null, Role.Manager, Actor.System);
        var second = await TestSignIn.SignInAsync(_app.Factory, _app.Idp, new FakeAccount("sub-second", "second@example.test", true, "Second"));
        Assert.Equal(HttpStatusCode.OK, (await second.GetAsync("/recordings")).StatusCode);

        // An open circuit keeps the principal it started with; the revalidator checks its session row every minute.
        var sid = _app.Service<SessionStore>().Create(other.Id, "google");
        var circuit = new AuthenticationState(ArchiveClaims.For(users.Get(other.Id)!, sid, "google", "sub-second"));
        var check = _app.Service<SessionRevalidator.Check>();
        Assert.True(check.IsValid(circuit));

        users.SetEnabled(other.Id, false, Actor.System);

        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/recordings")).StatusCode);
        Assert.False(check.IsValid(circuit));
    }

    [Fact]
    public async Task The_denied_page_never_echoes_its_input()
    {
        var html = await ArchiveApp.Page(_app.Anonymous(), "/denied?reason=%3Cscript%3Ealert(1)%3C%2Fscript%3E");
        Assert.DoesNotContain("<script>alert(1)", html);
        Assert.Contains("Sign-in did not complete", html);
    }

    [Theory]
    [InlineData("/_framework/blazor.web.js")]
    [InlineData("/lib/bootstrap/bootstrap.min.css")]
    [InlineData("/css/app.css")]
    public async Task The_sign_in_page_assets_load_without_a_session(string path)
        => Assert.Equal(HttpStatusCode.OK, (await _app.Anonymous().GetAsync(path)).StatusCode);

    [Fact]
    public async Task The_blazor_hub_needs_a_session()
    {
        using var negotiate = await _app.Anonymous().PostAsync("/_blazor/negotiate?negotiateVersion=1", null);
        Assert.Equal(HttpStatusCode.Unauthorized, negotiate.StatusCode);
    }
}
```
If `/_framework/blazor.web.js` answers 401, the script is served by an endpoint other than `MapStaticAssets` in this SDK: find it with `dotnet run` and `/_framework/blazor.web.js` in a browser's network tab, and give only that endpoint `AllowAnonymous` (never the Razor components endpoints).

- [ ] **Step 4: Run them and see them fail**

```powershell
dotnet test -c Release --filter "FullyQualifiedName~SignInFlowTests"
```
Expected: compile errors (`ArchiveClaims.For`, `SessionRevalidator`, the sign-in pages), then failing redirects.

- [ ] **Step 5: Implement the adapter, revalidation and hosting**

Add to `src/WebAudioArchive.Web/Accounts/Roles.cs`, inside `ArchiveClaims`:
```csharp
    /// <summary>The session's claims for this person: rebuilt from the database on every request, so a role change applies at once.</summary>
    public static IEnumerable<Claim> Claims(UserRecord user, string sessionId) =>
    [
        new(SessionId, sessionId),
        new(UserId, user.Id.ToString(CultureInfo.InvariantCulture)),
        new(ClaimTypes.Name, user.Label),
        new(ClaimTypes.Email, user.Email),
        new(ClaimTypes.Role, Roles.Name(user.Role)),
    ];

    public static ClaimsPrincipal For(UserRecord user, string sessionId, string provider, string subject)
    {
        var identity = new ClaimsIdentity(PanoramicData.AuthMagic.AspNetCore.AuthMagicServerDefaults.CookieScheme, ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(PanoramicData.AuthMagic.AspNetCore.AuthMagicServerDefaults.ProviderClaim, provider));
        identity.AddClaim(new Claim(PanoramicData.AuthMagic.AspNetCore.AuthMagicServerDefaults.SubjectClaim, subject));
        identity.AddClaims(Claims(user, sessionId));
        return new ClaimsPrincipal(identity);
    }
```

`src/WebAudioArchive.Web/Accounts/ArchiveGate.cs`:
```csharp
using System.Net;
using System.Security.Claims;
using PanoramicData.AuthMagic.AspNetCore;

namespace WebAudioArchive.Web.Accounts;

/// <summary>
/// The archive's answers to AuthMagic: who may sign in (AccountGate), the session row behind each cookie
/// (SessionStore), and the audit entries for sign-in, refusal and sign-out. The only AuthMagic-aware class besides hosting.
/// </summary>
public sealed class ArchiveGate(AccountGate gate, SessionStore sessions, AuditLog audit) : IAuthMagicAccountGate
{
    public Task<AuthMagicAdmission> AdmitAsync(AuthMagicExternalAccount account, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var decision = gate.Decide(new ExternalAccount(account.Provider, account.Subject, account.VerifiedEmails, account.DisplayName));
        if (decision.User is not { } user)
        {
            audit.Write(AuditActions.SignInRefused, decision.Email is null ? null : new Actor(null, decision.Email), decision.Email,
                $"provider={account.Provider} reason={decision.Refusal}", RemoteIp(httpContext));
            return Task.FromResult(AuthMagicAdmission.Refuse(decision.Refusal ?? "failed"));
        }

        var sessionId = sessions.Create(user.Id, account.Provider);
        audit.Write(AuditActions.SignIn, new Actor(user.Id, user.Email), user.Email, $"provider={account.Provider}", RemoteIp(httpContext));
        return Task.FromResult(AuthMagicAdmission.Admit(ArchiveClaims.Claims(user, sessionId)));
    }

    public Task<ClaimsPrincipal?> ValidateSessionAsync(ClaimsPrincipal principal, HttpContext httpContext, CancellationToken cancellationToken)
    {
        var sessionId = principal.FindFirst(ArchiveClaims.SessionId)?.Value;
        var provider = principal.FindFirst(AuthMagicServerDefaults.ProviderClaim)?.Value;
        var subject = principal.FindFirst(AuthMagicServerDefaults.SubjectClaim)?.Value;
        if (sessionId is null || provider is null || subject is null) return Task.FromResult<ClaimsPrincipal?>(null);
        var valid = sessions.Validate(sessionId, touch: true);
        return Task.FromResult(valid is null ? null : ArchiveClaims.For(valid.User, sessionId, provider, subject));
    }

    public Task SignedOutAsync(ClaimsPrincipal principal, HttpContext httpContext, CancellationToken cancellationToken)
    {
        if (principal.FindFirst(ArchiveClaims.SessionId)?.Value is { } sessionId) sessions.Delete(sessionId);
        if (Viewer.From(principal) is { } viewer) audit.Write(AuditActions.SignOut, viewer.Actor, viewer.Email, null, RemoteIp(httpContext));
        return Task.CompletedTask;
    }

    public static string? RemoteIp(HttpContext http) => http.Connection.RemoteIpAddress is { } ip
        ? (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString()
        : null;
}
```

`src/WebAudioArchive.Web/Accounts/SessionRevalidator.cs`:
```csharp
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using System.Security.Claims;

namespace WebAudioArchive.Web.Accounts;

/// <summary>
/// An open circuit re-checks its session every minute: signed out elsewhere, disabled, removed or a changed role, and
/// the page drops to signed out (AuthorizeRouteView then sends it to sign in).
/// </summary>
public sealed class SessionRevalidator(ILoggerFactory loggerFactory, SessionRevalidator.Check check) : RevalidatingServerAuthenticationStateProvider(loggerFactory)
{
    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(1);

    protected override Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
        => Task.FromResult(check.IsValid(authenticationState));

    /// <summary>The check itself, a singleton so tests can call it.</summary>
    public sealed class Check(SessionStore sessions)
    {
        public bool IsValid(AuthenticationState state)
        {
            var sessionId = state.User.FindFirst(ArchiveClaims.SessionId)?.Value;
            if (sessionId is null) return false;
            var valid = sessions.Validate(sessionId, touch: false);
            return valid is not null && Roles.Name(valid.User.Role) == state.User.FindFirst(ClaimTypes.Role)?.Value;
        }
    }
}

/// <summary>Interactive use of a page (clicks, typing) counts as activity for the idle timeout, at most once a minute.</summary>
public sealed class SessionActivityHandler(AuthenticationStateProvider authentication, SessionStore sessions, TimeProvider time) : CircuitHandler
{
    DateTimeOffset _last = DateTimeOffset.MinValue;

    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next) => async context =>
    {
        var now = time.GetUtcNow();
        if (now - _last >= SessionStore.TouchInterval)
        {
            _last = now;
            var state = await authentication.GetAuthenticationStateAsync();
            if (state.User.FindFirst(ArchiveClaims.SessionId)?.Value is { } sessionId) sessions.Touch(sessionId);
        }

        await next(context);
    };
}
```

`src/WebAudioArchive.Web/Accounts/AccountsHosting.cs`:
```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.DataProtection;
using PanoramicData.AuthMagic.AspNetCore;

namespace WebAudioArchive.Web.Accounts;

/// <summary>Everything sign-in adds to the web app, in one place so Program.cs changes by a few lines.</summary>
public static class AccountsHosting
{
    public static void AddArchiveAccounts(this WebApplicationBuilder builder, AuthSettings auth)
    {
        var s = builder.Services;
        s.AddSingleton(auth);
        s.AddSingleton(new AppDb(auth.DbPath));
        s.AddSingleton<AuditLog>();
        s.AddSingleton<UserStore>();
        s.AddSingleton<SessionStore>();
        s.AddSingleton<AccountGate>();
        s.AddSingleton<SessionRevalidator.Check>();
        s.AddHostedService<AccountsHousekeeper>();

        // Keys for the session, antiforgery and circuit cookies: in the app folder, beside the database (B11 protects them).
        s.AddDataProtection()
            .SetApplicationName("WebAudioArchive.Web")
            .PersistKeysToFileSystem(new DirectoryInfo(auth.KeysDir));

        s.AddAntiforgery(o =>
        {
            o.Cookie.Name = auth.IsHttps ? "__Host-waa-af" : "waa-af";
            o.Cookie.SecurePolicy = auth.IsHttps ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.Cookie.HttpOnly = true;
            o.Cookie.Path = "/"; // required by the __Host- prefix
        });

        s.AddAuthMagicServer<ArchiveGate>(o =>
        {
            o.PublicOrigin = auth.PublicUrl;
            o.Google = auth.Google is { } g ? new AuthMagicServerProviderOptions { ClientId = g.ClientId, ClientSecret = g.ClientSecret } : null;
            o.GitHub = auth.GitHub is { } h ? new AuthMagicServerProviderOptions { ClientId = h.ClientId, ClientSecret = h.ClientSecret } : null;
            o.SessionLifetime = auth.MaxSessionAge;
        });

        // Deny by default: every endpoint and page without its own policy needs a signed-in manager or presenter,
        // including the Blazor hub. Only static assets, /healthz and the sign-in pages opt out.
        s.AddAuthorizationBuilder()
            .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().RequireRole(Roles.Manager, Roles.Presenter).Build())
            .AddPolicy(ArchivePolicies.Manager, p => p.RequireAuthenticatedUser().RequireRole(Roles.Manager))
            .AddPolicy(ArchivePolicies.Member, p => p.RequireAuthenticatedUser().RequireRole(Roles.Manager, Roles.Presenter));

        s.AddCascadingAuthenticationState();
        s.AddScoped<AuthenticationStateProvider, SessionRevalidator>();
        s.AddScoped<CircuitHandler, SessionActivityHandler>();
    }

    /// <summary>Migrates the database, makes the bootstrap manager, and reports what will and will not work.</summary>
    public static void StartArchiveAccounts(this WebApplication app)
    {
        var auth = app.Services.GetRequiredService<AuthSettings>();
        var now = app.Services.GetRequiredService<TimeProvider>().GetUtcNow();
        app.Services.GetRequiredService<AppDb>().Migrate(now, app.Logger);
        var users = app.Services.GetRequiredService<UserStore>();
        if (auth.BootstrapManagerEmail is { } email) users.EnsureBootstrapManager(email);

        var all = users.All();
        if (!all.Any(u => u.Role == Role.Manager && u.Enabled))
            app.Logger.LogError("No manager can sign in: set BOOTSTRAP_MANAGER_EMAIL to the manager's Google or GitHub email address.");
        var presenters = all.Count(u => u.Role == Role.Presenter);
        if (auth.Mode == InstanceMode.ManagerOnly && presenters > 0)
            app.Logger.LogWarning("INSTANCE_MODE is manager-only: {Count} presenter accounts are stored here and cannot sign in.", presenters);
        app.Logger.LogInformation("Sign-in at {Origin}, mode {Mode}, providers {Providers}; sessions end after {Idle} idle minutes and at most {Max} hours.",
            auth.Origin, auth.Mode, string.Join(" and ", new[] { auth.Google is null ? null : "Google", auth.GitHub is null ? null : "GitHub" }.OfType<string>()),
            auth.IdleTimeout.TotalMinutes, auth.MaxSessionAge.TotalHours);
    }

    /// <summary>The middleware order sign-in needs. Call before mapping endpoints.</summary>
    public static void UseArchiveSecurity(this WebApplication app, AuthSettings auth)
    {
        app.UseAuthMagicCanonicalOrigin("/healthz");
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
    }

    public static void MapArchiveAccounts(this WebApplication app)
    {
        app.MapAuthMagicServer();
    }
}

/// <summary>At start-up and then daily: back up the database (14 kept) and drop finished sessions.</summary>
public sealed class AccountsHousekeeper(AppDb db, SessionStore sessions, TimeProvider time, ILogger<AccountsHousekeeper> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromDays(1), time);
        do
        {
            try
            {
                var backup = db.BackupNow(time.GetUtcNow());
                var pruned = sessions.Prune();
                log.LogInformation("Database backed up to {Backup}; {Pruned} finished sessions removed", backup, pruned);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                log.LogError(ex, "Database housekeeping failed for {Path}", db.DbPath);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
```

- [ ] **Step 6: Program.cs**

Edit `src/WebAudioArchive.Web/Program.cs` (keep every other line, including changes other agents made since):

1. Add `using WebAudioArchive.Web.Accounts;` with the other usings.
2. Replace the settings block:
```csharp
WebSettings settings;
AuthSettings auth;
try
{
    settings = WebSettings.Load(Setting);
    auth = AuthSettings.Load(Setting);
    AppDb.PrepareDirectory(auth.AppDir, null);
}
catch (SettingsException ex)
{
    Console.Error.WriteLine("config error: " + ex.Message);
    return 2;
}
```
3. Replace the data protection block (the comment and `builder.Services.AddDataProtection()...PersistKeysToFileSystem(...)` statement) with:
```csharp
builder.AddArchiveAccounts(auth);
```
and remove the now unused `using Microsoft.AspNetCore.DataProtection;`.
4. After `var app = builder.Build();`:
```csharp
try
{
    app.StartArchiveAccounts();
}
catch (SettingsException ex)
{
    Console.Error.WriteLine("config error: " + ex.Message);
    return 2;
}
```
5. Change the start-up log message `"Serving station {Station} from recorders {Recorders}; clip cache {Cache}. No sign-in: publish this port on loopback only."` to `"Serving station {Station} from recorders {Recorders}; clip cache {Cache}."`.
6. Replace
```csharp
app.UseAntiforgery();
app.MapStaticAssets();
```
with
```csharp
app.UseArchiveSecurity(auth);
app.MapStaticAssets().AllowAnonymous();
app.MapArchiveAccounts();
```
7. Add `.AllowAnonymous()` after the closing `});` of `app.MapGet("/healthz", ...)` (the container health check has no session).

- [ ] **Step 7: Pages, routes and layout**

`src/WebAudioArchive.Web/Components/_Imports.razor`, add:
```razor
@using Microsoft.AspNetCore.Authorization
@using Microsoft.AspNetCore.Components.Authorization
@using PanoramicData.AuthMagic.AspNetCore.Components
@using WebAudioArchive.Web.Accounts
```

`src/WebAudioArchive.Web/Components/App.razor`: change `<HeadOutlet @rendermode="InteractiveServer" />` to `<HeadOutlet @rendermode="PageRenderMode" />` and `<Routes @rendermode="InteractiveServer" />` to `<Routes @rendermode="PageRenderMode" />`, and add at the end of the file:
```razor
@code {
    [CascadingParameter] HttpContext HttpContext { get; set; } = default!;

    // Pages marked [ExcludeFromInteractiveRouting] (sign-in, denied, sign-out) render statically: no circuit for them.
    IComponentRenderMode? PageRenderMode => HttpContext.AcceptsInteractiveRouting() ? InteractiveServer : null;
}
```

`src/WebAudioArchive.Web/Components/Routes.razor` (replace the `RouteView` with `AuthorizeRouteView`):
```razor
<Router AppAssembly="typeof(App).Assembly">
    <Found Context="routeData">
        <AuthorizeRouteView RouteData="routeData" DefaultLayout="typeof(MainLayout)">
            <NotAuthorized>
                @if (context.User.Identity?.IsAuthenticated != true)
                {
                    <RedirectToSignIn />
                }
                else
                {
                    <h1 class="h3">Not available</h1>
                    <p id="not-authorized">This page is for managers. <a href="shows">Your shows</a></p>
                }
            </NotAuthorized>
            <Authorizing>
                <p class="text-body-secondary">Checking your sign-in...</p>
            </Authorizing>
        </AuthorizeRouteView>
        <FocusOnNavigate RouteData="routeData" Selector="h1" />
    </Found>
    <NotFound>
        <LayoutView Layout="typeof(MainLayout)">
            <h1>Not found</h1>
            <p>There is nothing at this address.</p>
        </LayoutView>
    </NotFound>
</Router>
```

`src/WebAudioArchive.Web/Components/Shared/RedirectToSignIn.razor`:
```razor
@inject NavigationManager Nav
@code {
    protected override void OnInitialized()
    {
        var back = "/" + Nav.ToBaseRelativePath(Nav.Uri);
        Nav.NavigateTo("signin?returnUrl=" + Uri.EscapeDataString(back), forceLoad: true);
    }
}
```

`src/WebAudioArchive.Web/Components/Pages/SignIn.razor`:
```razor
@page "/signin"
@attribute [AllowAnonymous]
@attribute [ExcludeFromInteractiveRouting]

<PageTitle>Sign in - WebAudioArchive</PageTitle>

<div class="row justify-content-center">
    <div class="col-12 col-md-7 col-lg-5">
        <h1 class="h3">Sign in</h1>
        <p>This archive is private. Sign in with the Google or GitHub account whose email address the station manager added.</p>
        <AuthMagicSignInButtons ReturnPath="@ReturnUrl" ButtonCssClass="btn btn-primary w-100 mb-2" />
        <p class="small text-body-secondary mt-3">Only accounts with a verified email address can sign in. Nothing is shared with Google or GitHub beyond the sign-in itself.</p>
    </div>
</div>

@code {
    [SupplyParameterFromQuery(Name = "returnUrl")] public string? ReturnUrl { get; set; }
}
```

`src/WebAudioArchive.Web/Components/Pages/Denied.razor`:
```razor
@page "/denied"
@attribute [AllowAnonymous]
@attribute [ExcludeFromInteractiveRouting]
@inject NavigationManager Nav

<PageTitle>Not signed in - WebAudioArchive</PageTitle>

<h1 class="h3">@(Reason is null ? "Not available" : "Not signed in")</h1>
<div class="alert alert-warning" id="denied-reason" role="alert">@Text</div>
<p><a href="signin">Sign in again</a></p>

@code {
    [CascadingParameter] HttpContext? HttpContext { get; set; }
    [SupplyParameterFromQuery(Name = "reason")] public string? Reason { get; set; }
    [SupplyParameterFromQuery(Name = "returnUrl")] public string? ReturnUrl { get; set; }

    protected override void OnInitialized()
    {
        // A presenter opening the manager's home page (Status) goes to their shows instead.
        if (Reason is null && ReturnUrl == "/" && HttpContext?.User.IsInRole(Roles.Presenter) == true) Nav.NavigateTo("shows");
    }

    // Fixed texts only: the reason is a code, never echoed.
    string Text => Reason switch
    {
        null => "This page is for managers.",
        "not-listed" => "This account is not on this archive's list. Ask the station manager to add the email address of your Google or GitHub account.",
        "email-not-verified" => "Your Google or GitHub account has no verified email address. Verify it with Google or GitHub, then try again.",
        "account-changed" => "A different Google or GitHub account was used with this email address before. Ask the station manager to reset your sign-in.",
        "disabled" => "Your access to this archive has been turned off. Ask the station manager.",
        "managers-only" => "This copy of the archive is for managers only. Presenters use the main archive.",
        "cancelled" => "Sign-in was cancelled.",
        "provider-error" => "Google or GitHub did not answer as expected. Try again in a minute.",
        _ => "Sign-in did not complete. Try again.",
    };
}
```

`src/WebAudioArchive.Web/Components/Pages/SignOut.razor` (needs a signed-in user: the fallback policy applies):
```razor
@page "/signout"
@attribute [ExcludeFromInteractiveRouting]

<PageTitle>Sign out - WebAudioArchive</PageTitle>

<h1 class="h3">Sign out</h1>
<p>Sign out of the archive on this browser. Your Google or GitHub session is not affected.</p>
<form method="post" action="/auth/signout">
    <AntiforgeryToken />
    <button type="submit" class="btn btn-primary" id="signout">Sign out</button>
</form>
```

`src/WebAudioArchive.Web/Components/Pages/SignedOut.razor`:
```razor
@page "/signed-out"
@attribute [AllowAnonymous]
@attribute [ExcludeFromInteractiveRouting]

<PageTitle>Signed out - WebAudioArchive</PageTitle>

<h1 class="h3">Signed out</h1>
<p>You have signed out of the archive. You may still be signed in to Google or GitHub in this browser.</p>
<p><a href="signin">Sign in again</a></p>
```

`src/WebAudioArchive.Web/Components/Layout/MainLayout.razor`: replace the "No sign-in: loopback only" placeholder with the signed-in person and a sign-out link, and show the manager pages only to managers (the server enforces this anyway, B7):
```razor
        <PDToolbarPlaceholder>
            @if (Me is not null)
            {
                <span class="badge text-bg-secondary me-1" id="signed-in-as" title="Signed in as @Me.Email">@Me.Name (@Roles.Name(Me.Role))</span>
                <a href="signout" class="btn btn-sm btn-outline-secondary" id="signout-link" data-enhance-nav="false">Sign out</a>
            }
        </PDToolbarPlaceholder>
```
In the `<nav>`, wrap the Status, Recordings and Player links in `@if (Me?.IsManager == true) { ... }` and the Shows link in `@if (Me is not null) { ... }` (the sign-in pages use the same layout and show no links), and add:
```razor
@code {
    [CascadingParameter] Task<AuthenticationState>? AuthState { get; set; }
    Viewer? Me { get; set; }

    protected override async Task OnParametersSetAsync() =>
        Me = AuthState is null ? null : Viewer.From((await AuthState).User);
}
```

- [ ] **Step 8: The existing web tests sign in as the manager**

`tests/WebAudioArchive.Tests/WebAppTests.cs`:
- add field `readonly FakeIdentityProvider _idp = new();`;
- in `InitializeAsync`, inside `WithWebHostBuilder(b => { ... })` add `TestSignIn.Configure(b, Path.Combine(_root, "app"), _idp);`;
- make `InitializeAsync` `async Task` and replace `_client = _factory.CreateClient();` with `_client = await TestSignIn.SignInAsync(_factory, _idp, TestSignIn.Manager);` and remove the `return Task.CompletedTask;`;
- in `DisposeAsync` add `_idp.Dispose();` and `Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();` before deleting `_root`;
- in `Status_page_shows_each_recorder_with_its_state_and_utc_times` replace `Assert.Contains("No sign-in", html);` with `Assert.Contains("id=\"signed-in-as\"", html);`.

`tests/WebAudioArchive.Tests/ShowsPageTests.cs` (`ShowsAppFixture`): the same three changes (an `Idp`, `TestSignIn.Configure(builder, Path.Combine(Root, "app"), Idp);`, and `Client = await TestSignIn.SignInAsync(Factory, Idp, TestSignIn.Manager);` in an `async` `InitializeAsync`), and dispose the provider and clear the pools.

- [ ] **Step 9: Run everything**

```powershell
dotnet test -c Release
```
Expected: all tests pass, including every existing web test (now signed in) and `SignInFlowTests`. If a test fails with "Correlation failed", the handler cookies were dropped: check `TestSignIn.Configure` set `TimeProvider.System` on all three schemes and that the client's base address is `https://localhost`. If the Status page fails to render for lack of `AuthenticationState`, check `AddCascadingAuthenticationState` and that `MainLayout` takes it as a nullable cascading parameter.

- [ ] **Step 10: Commit**

```powershell
git add src/WebAudioArchive.Web tests/WebAudioArchive.Tests
git commit -m "Sign in with Google or GitHub through PanoramicData.AuthMagic.AspNetCore: server sessions, deny by default, sign-in and sign-out pages (#100, #1)"
```
(`git add` of these two folders only; check `git status` first that nothing else is staged.)

### Task B7: Authorisation on every request: manager pages, the clip API, media grants, health

**Files:**
- Create: `src/WebAudioArchive.Web/Accounts/ClipGrants.cs`
- Modify: `src/WebAudioArchive.Web/Program.cs`, `Accounts/AccountsHosting.cs`, `Components/Pages/Status.razor`, `Recordings.razor`, `Player.razor`, `Shows.razor`, `ShowPage.razor`
- Test: `tests/WebAudioArchive.Tests/ClipGrantsTests.cs`, `tests/WebAudioArchive.Tests/AuthorizationMatrixTests.cs`

**Interfaces:**
- Produces: `ClipGrants.Grant(long userId, string clipId)`, `Has(long userId, string clipId)`, `Prune()`, lifetime 24 hours; policies applied: Status, Recordings, Player, `/api/clip` manager-only; Shows, ShowPage, `/media/clip` members; `/healthz` anonymous with details only for loopback callers.

- [ ] **Step 1: Write the failing tests**

`tests/WebAudioArchive.Tests/ClipGrantsTests.cs`:
```csharp
extern alias web;

using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Accounts;

namespace WebAudioArchive.Tests;

public sealed class ClipGrantsTests
{
    const string Clip = "0123456789abcdef0123456789abcdef";

    [Fact]
    public void A_grant_is_per_person_per_clip_and_lasts_a_day()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 15, 21, 30, 0, TimeSpan.Zero));
        var grants = new ClipGrants(time);
        grants.Grant(7, Clip);

        Assert.True(grants.Has(7, Clip));
        Assert.False(grants.Has(8, Clip));
        Assert.False(grants.Has(7, "fedcba9876543210fedcba9876543210"));
        time.Advance(TimeSpan.FromHours(24));
        Assert.False(grants.Has(7, Clip));
        Assert.Equal(1, grants.Prune());
    }
}
```

`tests/WebAudioArchive.Tests/AuthorizationMatrixTests.cs`:
```csharp
extern alias web;

using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

/// <summary>Every page and endpoint, as anonymous, presenter (nothing assigned) and manager.</summary>
public sealed class AuthorizationMatrixTests : IAsyncLifetime
{
    const string ClipQuery = "/api/clip?from=2026-10-15T19:10:00Z&to=2026-10-15T19:11:00Z";
    readonly ArchiveApp _app = new();
    HttpClient _manager = null!;
    HttpClient _presenter = null!;
    HttpClient _anonymous = null!;
    string _clipId = null!;

    public async Task InitializeAsync()
    {
        _manager = await _app.ManagerAsync();
        _presenter = await _app.PresenterAsync();
        _anonymous = _app.Anonymous();
        _clipId = JsonDocument.Parse(await _manager.GetStringAsync(ClipQuery)).RootElement.GetProperty("id").GetString()!;
    }

    public async Task DisposeAsync() => await _app.DisposeAsync();

    static HttpRequestMessage Html(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        return request;
    }

    // B8, B9 and B10 add "/assignments", "/audit" and "/users" to this list.
    public static TheoryData<string> ManagerPages => new() { "/", "/recordings", "/player", "/player?from=2026-10-15T19:10:00Z&to=2026-10-15T19:11:00Z" };

    [Theory]
    [MemberData(nameof(ManagerPages))]
    public async Task Manager_pages_are_for_managers_only(string url)
    {
        Assert.Equal(HttpStatusCode.OK, (await _manager.SendAsync(Html(url))).StatusCode);

        using var presenter = await _presenter.SendAsync(Html(url));
        Assert.Equal(HttpStatusCode.Redirect, presenter.StatusCode);
        Assert.StartsWith("https://localhost/denied?returnUrl=", presenter.Headers.Location!.AbsoluteUri);
        Assert.Equal(HttpStatusCode.Forbidden, (await _presenter.GetAsync(url)).StatusCode);

        using var anonymous = await _anonymous.SendAsync(Html(url));
        Assert.StartsWith("https://localhost/signin?returnUrl=", anonymous.Headers.Location!.AbsoluteUri);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.GetAsync(url)).StatusCode);
    }

    [Theory]
    [InlineData("/shows")]
    [InlineData("/shows/burst-20261015T1900Z")]
    [InlineData("/signout")]
    public async Task Member_pages_need_a_session(string url)
    {
        Assert.Equal(HttpStatusCode.OK, (await _manager.SendAsync(Html(url))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await _presenter.SendAsync(Html(url))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task A_presenter_opening_the_home_page_is_sent_to_their_shows()
    {
        using var first = await _presenter.SendAsync(Html("/"));
        using var second = await _presenter.SendAsync(Html(first.Headers.Location!.PathAndQuery));
        Assert.Equal(HttpStatusCode.Redirect, second.StatusCode);
        Assert.EndsWith("/shows", second.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task The_clip_api_is_for_managers_only()
    {
        Assert.Equal(HttpStatusCode.OK, (await _manager.GetAsync(ClipQuery)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await _presenter.GetAsync(ClipQuery)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.GetAsync(ClipQuery)).StatusCode);
    }

    [Fact]
    public async Task Media_is_served_to_managers_and_absent_for_anyone_without_a_grant()
    {
        var url = $"/media/clip/{_clipId}.mp3";
        Assert.Equal(HttpStatusCode.OK, (await _manager.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _presenter.GetAsync(url)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _presenter.GetAsync(url + "?download=1")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _presenter.GetAsync($"/media/clip/{new string('0', 32)}.mp3")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _anonymous.GetAsync(url)).StatusCode);
    }

    [Fact]
    public async Task Healthz_tells_strangers_only_the_status()
    {
        var json = JsonDocument.Parse(await _anonymous.GetStringAsync("/healthz")).RootElement;
        Assert.Equal("ok", json.GetProperty("status").GetString());
        Assert.False(json.TryGetProperty("recorders", out _));
    }

    [Fact]
    public async Task Healthz_gives_details_to_the_container_health_check_on_loopback()
    {
        await using var app = new ArchiveApp(configure: b => b.ConfigureTestServices(s => s.AddSingleton<IStartupFilter, FromLoopback>()));
        var json = JsonDocument.Parse(await app.Anonymous().GetStringAsync("/healthz")).RootElement;
        Assert.Equal(2, json.GetProperty("recorders").GetArrayLength());
    }

    sealed class FromLoopback : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Loopback;
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
```

- [ ] **Step 2: Run them and see them fail**

```powershell
dotnet test -c Release --filter "FullyQualifiedName~AuthorizationMatrixTests|FullyQualifiedName~ClipGrantsTests"
```
Expected: `ClipGrants` missing; then the presenter gets 200 on manager pages and media.

- [ ] **Step 3: Implement**

`src/WebAudioArchive.Web/Accounts/ClipGrants.cs`:
```csharp
using System.Collections.Concurrent;

namespace WebAudioArchive.Web.Accounts;

/// <summary>
/// Which clips a presenter may fetch from /media/clip: those their own show page made for them, for as long as the
/// clip cache keeps a clip (24 hours). Clip ids are content hashes shared by everyone, so the id alone proves nothing.
/// In memory: after a restart the presenter's page makes (and grants) the clip again when reloaded.
/// </summary>
public sealed class ClipGrants(TimeProvider time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(24);
    readonly ConcurrentDictionary<(long UserId, string ClipId), DateTimeOffset> _grants = new();

    public void Grant(long userId, string clipId)
    {
        _grants[(userId, clipId)] = time.GetUtcNow() + Lifetime;
        if (_grants.Count > 50_000) Prune();
    }

    public bool Has(long userId, string clipId) => _grants.TryGetValue((userId, clipId), out var until) && time.GetUtcNow() < until;

    public int Prune()
    {
        var now = time.GetUtcNow();
        var removed = 0;
        foreach (var (key, until) in _grants)
            if (until <= now && _grants.TryRemove(key, out _)) removed++;
        return removed;
    }
}
```
In `AccountsHosting.AddArchiveAccounts` add `s.AddSingleton<ClipGrants>();`.

Page attributes (one line under each `@page`):
- `Components/Pages/Status.razor`, `Recordings.razor`, `Player.razor`: `@attribute [Authorize(Policy = ArchivePolicies.Manager)]`
- `Components/Pages/Shows.razor`, `ShowPage.razor`: `@attribute [Authorize(Policy = ArchivePolicies.Member)]`

`src/WebAudioArchive.Web/Program.cs`:
- add `using System.Net;`;
- `/healthz`: give the handler an `HttpContext http` parameter and return the detail only to loopback callers:
```csharp
app.MapGet("/healthz", (WebSettings s, HttpContext http) =>
{
    var recorders = s.Recorders.Select(r => new { id = r.Id, root = r.Root, rootExists = Directory.Exists(r.Root) }).ToList();
    var ok = recorders.All(r => r.rootExists);
    var status = ok ? "ok" : "data root missing";
    var code = ok ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
    // Paths and recorder ids only for callers on this machine (the container's own health check); others get the status.
    var local = http.Connection.RemoteIpAddress is { } ip && IPAddress.IsLoopback(ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip);
    return local
        ? Results.Json(new { status, station = s.Station, recorders }, statusCode: code)
        : Results.Json(new { status }, statusCode: code);
}).AllowAnonymous();
```
- `/api/clip`: append `.RequireAuthorization(ArchivePolicies.Manager)` to the `MapGet`.
- `/media/clip/{id}.mp3`: replace the handler with the grant check:
```csharp
app.MapGet("/media/clip/{id}.mp3", (string id, HttpContext http, WebSettings s, ClipGrants grants) =>
{
    // Only a 32 character lower-case hex id reaches the file system, and only inside the cache directory.
    if (!ClipBuilder.IsValidId(id)) return Results.NotFound();
    // Managers may play any clip; a presenter only one their own show page made for them. Anything else looks absent.
    var viewer = Viewer.From(http.User);
    if (viewer is null || (!viewer.IsManager && !grants.Has(viewer.UserId, id))) return Results.NotFound();
    var path = ClipBuilder.PathFor(s.CacheDir, id);
    if (!File.Exists(path)) return Results.NotFound();
    var download = http.Request.Query["download"] == "1";
    var name = download ? ClipBuilder.ReadInfo(s.CacheDir, id)?.DownloadName ?? $"{s.Station}_{id}.mp3" : null;
    return Results.File(path, "audio/mpeg", name, enableRangeProcessing: true);
}).RequireAuthorization(ArchivePolicies.Member);
```

- [ ] **Step 4: Run all tests and commit**

```powershell
dotnet test -c Release
git add src/WebAudioArchive.Web tests/WebAudioArchive.Tests
git commit -m "Authorise every request: manager-only pages and clip API, media only by grant, minimal health for strangers (#90)"
```
Expected: everything passes (the existing web tests run as the manager).

### Task B8: Assignments and the presenter view

**Files:**
- Create: `src/WebAudioArchive.Web/Accounts/AssignmentStore.cs` (with `ShowAccess`), `Components/Pages/Assignments.razor`, `Components/Pages/Assignments.razor.cs`
- Modify: `Accounts/AccountsHosting.cs`, `Components/Pages/Shows.razor`, `Shows.razor.cs`, `ShowPage.razor`, `ShowPage.razor.cs`, `Components/Layout/MainLayout.razor`, `tests/WebAudioArchive.Tests/Support/ArchiveApp.cs` (the `foreach` in `PresenterAsync`), `AuthorizationMatrixTests.cs` (add `/assignments`)
- Test: `tests/WebAudioArchive.Tests/AssignmentStoreTests.cs`, `tests/WebAudioArchive.Tests/PresenterViewTests.cs`

**Interfaces:**
- Produces: `record Assignment(long Id, long UserId, string Station, string ShowTitle, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, DateTimeOffset CreatedUtc, string CreatedBy)` with `Covers(ShowOccurrence)`; `AssignmentStore.ForUser`, `All`, `Add(long userId, string station, string title, DateOnly? firstUtcDate, DateOnly? lastUtcDate, Actor)`, `Remove(long id, Actor)`; `ShowAccess.CanSee(Viewer, ShowOccurrence)`, `ShowAccess.Visible(Viewer, IEnumerable<ShowOccurrence>)`.

- [ ] **Step 1: Write the failing tests**

`tests/WebAudioArchive.Tests/AssignmentStoreTests.cs`:
```csharp
extern alias web;

using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Accounts;
using web::WebAudioArchive.Web.Schedule;

namespace WebAudioArchive.Tests;

public sealed class AssignmentStoreTests : IDisposable
{
    readonly AccountsTestDb _t = new();
    readonly AssignmentStore _store;
    readonly UserRecord _presenter;

    public AssignmentStoreTests()
    {
        _t.Users.EnsureBootstrapManager("manager@example.test");
        _presenter = _t.Users.Add("presenter@example.test", null, Role.Presenter, Actor.System);
        _store = new AssignmentStore(_t.Db, _t.Users, _t.Auth, _t.Audit, _t.Time);
    }

    public void Dispose() => _t.Dispose();

    static ShowOccurrence Show(string title, int day, int hour = 19) =>
        new("burst", title, new DateTimeOffset(2026, 10, day, hour, 0, 0, TimeSpan.Zero), new DateTimeOffset(2026, 10, day, hour + 1, 0, 0, TimeSpan.Zero));

    [Fact]
    public void A_title_covers_every_occurrence_ignoring_case_and_spaces()
    {
        var a = _store.Add(_presenter.Id, "burst", "  backseat aux ", null, null, Actor.System);
        Assert.Equal("backseat aux", a.ShowTitle);
        Assert.True(a.Covers(Show("Backseat Aux", 15)));
        Assert.True(a.Covers(Show("Backseat Aux", 22)));
        Assert.False(a.Covers(Show("Backseat Aux Extra", 15)));
        Assert.False(a.Covers(Show("Lo-Fidelity", 15)));
        Assert.False(a.Covers(Show("Backseat Aux", 15) with { Station = "other" }));
    }

    [Fact]
    public void A_date_range_includes_its_first_and_last_utc_dates()
    {
        var a = _store.Add(_presenter.Id, "burst", "Backseat Aux", new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 22), Actor.System);
        Assert.False(a.Covers(Show("Backseat Aux", 8)));
        Assert.True(a.Covers(Show("Backseat Aux", 15, 0)));
        Assert.True(a.Covers(Show("Backseat Aux", 22, 23 - 1)));
        Assert.False(a.Covers(Show("Backseat Aux", 29)));
    }

    [Fact]
    public void Only_presenters_on_the_primary_get_assignments()
    {
        var manager = _t.Users.FindByEmail("manager@example.test")!;
        Assert.Contains("already sees every show", Assert.Throws<AccountException>(() => _store.Add(manager.Id, "burst", "X", null, null, Actor.System)).Message);
        Assert.Contains("exactly as the schedule shows it", Assert.Throws<AccountException>(() => _store.Add(_presenter.Id, "burst", " ", null, null, Actor.System)).Message);
        Assert.Contains("must not be after", Assert.Throws<AccountException>(() => _store.Add(_presenter.Id, "burst", "X", new DateOnly(2026, 10, 2), new DateOnly(2026, 10, 1), Actor.System)).Message);

        var managerOnly = new AssignmentStore(_t.Db, _t.Users, _t.Auth with { Mode = InstanceMode.ManagerOnly }, _t.Audit, _t.Time);
        Assert.Contains("manager-only", Assert.Throws<AccountException>(() => managerOnly.Add(_presenter.Id, "burst", "X", null, null, Actor.System)).Message);
    }

    [Fact]
    public void Show_access_managers_see_all_presenters_their_own_and_nothing_on_a_manager_only_instance()
    {
        _store.Add(_presenter.Id, "burst", "Backseat Aux", null, null, Actor.System);
        var shows = new[] { Show("Backseat Aux", 15), Show("Lo-Fidelity", 15, 18) };
        var access = new ShowAccess(_store, _t.Auth);
        var presenter = new Viewer(_presenter.Id, _presenter.Email, "P", Role.Presenter);
        var manager = new Viewer(1, "manager@example.test", "M", Role.Manager);

        Assert.Equal(2, access.Visible(manager, shows).Count);
        Assert.Equal(new[] { "Backseat Aux" }, access.Visible(presenter, shows).Select(s => s.Title));
        Assert.False(access.CanSee(presenter, shows[1]));
        Assert.Empty(new ShowAccess(_store, _t.Auth with { Mode = InstanceMode.ManagerOnly }).Visible(presenter, shows));
    }

    [Fact]
    public void Adding_and_removing_is_audited()
    {
        var a = _store.Add(_presenter.Id, "burst", "Backseat Aux", null, null, new Actor(1, "manager@example.test"));
        _store.Remove(a.Id, new Actor(1, "manager@example.test"));
        Assert.Empty(_store.ForUser(_presenter.Id));
        Assert.Single(_t.Audit.Query(null, AuditActions.AssignmentAdded, "presenter@example.test"));
        Assert.Single(_t.Audit.Query(null, AuditActions.AssignmentRemoved, "presenter@example.test"));
    }
}
```

`tests/WebAudioArchive.Tests/PresenterViewTests.cs`:
```csharp
extern alias web;

using System.Net;
using System.Text.RegularExpressions;
using WebAudioArchive.Tests.Support;

namespace WebAudioArchive.Tests;

/// <summary>A presenter assigned "Backseat Aux" (Thursday 15 October 2026, 19:00 to 20:00 UTC).</summary>
public sealed partial class PresenterViewTests : IAsyncLifetime
{
    static readonly string[] OtherTitles = ["Lo-Fidelity", "The Noise Exchange", "Reset Cafe", "Breakfast with Alice", "Breakfast with Mackenzie"];
    readonly ArchiveApp _app = new();
    HttpClient _presenter = null!;

    public async Task InitializeAsync() => _presenter = await _app.PresenterAsync("Backseat Aux");

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [GeneratedRegex("/media/clip/([0-9a-f]{32})[.]mp3")]
    private static partial Regex ClipUrl();

    [GeneratedRegex("id=\"show-not-found\"[^>]*>([^<]*)<")]
    private static partial Regex NotFoundText();

    [Fact]
    public async Task The_calendar_shows_only_their_show_and_no_other_title()
    {
        var html = await ArchiveApp.Page(_presenter, "/shows?date=2026-10-15");
        Assert.Contains("Backseat Aux\n2026-10-15 19:00:00 UTC to 2026-10-15 20:00:00 UTC", html);
        foreach (var title in OtherTitles) Assert.DoesNotContain(title, html);
        Assert.DoesNotContain("weekly slots", html); // the schedule summary is for managers
        Assert.DoesNotContain("href=\"recordings\"", html);
        Assert.DoesNotContain("href=\"player\"", html);
    }

    [Fact]
    public async Task Their_show_plays_with_exactly_five_minutes_either_side_and_downloads()
    {
        var html = await ArchiveApp.Page(_presenter, "/shows/burst-20261015T1900Z");
        Assert.Contains("id=\"show-title\">Backseat Aux</h1>", html);
        Assert.Matches("The clip runs from <span class=\"utc\">2026-10-15 18:5(4:59|5:00) UTC</span> to <span class=\"utc\">2026-10-15 20:05:00 UTC</span>", html);
        Assert.DoesNotContain("id=\"show-margin\"", html);

        var clip = ClipUrl().Match(html).Groups[1].Value;
        using var media = await _presenter.GetAsync($"/media/clip/{clip}.mp3");
        Assert.Equal(HttpStatusCode.OK, media.StatusCode);
        Assert.Equal("audio/mpeg", media.Content.Headers.ContentType!.MediaType);
        using var download = await _presenter.GetAsync($"/media/clip/{clip}.mp3?download=1");
        Assert.Equal("burst_20261015T185500Z_20261015T200500Z_A.mp3", download.Content.Headers.ContentDisposition!.FileName);
    }

    [Theory]
    [InlineData("?margin=60")]
    [InlineData("?margin=0")]
    [InlineData("?margin=5&recorder=a")]
    public async Task Presenter_margin_is_fixed(string query)
    {
        var html = await ArchiveApp.Page(_presenter, "/shows/burst-20261015T1900Z" + query);
        Assert.Matches("The clip runs from <span class=\"utc\">2026-10-15 18:5(4:59|5:00) UTC</span> to <span class=\"utc\">2026-10-15 20:05:00 UTC</span>", html);
    }

    [Fact]
    public async Task Another_show_looks_exactly_like_one_that_does_not_exist()
    {
        var other = await ArchiveApp.Page(_presenter, "/shows/burst-20261015T2000Z");   // The Noise Exchange exists
        var missing = await ArchiveApp.Page(_presenter, "/shows/burst-20261015T1930Z"); // nothing starts at 19:30
        Assert.DoesNotContain("The Noise Exchange", other);
        Assert.DoesNotContain("pd-media-player", other);
        Assert.Equal(NotFoundText().Match(missing).Groups[1].Value.Replace("19:30", "20:00"), NotFoundText().Match(other).Groups[1].Value);
    }

    [Fact]
    public async Task Another_shows_clip_is_not_served_even_with_its_id()
    {
        var manager = await _app.ManagerAsync();
        var html = await ArchiveApp.Page(manager, "/shows/burst-20261015T2000Z?margin=2");
        var clip = ClipUrl().Match(html).Groups[1].Value;
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync($"/media/clip/{clip}.mp3")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _presenter.GetAsync($"/media/clip/{clip}.mp3")).StatusCode);
    }

    [Fact]
    public async Task The_manager_sees_every_show_and_the_assignments_page()
    {
        var manager = await _app.ManagerAsync();
        var html = await ArchiveApp.Page(manager, "/shows?date=2026-10-15");
        foreach (var title in OtherTitles.Append("Backseat Aux")) Assert.Contains(title, html);
        var assignments = await ArchiveApp.Page(manager, "/assignments");
        Assert.Contains("presenter@example.test", assignments);
        Assert.Contains("Backseat Aux", assignments);
        Assert.Contains("id=\"assignment-add\"", assignments);
    }

    [Fact]
    public async Task On_a_manager_only_instance_there_are_no_assignments()
    {
        await using var secondary = new ArchiveApp(mode: "manager-only");
        var html = await ArchiveApp.Page(await secondary.ManagerAsync(), "/assignments");
        Assert.Contains("id=\"assignments-manager-only\"", html);
        Assert.DoesNotContain("id=\"assignment-add\"", html);
    }
}
```
Add `"/assignments"` to `AuthorizationMatrixTests.ManagerPages`.

- [ ] **Step 2: Run them and see them fail** (`dotnet build -c Release`: `AssignmentStore` missing.)

- [ ] **Step 3: Implement the store**

`src/WebAudioArchive.Web/Accounts/AssignmentStore.cs`:
```csharp
using System.Globalization;
using WebAudioArchive.Web.Schedule;

namespace WebAudioArchive.Web.Accounts;

/// <summary>A presenter's show: every occurrence of the title on the station, optionally from FromUtc (inclusive) to ToUtc (exclusive).</summary>
public sealed record Assignment(long Id, long UserId, string Station, string ShowTitle, DateTimeOffset? FromUtc, DateTimeOffset? ToUtc, DateTimeOffset CreatedUtc, string CreatedBy)
{
    public bool Covers(ShowOccurrence show) =>
        string.Equals(Station, show.Station, StringComparison.Ordinal)
        && string.Equals(ShowTitle.Trim(), show.Title.Trim(), StringComparison.OrdinalIgnoreCase)
        && (FromUtc is null || show.StartUtc >= FromUtc)
        && (ToUtc is null || show.StartUtc < ToUtc);

    public string RangeText => (FromUtc, ToUtc) switch
    {
        (null, null) => "every occurrence",
        ({ } f, null) => $"from {Date(f)}",
        (null, { } t) => $"until {Date(t.AddDays(-1))}",
        ({ } f, { } t) => $"{Date(f)} to {Date(t.AddDays(-1))}",
    };

    static string Date(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + " UTC";
}

/// <summary>
/// Assignments of presenters to shows by title (spec 3.8). Kept on the primary only; a manager-only instance has none.
/// Derived hosts (from transcripts, later) are never used here.
/// </summary>
public sealed class AssignmentStore(AppDb db, UserStore users, AuthSettings auth, AuditLog audit, TimeProvider time)
{
    const string Columns = "id, user_id, station, show_title, from_utc, to_utc, created_utc, created_by";

    public IReadOnlyList<Assignment> ForUser(long userId)
    {
        using var c = db.Open();
        return c.Query($"SELECT {Columns} FROM assignments WHERE user_id = $u ORDER BY show_title", Map, ("$u", userId));
    }

    public IReadOnlyList<Assignment> All()
    {
        using var c = db.Open();
        return c.Query($"SELECT {Columns} FROM assignments ORDER BY show_title, user_id", Map);
    }

    public Assignment Add(long userId, string station, string title, DateOnly? firstUtcDate, DateOnly? lastUtcDate, Actor actor)
    {
        if (auth.Mode != InstanceMode.Primary)
            throw new AccountException("This is a manager-only instance: assignments are kept on the primary instance only.");
        var user = users.Get(userId) ?? throw new AccountException("There is no such user.");
        if (user.Role != Role.Presenter) throw new AccountException($"{user.Email} is a manager and already sees every show.");
        var t = title.Trim();
        if (t.Length is 0 or > 200) throw new AccountException("Give the show title exactly as the schedule shows it.");
        if (firstUtcDate > lastUtcDate) throw new AccountException("The first date must not be after the last date.");
        DateTimeOffset? from = firstUtcDate is { } f ? new DateTimeOffset(f.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;
        DateTimeOffset? to = lastUtcDate is { } l ? new DateTimeOffset(l.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero) : null;

        using var c = db.Open();
        c.Exec("INSERT INTO assignments (user_id, station, show_title, from_utc, to_utc, created_utc, created_by) VALUES ($u, $s, $t, $f, $to, $now, $by)",
            ("$u", userId), ("$s", station), ("$t", t), ("$f", Sql.Text(from)), ("$to", Sql.Text(to)), ("$now", Sql.Text(time.GetUtcNow())), ("$by", actor.Name));
        var id = c.ScalarLong("SELECT last_insert_rowid()")!.Value;
        var added = ForUser(userId).Single(a => a.Id == id);
        audit.Write(AuditActions.AssignmentAdded, actor, user.Email, $"{station} '{t}' {added.RangeText}");
        return added;
    }

    public void Remove(long id, Actor actor)
    {
        var a = All().SingleOrDefault(x => x.Id == id) ?? throw new AccountException("There is no such assignment.");
        using var c = db.Open();
        c.Exec("DELETE FROM assignments WHERE id = $id", ("$id", id));
        audit.Write(AuditActions.AssignmentRemoved, actor, users.Get(a.UserId)?.Email, $"{a.Station} '{a.ShowTitle}' {a.RangeText}");
    }

    static Assignment Map(Microsoft.Data.Sqlite.SqliteDataReader r) => new(
        r.GetInt64(0), r.GetInt64(1), r.GetString(2), r.GetString(3),
        r.StringOrNull(4) is { } f ? Sql.Time(f) : null,
        r.StringOrNull(5) is { } t ? Sql.Time(t) : null,
        Sql.Time(r.GetString(6)), r.GetString(7));
}

/// <summary>The one place that decides whether someone may see a show. Pages and endpoints call it; links are never the check.</summary>
public sealed class ShowAccess(AssignmentStore assignments, AuthSettings auth)
{
    public bool CanSee(Viewer viewer, ShowOccurrence show) =>
        viewer.IsManager || (auth.Mode == InstanceMode.Primary && assignments.ForUser(viewer.UserId).Any(a => a.Covers(show)));

    public IReadOnlyList<ShowOccurrence> Visible(Viewer viewer, IEnumerable<ShowOccurrence> shows)
    {
        if (viewer.IsManager) return [.. shows];
        if (auth.Mode != InstanceMode.Primary) return [];
        var mine = assignments.ForUser(viewer.UserId);
        return [.. shows.Where(s => mine.Any(a => a.Covers(s)))];
    }
}
```
In `AccountsHosting.AddArchiveAccounts` add `s.AddSingleton<AssignmentStore>();` and `s.AddSingleton<ShowAccess>();`. In `ArchiveApp.PresenterAsync` restore the `foreach` line that adds the assignments.

- [ ] **Step 4: Filter the Shows calendar** (`Components/Pages/Shows.razor.cs`; keep the other agents' changes to this file)

- add `using WebAudioArchive.Web.Accounts;` and `using Microsoft.AspNetCore.Components.Authorization;`;
- add `[Inject] public required ShowAccess Access { get; set; }` and `[CascadingParameter] public Task<AuthenticationState>? AuthState { get; set; }` and a property `Viewer? Me { get; set; }`;
- at the start of `OnInitializedAsync`: `Me = AuthState is null ? null : Viewer.From((await AuthState).User);`
- where the items are built from `Schedule.Occurrences(range.From, range.To)`, filter first: `IReadOnlyList<ShowOccurrence> shows = Me is null ? [] : Access.Visible(Me, Schedule.Occurrences(range.From, range.To));` and build the items from `shows` (the calendar, its counts and tooltips then only ever see the shows this person may see).

`Components/Pages/Shows.razor`: show the `<ScheduleStatus ... />` summary only when `Me?.IsManager == true`; for presenters put `<p class="text-body-secondary" id="shows-yours">Your shows. Times UTC.</p>` in its place.

- [ ] **Step 5: Check access, fix the margin and grant the clip on the show page** (`Components/Pages/ShowPage.razor.cs`)

- add `using WebAudioArchive.Web.Accounts;` and `using Microsoft.AspNetCore.Components.Authorization;`;
- add `[Inject] public required ShowAccess Access { get; set; }`, `[Inject] public required ClipGrants Grants { get; set; }`, `[CascadingParameter] public Task<AuthenticationState>? AuthState { get; set; }`, and `Viewer? Me { get; set; }`;
- in `OnParametersSetAsync`, before the margin is read: `Me = AuthState is null ? null : Viewer.From((await AuthState).User);`
- after the margin is read from the query: `if (Me?.IsManager != true) MarginMinutes = Settings.DefaultMarginMinutes; // presenters: exactly the configured margin`
- straight after `Show = Schedule.Find(Id);`: `if (Show is not null && (Me is null || !Access.CanSee(Me, Show))) Show = null; // not theirs: exactly like a show that does not exist`
- in `OnMarginChanged`, first line: `if (Me?.IsManager != true) return;`
- wherever the page sets `Clip = ...` to a prepared or cached clip (in `PrepareAsync`, and in any cached-clip path the other agents added), call `Accept(clip)` instead, with:
```csharp
    void Accept(PreparedClip clip)
    {
        // A presenter may fetch this clip's audio from /media/clip; nobody else gains anything from the grant.
        if (Me is { IsManager: false } me) Grants.Grant(me.UserId, clip.File.Id);
        Clip = clip;
    }
```

`Components/Pages/ShowPage.razor`: wrap the margin `<div class="col-12 col-sm-auto">` that contains `id="show-margin"` in `@if (Me?.IsManager == true) { ... }`.

`Components/Layout/MainLayout.razor`: inside the manager-only part of the nav add `@if (Auth.Mode == InstanceMode.Primary) { <PDNavLink href="assignments" class="btn btn-sm btn-outline-primary" ActiveClass="active"><i class="fas fa-user-tag me-1" aria-hidden="true"></i>Assignments</PDNavLink> }` and `@inject AuthSettings Auth` at the top.

- [ ] **Step 6: The assignments page**

`src/WebAudioArchive.Web/Components/Pages/Assignments.razor`:
```razor
@page "/assignments"
@attribute [Authorize(Policy = ArchivePolicies.Manager)]

<PageTitle>Assignments - WebAudioArchive</PageTitle>

<h1 class="h3">Assignments</h1>

@if (Auth.Mode != InstanceMode.Primary)
{
    <div class="alert alert-info" id="assignments-manager-only" role="status">
        This is a manager-only instance: there are no presenters here, so nothing is assigned. Assign shows on the primary instance.
    </div>
}
else
{
    <p>A presenter sees only the shows assigned to them: every occurrence of the title, or those between two UTC dates, each with @Settings.DefaultMarginMinutes minutes either side, and nothing else.</p>

    @if (Error is not null)
    {
        <div class="alert alert-danger" id="assignment-error" role="alert">@Error</div>
    }

    <div class="row g-2 align-items-end mb-3">
        <div class="col-12 col-md-3">
            <label class="form-label small mb-1" for="assignment-presenter">Presenter</label>
            <select class="form-select" id="assignment-presenter" @bind="PresenterId">
                <option value="0">Choose...</option>
                @foreach (var p in Presenters)
                {
                    <option value="@p.Id">@p.Label (@p.Email)</option>
                }
            </select>
        </div>
        <div class="col-12 col-md-3">
            <label class="form-label small mb-1" for="assignment-title">Show title</label>
            <input class="form-control" id="assignment-title" list="assignment-titles" @bind="Title" />
            <datalist id="assignment-titles">
                @foreach (var t in Titles)
                {
                    <option value="@t" />
                }
            </datalist>
        </div>
        <div class="col-6 col-md-2">
            <label class="form-label small mb-1" for="assignment-from">First date (UTC, optional)</label>
            <input class="form-control" id="assignment-from" type="date" @bind="First" />
        </div>
        <div class="col-6 col-md-2">
            <label class="form-label small mb-1" for="assignment-to">Last date (UTC, optional)</label>
            <input class="form-control" id="assignment-to" type="date" @bind="Last" />
        </div>
        <div class="col-12 col-md-2">
            <button type="button" class="btn btn-primary w-100" id="assignment-add" @onclick="Add">Assign</button>
        </div>
    </div>

    <table class="table table-sm" id="assignments-table">
        <thead><tr><th>Presenter</th><th>Show</th><th>Occurrences</th><th>Added (UTC)</th><th></th></tr></thead>
        <tbody>
            @foreach (var a in Rows)
            {
                <tr>
                    <td>@(People.TryGetValue(a.UserId, out var u) ? u.Email : "(removed)")</td>
                    <td>@a.ShowTitle</td>
                    <td>@a.RangeText</td>
                    <td class="utc">@Utc(a.CreatedUtc) by @a.CreatedBy</td>
                    <td><button type="button" class="btn btn-sm btn-outline-danger" @onclick="() => Remove(a.Id)">Remove</button></td>
                </tr>
            }
        </tbody>
    </table>
}
```

`src/WebAudioArchive.Web/Components/Pages/Assignments.razor.cs`:
```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using WebAudioArchive.Web.Accounts;
using WebAudioArchive.Web.Clips;
using WebAudioArchive.Web.Schedule;

namespace WebAudioArchive.Web.Components.Pages;

public sealed partial class Assignments
{
    [Inject] public required AssignmentStore Store { get; set; }
    [Inject] public required UserStore Users { get; set; }
    [Inject] public required ScheduleService Schedule { get; set; }
    [Inject] public required AuthSettings Auth { get; set; }
    [Inject] public required WebSettings Settings { get; set; }
    [CascadingParameter] public Task<AuthenticationState>? AuthState { get; set; }

    IReadOnlyList<UserRecord> Presenters { get; set; } = [];
    Dictionary<long, UserRecord> People { get; set; } = [];
    IReadOnlyList<Assignment> Rows { get; set; } = [];
    IReadOnlyList<string> Titles { get; set; } = [];
    long PresenterId { get; set; }
    string Title { get; set; } = "";
    DateOnly? First { get; set; }
    DateOnly? Last { get; set; }
    string? Error { get; set; }
    Viewer? Me { get; set; }

    protected override async Task OnInitializedAsync()
    {
        Me = AuthState is null ? null : Viewer.From((await AuthState).User);
        Titles = Schedule.Current.Schedule?.Slots.Select(s => s.Title).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        Load();
    }

    void Load()
    {
        var all = Users.All();
        People = all.ToDictionary(u => u.Id);
        Presenters = all.Where(u => u.Role == Role.Presenter && u.Enabled).ToList();
        Rows = Store.All();
    }

    void Add()
    {
        Error = null;
        try
        {
            Store.Add(PresenterId, Settings.Station, Title, First, Last, Me!.Actor);
            Title = "";
            First = Last = null;
        }
        catch (AccountException ex)
        {
            Error = ex.Message;
        }

        Load();
    }

    void Remove(long id)
    {
        Error = null;
        try
        {
            Store.Remove(id, Me!.Actor);
        }
        catch (AccountException ex)
        {
            Error = ex.Message;
        }

        Load();
    }

    static string Utc(DateTimeOffset t) => ClipPlanner.Utc(t);
}
```

- [ ] **Step 7: Run all tests and commit**

```powershell
dotnet test -c Release
git add src/WebAudioArchive.Web tests/WebAudioArchive.Tests
git commit -m "Assign presenters to shows by title; presenters see only their shows with exactly the 5 minute margins (#86, #90)"
```

### Task B9: Audit every access to a show

**Files:**
- Create: `Components/Pages/Audit.razor`, `Components/Pages/Audit.razor.cs`
- Modify: `Accounts/AuditLog.cs` (`AuditActions.All`), `Program.cs` (`/api/clip`, `/media/clip`), `Components/Pages/ShowPage.razor.cs`, `Components/Pages/Player.razor.cs`, `Components/Layout/MainLayout.razor`, `AuthorizationMatrixTests.cs` (add `/audit`)
- Test: `tests/WebAudioArchive.Tests/AuditTests.cs`

**Interfaces:**
- Produces: audit entries `show-viewed` (target show id, detail title), `clip-prepared` (target clip id, detail source and UTC window), `media-played` (once per person and clip per 10 minutes), `media-downloaded` (every time); page `/audit` (managers).

- [ ] **Step 1: Write the failing tests**

`tests/WebAudioArchive.Tests/AuditTests.cs`:
```csharp
extern alias web;

using System.Text.Json;
using System.Text.RegularExpressions;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Accounts;

namespace WebAudioArchive.Tests;

public sealed class AuditTests : IAsyncLifetime
{
    readonly ArchiveApp _app = new();
    AuditLog Audit => _app.Service<AuditLog>();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [Fact]
    public async Task A_show_visit_is_one_show_viewed_and_one_clip_prepared_entry_for_anyone()
    {
        var presenter = await _app.PresenterAsync("Backseat Aux");
        await presenter.GetStringAsync("/shows/burst-20261015T1900Z");
        await presenter.GetStringAsync("/shows/burst-20261015T1900Z"); // a reload within 10 minutes: the same visit

        var viewed = Audit.Query(null, AuditActions.ShowViewed, TestSignIn.PresenterEmail).Single();
        Assert.Equal("burst-20261015T1900Z", viewed.Target);
        Assert.Equal("Backseat Aux", viewed.Detail);
        Assert.Equal(ArchiveApp.Now, viewed.AtUtc);
        var prepared = Audit.Query(null, AuditActions.ClipPrepared, TestSignIn.PresenterEmail).Single();
        Assert.Matches("^[0-9a-f]{32}$", prepared.Target!);
        Assert.Contains("show burst-20261015T1900Z", prepared.Detail);

        var manager = await _app.ManagerAsync();
        await manager.GetStringAsync("/shows/burst-20261015T1900Z");
        Assert.Single(Audit.Query(null, AuditActions.ShowViewed, TestSignIn.ManagerEmail));
    }

    [Fact]
    public async Task A_refused_show_is_not_audited_as_viewed()
    {
        var presenter = await _app.PresenterAsync("Backseat Aux");
        await presenter.GetStringAsync("/shows/burst-20261015T2000Z");
        Assert.Empty(Audit.Query(null, AuditActions.ShowViewed, TestSignIn.PresenterEmail));
    }

    [Fact]
    public async Task Plays_are_collapsed_and_every_download_is_written()
    {
        var manager = await _app.ManagerAsync();
        var id = JsonDocument.Parse(await manager.GetStringAsync("/api/clip?from=2026-10-15T19:10:00Z&to=2026-10-15T19:11:00Z")).RootElement.GetProperty("id").GetString();
        for (var i = 0; i < 3; i++) await manager.GetAsync($"/media/clip/{id}.mp3");
        await manager.GetAsync($"/media/clip/{id}.mp3?download=1");
        await manager.GetAsync($"/media/clip/{id}.mp3?download=1");

        Assert.Single(Audit.Query(null, AuditActions.ClipPrepared, id));
        Assert.Contains("api 2026-10-15 19:10:00 UTC to 2026-10-15 19:11:00 UTC", Audit.Query(null, AuditActions.ClipPrepared, id)[0].Detail);
        Assert.Single(Audit.Query(null, AuditActions.MediaPlayed, id));
        Assert.Equal(2, Audit.Query(null, AuditActions.MediaDownloaded, id).Count);
    }

    [Fact]
    public async Task The_audit_page_lists_entries_newest_first_in_utc()
    {
        var manager = await _app.ManagerAsync();
        await manager.GetStringAsync("/shows/burst-20261015T1900Z");
        var html = await ArchiveApp.Page(manager, "/audit");
        var table = html[html.IndexOf("id=\"audit-table\"", StringComparison.Ordinal)..]; // after the filter's option list
        Assert.Contains("2026-10-15 21:30:00 UTC", table);
        Assert.True(table.IndexOf(">show-viewed<", StringComparison.Ordinal) < table.IndexOf(">sign-in<", StringComparison.Ordinal), "newest first");
    }
}
```
Add `"/audit"` to `AuthorizationMatrixTests.ManagerPages`.

- [ ] **Step 2: Run them and see them fail.**

- [ ] **Step 3: Implement**

`Accounts/AuditLog.cs`, in `AuditActions`, add:
```csharp
    public static readonly IReadOnlyList<string> All =
    [
        SignIn, SignInRefused, SignOut, ShowViewed, ClipPrepared, MediaPlayed, MediaDownloaded, UserAdded, UserRole, UserEnabled,
        UserRemoved, UserLoginsReset, SessionsEnded, UsersExported, UsersImported, AssignmentAdded, AssignmentRemoved,
    ];
```

`Program.cs`:
- `/api/clip`: give the handler `HttpContext http` and `AuditLog audit` (keep `HttpRequest req` or use `http.Request`), and after the clip is prepared and before returning it:
```csharp
        var viewer = Viewer.From(http.User)!; // the manager policy guarantees a manager
        audit.WriteOnce(AuditActions.ClipPrepared, viewer.Actor, clip.File.Id,
            $"api {ClipPlanner.Utc(clip.Plan.WindowFrom)} to {ClipPlanner.Utc(clip.Plan.WindowTo)}", ArchiveGate.RemoteIp(http));
```
- `/media/clip/{id}.mp3`: add `AuditLog audit` to the handler and, after the file exists check:
```csharp
    var ip = ArchiveGate.RemoteIp(http);
    if (download) audit.Write(AuditActions.MediaDownloaded, viewer.Actor, id, null, ip);
    else audit.WriteOnce(AuditActions.MediaPlayed, viewer.Actor, id, null, ip);
```
(Writing happens before the file is returned; if the database cannot be written the request fails rather than serving unaudited audio.)

`ShowPage.razor.cs`: inject `[Inject] public required AuditLog Audit { get; set; }`; after the access check, when `Show` is not null: `Audit.WriteOnce(AuditActions.ShowViewed, Me!.Actor, Show.Id, Show.Title);`; in `Accept(clip)`: `Audit.WriteOnce(AuditActions.ClipPrepared, Me!.Actor, clip.File.Id, $"show {Show!.Id} {ClipPlanner.Utc(clip.Plan.StartUtc)} to {ClipPlanner.Utc(clip.Plan.EndUtc)}");`.

`Player.razor.cs` (manager only): inject `[Inject] public required AuditLog Audit { get; set; }` and `[CascadingParameter] public Task<AuthenticationState>? AuthState { get; set; }` (with `using Microsoft.AspNetCore.Components.Authorization;` and `using WebAudioArchive.Web.Accounts;`), and right after the line that sets `Markers = ClipMarkers.ForPlayer(Clip);` add:
```csharp
            if (AuthState is not null && Viewer.From((await AuthState).User) is { } viewer)
                Audit.WriteOnce(AuditActions.ClipPrepared, viewer.Actor, Clip.File.Id, $"player {ClipPlanner.Utc(Clip.Plan.WindowFrom)} to {ClipPlanner.Utc(Clip.Plan.WindowTo)}");
```

`Components/Pages/Audit.razor`:
```razor
@page "/audit"
@attribute [Authorize(Policy = ArchivePolicies.Manager)]

<PageTitle>Audit log - WebAudioArchive</PageTitle>

<h1 class="h3">Audit log</h1>
<p>Every sign-in, refused sign-in and sign-out; every show viewed, clip made, play and download, by managers and presenters; every change to people and assignments. Times UTC.</p>

<div class="row g-2 align-items-end mb-3">
    <div class="col-auto">
        <label class="form-label small mb-1" for="audit-date">UTC date</label>
        <input class="form-control" id="audit-date" type="date" @bind="Date" @bind:after="Load" />
    </div>
    <div class="col-auto">
        <label class="form-label small mb-1" for="audit-action">Action</label>
        <select class="form-select" id="audit-action" @bind="Action" @bind:after="Load">
            <option value="">All</option>
            @foreach (var a in AuditActions.All)
            {
                <option value="@a">@a</option>
            }
        </select>
    </div>
    <div class="col-auto">
        <label class="form-label small mb-1" for="audit-contains">Email or target contains</label>
        <input class="form-control" id="audit-contains" @bind="Contains" @bind:after="Load" />
    </div>
</div>

<div class="table-responsive">
    <table class="table table-sm" id="audit-table">
        <thead><tr><th>Time (UTC)</th><th>Who</th><th>Action</th><th>Target</th><th>Detail</th><th>From</th></tr></thead>
        <tbody>
            @foreach (var e in Entries)
            {
                <tr>
                    <td class="utc text-nowrap">@ClipPlanner.Utc(e.AtUtc)</td>
                    <td>@e.Email</td>
                    <td>@e.Action</td>
                    <td><code>@e.Target</code></td>
                    <td>@e.Detail</td>
                    <td>@e.RemoteIp</td>
                </tr>
            }
        </tbody>
    </table>
</div>
<p class="small text-body-secondary">The newest @Entries.Count entries (at most 500). Entries are kept until a retention period is decided (spec 3.12).</p>
```

`Components/Pages/Audit.razor.cs`:
```csharp
using Microsoft.AspNetCore.Components;
using WebAudioArchive.Web.Accounts;

namespace WebAudioArchive.Web.Components.Pages;

public sealed partial class Audit
{
    [Inject] public required AuditLog Log { get; set; }

    DateOnly? Date { get; set; }
    string Action { get; set; } = "";
    string Contains { get; set; } = "";
    IReadOnlyList<AuditEntry> Entries { get; set; } = [];

    protected override void OnInitialized() => Load();

    void Load() => Entries = Log.Query(Date, Action, Contains);
}
```
Add an Audit link to the manager part of the nav in `MainLayout.razor` (`href="audit"`, icon `fas fa-clipboard-list`).

- [ ] **Step 4: Run all tests and commit**

```powershell
dotnet test -c Release
git add src/WebAudioArchive.Web tests/WebAudioArchive.Tests
git commit -m "Audit every show view, clip, play and download in UTC, with an audit page for managers (#87)"
```

### Task B10: People: the users page, export and import of the manager list

**Files:**
- Modify: `Accounts/UserStore.cs` (export and import), `Accounts/AccountsHosting.cs` (`/users/export.json`), `Components/Layout/MainLayout.razor`, `AuthorizationMatrixTests.cs` (add `/users`)
- Create: `Components/Pages/Users.razor`, `Components/Pages/Users.razor.cs`
- Test: `tests/WebAudioArchive.Tests/UserExportTests.cs`

**Interfaces:**
- Produces: `record UserExportEntry(string Email, string? DisplayName, string Role, bool Enabled)`; `record UserExport(string Format, int Version, DateTimeOffset ExportedUtc, string InstanceMode, IReadOnlyList<UserExportEntry> Users)` with `FormatName = "webaudioarchive-users"`, `Json` options; `record ImportPlan(IReadOnlyList<UserExportEntry> ToAdd, IReadOnlyList<string> Unchanged, IReadOnlyList<string> Conflicts, IReadOnlyList<string> Skipped)`; `UserStore.Export(bool includePresenters)`, `UserStore.ParseExport(string json)`, `UserStore.PlanImport(UserExport)`, `UserStore.ApplyImport(ImportPlan, Actor) : int`; `GET /users/export.json[?include=all]` (managers; `all` only on the primary).

- [ ] **Step 1: Write the failing tests**

`tests/WebAudioArchive.Tests/UserExportTests.cs`:
```csharp
extern alias web;

using System.Net;
using System.Text.Json;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Accounts;

namespace WebAudioArchive.Tests;

public sealed class UserExportTests : IDisposable
{
    readonly AccountsTestDb _t = new();
    UserStore Users => _t.Users;

    public UserExportTests()
    {
        Users.EnsureBootstrapManager("manager@example.test");
        Users.Add("second@example.test", "Second", Role.Manager, Actor.System);
        var p = Users.Add("presenter@example.test", "Presenter", Role.Presenter, Actor.System);
        Users.Bind(p.Id, "google", "sub-secret-ish", "presenter@example.test");
    }

    public void Dispose() => _t.Dispose();

    [Fact]
    public void The_export_has_managers_only_by_default_and_never_logins()
    {
        var export = Users.Export(includePresenters: false);
        Assert.Equal("webaudioarchive-users", export.Format);
        Assert.Equal(1, export.Version);
        Assert.Equal(new[] { "manager@example.test", "second@example.test" }, export.Users.Select(u => u.Email));
        var json = JsonSerializer.Serialize(export, UserExport.Json);
        Assert.DoesNotContain("sub-secret-ish", json);
        Assert.DoesNotContain("presenter@example.test", json);
        Assert.Contains("\"exportedUtc\": \"2026-10-16T09:00:00+00:00\"", json);
        Assert.Equal(3, Users.Export(includePresenters: true).Users.Count);
    }

    [Fact]
    public void Import_never_removes_or_downgrades()
    {
        using var other = new AccountsTestDb(InstanceMode.ManagerOnly);
        other.Users.EnsureBootstrapManager("manager@example.test");
        other.Users.Add("second@example.test", null, Role.Manager, Actor.System);
        other.Users.Add("only-here@example.test", null, Role.Manager, Actor.System);

        var file = UserStore.ParseExport("""
            { "format": "webaudioarchive-users", "version": 1, "exportedUtc": "2026-10-16T09:00:00Z", "instanceMode": "primary",
              "users": [
                { "email": "Manager@Example.test", "role": "manager", "enabled": true },
                { "email": "new@example.test", "displayName": "New", "role": "manager", "enabled": true },
                { "email": "new@example.test", "role": "manager", "enabled": true },
                { "email": "presenter@example.test", "role": "presenter", "enabled": true },
                { "email": "not an email", "role": "manager", "enabled": true },
                { "email": "x@example.test", "role": "admin", "enabled": true }
              ] }
            """);
        var plan = other.Users.PlanImport(file);

        Assert.Equal(new[] { "new@example.test" }, plan.ToAdd.Select(u => u.Email));
        Assert.Equal(new[] { "manager@example.test" }, plan.Unchanged);
        Assert.Contains(plan.Skipped, s => s.Contains("presenter@example.test") && s.Contains("manager-only"));
        Assert.Contains(plan.Skipped, s => s.Contains("not an email"));
        Assert.Contains(plan.Skipped, s => s.Contains("admin"));
        Assert.Equal(1, other.Users.ApplyImport(plan, Actor.System));
        Assert.NotNull(other.Users.FindByEmail("only-here@example.test")); // never removed
        Assert.Single(other.Audit.Query(null, AuditActions.UsersImported, null));
    }

    [Fact]
    public void A_role_difference_is_reported_not_applied()
    {
        var file = new UserExport(UserExport.FormatName, 1, AccountsTestDb.Start, "primary", [new("second@example.test", null, "presenter", true)]);
        var plan = Users.PlanImport(file);
        Assert.Contains(plan.Conflicts, c => c.Contains("second@example.test") && c.Contains("manager here"));
        Assert.Equal(0, Users.ApplyImport(plan, Actor.System));
        Assert.Equal(Role.Manager, Users.FindByEmail("second@example.test")!.Role);
    }

    [Theory]
    [InlineData("""{ "format": "something-else", "version": 1, "users": [] }""", "not a WebAudioArchive user list")]
    [InlineData("""{ "format": "webaudioarchive-users", "version": 2, "users": [] }""", "version 2")]
    [InlineData("not json", "not a WebAudioArchive user list")]
    public void Other_files_are_refused(string json, string message)
        => Assert.Contains(message, Assert.Throws<AccountException>(() => UserStore.ParseExport(json)).Message);

    [Fact]
    public async Task The_export_endpoint_is_for_managers_and_is_audited()
    {
        await using var app = new ArchiveApp();
        var manager = await app.ManagerAsync();
        using var response = await manager.GetAsync("/users/export.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.StartsWith("webaudioarchive-users-", response.Content.Headers.ContentDisposition!.FileName);
        Assert.Equal("webaudioarchive-users", JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("format").GetString());
        Assert.Single(app.Service<AuditLog>().Query(null, AuditActions.UsersExported, null));

        var presenter = await app.PresenterAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await presenter.GetAsync("/users/export.json")).StatusCode);
    }

    [Fact]
    public async Task The_users_page_lists_people_with_their_sign_ins()
    {
        await using var app = new ArchiveApp();
        var manager = await app.ManagerAsync();
        var html = await ArchiveApp.Page(manager, "/users");
        Assert.Contains("manager@example.test", html);
        Assert.Contains("data-login=\"google\"", html);
        Assert.Contains("id=\"user-add\"", html);
        Assert.Contains("href=\"users/export.json\"", html);
    }
}
```
Add `"/users"` to `AuthorizationMatrixTests.ManagerPages`.

- [ ] **Step 2: Run them and see them fail.**

- [ ] **Step 3: Implement export and import** (append to `Accounts/UserStore.cs`; add `using System.Text.Json;`)

```csharp
public sealed record UserExportEntry(string Email, string? DisplayName, string Role, bool Enabled);

/// <summary>The manager list as a JSON file, to copy by hand between instances (spec 3.8). It holds emails: personal data, never commit it.</summary>
public sealed record UserExport(string Format, int Version, DateTimeOffset ExportedUtc, string InstanceMode, IReadOnlyList<UserExportEntry> Users)
{
    public const string FormatName = "webaudioarchive-users";
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
}

/// <summary>What an import would do. It only ever adds; differences are reported for a manager to settle by hand.</summary>
public sealed record ImportPlan(IReadOnlyList<UserExportEntry> ToAdd, IReadOnlyList<string> Unchanged, IReadOnlyList<string> Conflicts, IReadOnlyList<string> Skipped);
```
and inside `UserStore`:
```csharp
    public UserExport Export(bool includePresenters) => new(
        UserExport.FormatName, 1, time.GetUtcNow(), auth.Mode == InstanceMode.Primary ? "primary" : "manager-only",
        [.. All().Where(u => includePresenters || u.Role == Role.Manager).Select(u => new UserExportEntry(u.Email, u.DisplayName, Roles.Name(u.Role), u.Enabled))]);

    public static UserExport ParseExport(string json)
    {
        UserExport? file;
        try
        {
            file = JsonSerializer.Deserialize<UserExport>(json, UserExport.Json);
        }
        catch (JsonException)
        {
            file = null;
        }

        if (file is null || file.Format != UserExport.FormatName || file.Users is null)
            throw new AccountException("This is not a WebAudioArchive user list (format webaudioarchive-users).");
        if (file.Version != 1)
            throw new AccountException($"This user list is version {file.Version}; this build reads version 1.");
        return file;
    }

    public ImportPlan PlanImport(UserExport file)
    {
        var add = new List<UserExportEntry>();
        var unchanged = new List<string>();
        var conflicts = new List<string>();
        var skipped = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in file.Users)
        {
            var email = Emails.Normalise(entry.Email);
            if (email is null) { skipped.Add($"'{entry.Email}': not an email address"); continue; }
            if (!seen.Add(email)) continue;
            if (Roles.Parse(entry.Role) is not { } role) { skipped.Add($"{email}: unknown role '{entry.Role}'"); continue; }
            if (role == Role.Presenter && auth.Mode == InstanceMode.ManagerOnly) { skipped.Add($"{email}: presenters are not kept on a manager-only instance"); continue; }
            var existing = FindByEmail(email);
            if (existing is null) add.Add(entry with { Email = email });
            else if (existing.Role == role) unchanged.Add(email);
            else conflicts.Add($"{email}: {Roles.Name(existing.Role)} here, {Roles.Name(role)} in the file; not changed");
        }

        return new ImportPlan(add, unchanged, conflicts, skipped);
    }

    public int ApplyImport(ImportPlan plan, Actor actor)
    {
        var added = 0;
        foreach (var entry in plan.ToAdd)
        {
            if (FindByEmail(entry.Email) is not null) continue;
            var u = Add(entry.Email, entry.DisplayName, Roles.Parse(entry.Role)!.Value, actor);
            if (!entry.Enabled) SetEnabled(u.Id, false, actor);
            added++;
        }

        audit.Write(AuditActions.UsersImported, actor, null, $"added={added} unchanged={plan.Unchanged.Count} conflicts={plan.Conflicts.Count} skipped={plan.Skipped.Count}");
        return added;
    }
```

`AccountsHosting.MapArchiveAccounts`, add (with `using System.Globalization; using System.Text.Json;`):
```csharp
        app.MapGet("/users/export.json", (HttpContext http, UserStore users, AuthSettings auth, AuditLog audit, TimeProvider time) =>
        {
            var all = http.Request.Query["include"] == "all" && auth.Mode == InstanceMode.Primary;
            var file = users.Export(includePresenters: all);
            var viewer = Viewer.From(http.User)!;
            audit.Write(AuditActions.UsersExported, viewer.Actor, null, $"users={file.Users.Count} presenters={(all ? "yes" : "no")}", ArchiveGate.RemoteIp(http));
            var name = "webaudioarchive-users-" + time.GetUtcNow().UtcDateTime.ToString("yyyyMMdd'T'HHmm'Z'", CultureInfo.InvariantCulture) + ".json";
            return Results.File(JsonSerializer.SerializeToUtf8Bytes(file, UserExport.Json), "application/json", name);
        }).RequireAuthorization(ArchivePolicies.Manager);
```

- [ ] **Step 4: The users page**

`Components/Pages/Users.razor`:
```razor
@page "/users"
@attribute [Authorize(Policy = ArchivePolicies.Manager)]

<PageTitle>People - WebAudioArchive</PageTitle>

<h1 class="h3">People</h1>
<p>Only the people on this list can sign in, with a Google or GitHub account whose verified email is the one given here. The first sign-in binds that account; "Reset sign-in" lets a changed account bind again. This instance is <strong>@(Auth.Mode == InstanceMode.Primary ? "primary" : "manager-only")</strong>.</p>

@if (Error is not null)
{
    <div class="alert alert-danger" id="user-error" role="alert">@Error</div>
}
@if (Notice is not null)
{
    <div class="alert alert-success" id="user-notice" role="status">@Notice</div>
}

<div class="row g-2 align-items-end mb-3">
    <div class="col-12 col-md-4">
        <label class="form-label small mb-1" for="user-email">Email (Google or GitHub)</label>
        <input class="form-control" id="user-email" type="email" @bind="NewEmail" />
    </div>
    <div class="col-12 col-md-3">
        <label class="form-label small mb-1" for="user-name">Name (optional)</label>
        <input class="form-control" id="user-name" @bind="NewName" />
    </div>
    <div class="col-6 col-md-2">
        <label class="form-label small mb-1" for="user-role">Role</label>
        <select class="form-select" id="user-role" @bind="NewRole">
            <option value="@Role.Manager">Manager</option>
            @if (Auth.Mode == InstanceMode.Primary)
            {
                <option value="@Role.Presenter">Presenter</option>
            }
        </select>
    </div>
    <div class="col-6 col-md-3">
        <button type="button" class="btn btn-primary w-100" id="user-add" @onclick="Add">Add</button>
    </div>
</div>

<div class="table-responsive">
    <table class="table table-sm align-middle" id="users-table">
        <thead><tr><th>Email</th><th>Name</th><th>Role</th><th>Sign-in</th><th>Last sign-in (UTC)</th><th></th></tr></thead>
        <tbody>
            @foreach (var u in People)
            {
                <tr data-user="@u.Email" class="@(u.Enabled ? "" : "text-body-secondary")">
                    <td>@u.Email @(u.Enabled ? "" : "(disabled)")</td>
                    <td>@u.DisplayName</td>
                    <td>@Roles.Name(u.Role)</td>
                    <td>
                        @foreach (var l in u.Logins)
                        {
                            <span class="badge text-bg-light me-1" data-login="@l.Provider" title="Bound @ClipPlanner.Utc(l.BoundUtc) with @l.EmailAtBind">@l.Provider</span>
                        }
                    </td>
                    <td class="utc">@(u.LastSignInUtc is { } t ? ClipPlanner.Utc(t) : "never")</td>
                    <td class="text-nowrap">
                        @if (u.Id != Me?.UserId)
                        {
                            @if (Auth.Mode == InstanceMode.Primary)
                            {
                                <button type="button" class="btn btn-sm btn-outline-secondary me-1" @onclick="() => Do(() => Store.SetRole(u.Id, u.Role == Role.Manager ? Role.Presenter : Role.Manager, Me!.Actor))">Make @(u.Role == Role.Manager ? "presenter" : "manager")</button>
                            }
                            <button type="button" class="btn btn-sm btn-outline-secondary me-1" @onclick="() => Do(() => Store.SetEnabled(u.Id, !u.Enabled, Me!.Actor))">@(u.Enabled ? "Disable" : "Enable")</button>
                            <button type="button" class="btn btn-sm btn-outline-secondary me-1" @onclick="() => Do(() => Store.ResetLogins(u.Id, Me!.Actor))">Reset sign-in</button>
                            <button type="button" class="btn btn-sm btn-outline-secondary me-1" @onclick="() => Do(() => Store.EndSessions(u.Id, Me!.Actor))">Sign out everywhere</button>
                            @if (ConfirmRemove == u.Id)
                            {
                                <button type="button" class="btn btn-sm btn-danger" @onclick="() => Do(() => Store.Remove(u.Id, Me!.Actor))">Confirm remove</button>
                            }
                            else
                            {
                                <button type="button" class="btn btn-sm btn-outline-danger" @onclick="() => ConfirmRemove = u.Id">Remove</button>
                            }
                        }
                    </td>
                </tr>
            }
        </tbody>
    </table>
</div>

<h2 class="h5 mt-4">Copy the manager list to the other instance</h2>
<p>
    <a class="btn btn-sm btn-outline-primary" href="users/export.json" data-enhance-nav="false" id="users-export">Export managers (JSON)</a>
    @if (Auth.Mode == InstanceMode.Primary)
    {
        <a class="btn btn-sm btn-outline-primary ms-1" href="users/export.json?include=all" data-enhance-nav="false">Export everyone</a>
    }
    <span class="small text-body-secondary ms-2">The file holds email addresses: keep it private, never commit it, delete it after importing.</span>
</p>
<div class="mb-2">
    <label class="form-label small mb-1" for="users-import">Import a list exported from the other instance (adds only, never removes or changes anyone)</label>
    <InputFile id="users-import" class="form-control" accept=".json,application/json" OnChange="ReadImport" />
</div>
@if (Plan is not null)
{
    <div class="card mb-3" id="import-plan">
        <div class="card-body small">
            <p class="mb-1">Will add: @(Plan.ToAdd.Count == 0 ? "nobody" : string.Join(", ", Plan.ToAdd.Select(u => $"{u.Email} ({u.Role})")))</p>
            <p class="mb-1">Already here: @Plan.Unchanged.Count</p>
            @foreach (var c in Plan.Conflicts) { <p class="mb-1 text-warning-emphasis">@c</p> }
            @foreach (var s in Plan.Skipped) { <p class="mb-1 text-body-secondary">Skipped @s</p> }
            <button type="button" class="btn btn-sm btn-primary mt-2" id="import-apply" disabled="@(Plan.ToAdd.Count == 0)" @onclick="ApplyImport">Add @Plan.ToAdd.Count</button>
        </div>
    </div>
}
```

`Components/Pages/Users.razor.cs`:
```csharp
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Forms;
using WebAudioArchive.Web.Accounts;

namespace WebAudioArchive.Web.Components.Pages;

public sealed partial class Users
{
    const long MaxImportBytes = 64 * 1024;

    [Inject] public required UserStore Store { get; set; }
    [Inject] public required AuthSettings Auth { get; set; }
    [CascadingParameter] public Task<AuthenticationState>? AuthState { get; set; }

    IReadOnlyList<UserRecord> People { get; set; } = [];
    Viewer? Me { get; set; }
    string NewEmail { get; set; } = "";
    string NewName { get; set; } = "";
    Role NewRole { get; set; } = Role.Manager;
    long? ConfirmRemove { get; set; }
    ImportPlan? Plan { get; set; }
    string? Error { get; set; }
    string? Notice { get; set; }

    protected override async Task OnInitializedAsync()
    {
        Me = AuthState is null ? null : Viewer.From((await AuthState).User);
        People = Store.All();
    }

    void Add() => Do(() =>
    {
        Store.Add(NewEmail, NewName, NewRole, Me!.Actor);
        Notice = $"{NewEmail.Trim()} can now sign in.";
        NewEmail = NewName = "";
    });

    void Do(Action change)
    {
        Error = Notice = null;
        try
        {
            change();
        }
        catch (AccountException ex)
        {
            Error = ex.Message;
        }

        ConfirmRemove = null;
        People = Store.All();
    }

    async Task ReadImport(InputFileChangeEventArgs e)
    {
        Error = Notice = null;
        Plan = null;
        try
        {
            await using var stream = e.File.OpenReadStream(MaxImportBytes);
            using var reader = new StreamReader(stream);
            Plan = Store.PlanImport(UserStore.ParseExport(await reader.ReadToEndAsync()));
        }
        catch (AccountException ex)
        {
            Error = ex.Message;
        }
        catch (IOException)
        {
            Error = $"The file is larger than {MaxImportBytes / 1024} KB; a user list is much smaller.";
        }
    }

    void ApplyImport() => Do(() =>
    {
        var added = Store.ApplyImport(Plan!, Me!.Actor);
        Notice = $"Added {added}.";
        Plan = null;
    });
}
```
Add a People link to the manager part of the nav in `MainLayout.razor` (`href="users"`, icon `fas fa-users`).

- [ ] **Step 5: Run all tests and commit**

```powershell
dotnet test -c Release
git add src/WebAudioArchive.Web tests/WebAudioArchive.Tests
git commit -m "People page: add, role, disable, reset sign-in, sign out everywhere; export and import of the manager list (#91, #1)"
```

### Task B11: Hardening: headers, CSP, Origin check, rate limits, clip build cap, forwarded headers, protected keys

**Files:**
- Create: `src/WebAudioArchive.Web/Accounts/SecurityHeaders.cs`, `src/WebAudioArchive.Web/Clips/ClipBuildGate.cs`, `src/WebAudioArchive.Web/wwwroot/js/theme-boot.js`
- Modify: `Accounts/AccountsHosting.cs`, `Clips/ClipService.cs`, `Components/App.razor`, `Program.cs` (rate-limit policies on the two endpoints), `tests/WebAudioArchive.Tests/WebAppTests.cs` (the theme test reads the script file)
- Test: `tests/WebAudioArchive.Tests/HardeningTests.cs`

**Interfaces:**
- Produces: `SecurityHeaders.ContentSecurityPolicy(AuthSettings)`, `UseSecurityHeaders`, `UseSameOriginGuard`; `ArchiveRateLimits.Clip`, `ArchiveRateLimits.Media`; `ClipBuildGate(int concurrent = 2)` with `Enter(TimeSpan wait) : IDisposable` (throws `ClipException(503, ...)`).

- [ ] **Step 1: Write the failing tests**

`tests/WebAudioArchive.Tests/HardeningTests.cs`:
```csharp
extern alias web;

using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using WebAudioArchive.Tests.Support;
using web::WebAudioArchive.Web.Accounts;
using web::WebAudioArchive.Web.Clips;

namespace WebAudioArchive.Tests;

public sealed partial class HardeningTests : IAsyncLifetime
{
    readonly ArchiveApp _app = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _app.DisposeAsync();

    [GeneratedRegex("<script(?![^>]*\\bsrc=)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex InlineScript();

    [Fact]
    public async Task Security_headers_on_pages_api_and_media()
    {
        var manager = await _app.ManagerAsync();
        var json = JsonDocument.Parse(await manager.GetStringAsync("/api/clip?from=2026-10-15T19:10:00Z&to=2026-10-15T19:11:00Z")).RootElement;
        foreach (var url in new[] { "/", "/signin", "/api/clip?from=2026-10-15T19:10:00Z&to=2026-10-15T19:11:00Z", json.GetProperty("url").GetString()! })
        {
            using var response = await manager.GetAsync(url);
            var h = response.Headers;
            var csp = Assert.Single(h.GetValues("Content-Security-Policy"));
            Assert.Equal("default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; media-src 'self'; connect-src 'self' wss://localhost; object-src 'none'; base-uri 'self'; frame-ancestors 'none'; form-action 'self' https://accounts.google.com https://github.com", csp);
            Assert.Equal("nosniff", Assert.Single(h.GetValues("X-Content-Type-Options")));
            Assert.Equal("DENY", Assert.Single(h.GetValues("X-Frame-Options")));
            Assert.Equal("no-referrer", Assert.Single(h.GetValues("Referrer-Policy")));
            Assert.Equal("same-origin", Assert.Single(h.GetValues("Cross-Origin-Opener-Policy")));
            Assert.Equal("max-age=31536000", Assert.Single(h.GetValues("Strict-Transport-Security")));
            Assert.True(h.CacheControl?.NoStore, $"{url}: Cache-Control no-store");
        }
    }

    [Fact]
    public async Task Pages_have_no_inline_scripts()
    {
        var manager = await _app.ManagerAsync();
        foreach (var url in new[] { "/", "/shows", "/recordings", "/player" })
            Assert.DoesNotMatch(InlineScript(), await manager.GetStringAsync(url));
        Assert.DoesNotMatch(InlineScript(), await _app.Anonymous().GetStringAsync("/signin"));
        Assert.Contains("<script id=\"theme-boot\" src=\"js/theme-boot.js\"></script>", await _app.Anonymous().GetStringAsync("/signin"));
        Assert.Equal(HttpStatusCode.OK, (await _app.Anonymous().GetAsync("/js/theme-boot.js")).StatusCode);
    }

    [Fact]
    public async Task Cookies_are_hardened()
    {
        using var page = await _app.Anonymous().GetAsync("/signin");
        var antiforgery = Assert.Single(page.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-waa-af=", StringComparison.Ordinal));
        Assert.Contains("samesite=strict", antiforgery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", antiforgery, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", antiforgery, StringComparison.OrdinalIgnoreCase);

        var client = _app.Anonymous();
        using var back = await TestSignIn.StartAndCompleteAsync(client, _app.Idp, TestSignIn.Manager);
        var session = Assert.Single(back.Headers.GetValues("Set-Cookie"), c => c.StartsWith("__Host-authmagic=", StringComparison.Ordinal));
        foreach (var flag in new[] { "path=/", "secure", "samesite=lax", "httponly" })
            Assert.Contains(flag, session, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Cross_origin_posts_and_hub_connections_are_refused()
    {
        var manager = await _app.ManagerAsync();
        var page = await manager.GetStringAsync("/signout");
        using var post = new HttpRequestMessage(HttpMethod.Post, "/auth/signout") { Content = new FormUrlEncodedContent([new("__RequestVerificationToken", TestSignIn.Antiforgery(page))]) };
        post.Headers.Add("Origin", "https://evil.example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.SendAsync(post)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await manager.GetAsync("/recordings")).StatusCode); // still signed in

        using var hub = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        hub.Headers.Add("Origin", "https://evil.example.test");
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.SendAsync(hub)).StatusCode);

        using var sameOrigin = new HttpRequestMessage(HttpMethod.Post, "/_blazor/negotiate?negotiateVersion=1");
        sameOrigin.Headers.Add("Origin", "https://localhost");
        Assert.Equal(HttpStatusCode.OK, (await manager.SendAsync(sameOrigin)).StatusCode);
    }

    [Fact]
    public async Task Sign_in_is_rate_limited()
    {
        var client = _app.Anonymous();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 25; i++)
            codes.Add((await client.PostAsync("/auth/signin/google", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Equal(20, codes.Count(c => c == HttpStatusCode.BadRequest)); // no antiforgery token: refused, but counted
        Assert.Equal(5, codes.Count(c => c == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public async Task The_clip_api_is_rate_limited()
    {
        var manager = await _app.ManagerAsync();
        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 32; i++)
            codes.Add((await manager.GetAsync("/api/clip?from=2026-10-15T19:10:00Z&to=2026-10-15T19:11:00Z")).StatusCode);
        Assert.Equal(30, codes.Count(c => c == HttpStatusCode.OK));
        Assert.Equal(2, codes.Count(c => c == HttpStatusCode.TooManyRequests));
    }

    [Fact]
    public void Clip_builds_are_limited_to_two_at_once()
    {
        using var gate = new ClipBuildGate(2);
        using var a = gate.Enter(TimeSpan.Zero);
        using (gate.Enter(TimeSpan.Zero))
        {
            Assert.Equal(503, Assert.Throws<ClipException>(() => gate.Enter(TimeSpan.Zero)).StatusCode);
        }

        using var c = gate.Enter(TimeSpan.Zero); // a slot came back
    }

    [Fact]
    public async Task Canonical_origin_redirects_other_hosts()
    {
        var other = _app.Factory.CreateDefaultClient(new Uri("http://127.0.0.1"));
        using var page = await other.GetAsync("/shows?view=month");
        Assert.Equal(HttpStatusCode.TemporaryRedirect, page.StatusCode);
        Assert.Equal("https://localhost/shows?view=month", page.Headers.Location!.AbsoluteUri);
        Assert.Equal(HttpStatusCode.OK, (await other.GetAsync("/healthz")).StatusCode);
    }

    [Fact]
    public async Task Forwarded_headers_are_ignored_without_trusted_proxies()
    {
        var client = _app.Anonymous();
        client.DefaultRequestHeaders.Add("X-Forwarded-For", "203.0.113.9");
        client.DefaultRequestHeaders.Add("X-Forwarded-Host", "evil.example.test");
        using var back = await TestSignIn.StartAndCompleteAsync(client, _app.Idp, TestSignIn.Manager);
        Assert.Equal("/", back.Headers.Location!.OriginalString);
        Assert.DoesNotContain(_app.Service<AuditLog>().Query(null, AuditActions.SignIn, null), e => e.RemoteIp == "203.0.113.9");
    }

    [Fact]
    public async Task Keys_are_kept_in_the_app_directory()
    {
        await _app.ManagerAsync();
        var keys = Directory.GetFiles(Path.Combine(_app.AppDir, "keys"), "key-*.xml");
        Assert.NotEmpty(keys);
        Assert.Contains("<masterKey", File.ReadAllText(keys[0])); // not encrypted: no certificate configured
    }

    [Fact]
    public async Task Keys_are_encrypted_with_a_configured_certificate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "waa-dpcert-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=WebAudioArchive data protection (test)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        File.WriteAllBytes(Path.Combine(dir, "dp.pfx"), cert.Export(X509ContentType.Pfx, "test-password"));
        File.WriteAllText(Path.Combine(dir, "dp.password"), "test-password");

        await using var app = new ArchiveApp(configure: b =>
        {
            b.UseSetting("WAA_DP_CERT_FILE", Path.Combine(dir, "dp.pfx"));
            b.UseSetting("WAA_DP_CERT_PASSWORD_FILE", Path.Combine(dir, "dp.password"));
        });
        await app.ManagerAsync();
        var key = File.ReadAllText(Directory.GetFiles(Path.Combine(app.AppDir, "keys"), "key-*.xml")[0]);
        Assert.Contains("EncryptedXml", key);
        Assert.DoesNotContain("<masterKey", key);
        Directory.Delete(dir, recursive: true);
    }
}
```

- [ ] **Step 2: Run them and see them fail.**

- [ ] **Step 3: Move the theme script out of the page**

Create `src/WebAudioArchive.Web/wwwroot/js/theme-boot.js` with exactly the body of the current `<script id="theme-boot">` element in `Components/App.razor` (the whole `(function () { ... })();`, unchanged), and replace that element in `App.razor` with:
```html
    <script id="theme-boot" src="js/theme-boot.js"></script>
```
It stays in `<head>` before the stylesheets, so it still runs before first paint. If other agents have added further inline `<script>` blocks to `App.razor` or any page by the time of this task, move each to its own file under `wwwroot/js` the same way.

Update `WebAppTests.Every_page_sets_the_theme_before_first_paint_and_has_the_theme_toggle`: it still finds `id="theme-boot"` in `<head>` before the first stylesheet; replace its three checks on `head` (`prefers-color-scheme: dark`, `data-bs-theme`, `localStorage`) with the same checks on the script file:
```csharp
        var boot = await _client.GetStringAsync("/js/theme-boot.js"); // an external script, so the CSP needs no inline scripts
        Assert.Contains("prefers-color-scheme: dark", boot);
        Assert.Contains("data-bs-theme", boot);
        Assert.Contains("localStorage", boot);
```

- [ ] **Step 4: Headers and the Origin check**

`src/WebAudioArchive.Web/Accounts/SecurityHeaders.cs`:
```csharp
using Microsoft.Net.Http.Headers;

namespace WebAudioArchive.Web.Accounts;

public static class SecurityHeaders
{
    /// <summary>
    /// Scripts, fonts, media and connections from this origin only; styles also inline (Blazor and the component
    /// library set style attributes); no framing; forms may post to this site and continue to Google or GitHub.
    /// </summary>
    public static string ContentSecurityPolicy(AuthSettings auth)
    {
        var socket = (auth.IsHttps ? "wss://" : "ws://") + auth.PublicUrl.Authority;
        return string.Join("; ",
            "default-src 'self'",
            "script-src 'self'",
            "style-src 'self' 'unsafe-inline'",
            "img-src 'self' data:",
            "font-src 'self'",
            "media-src 'self'",
            $"connect-src 'self' {socket}",
            "object-src 'none'",
            "base-uri 'self'",
            "frame-ancestors 'none'",
            "form-action 'self' https://accounts.google.com https://github.com");
    }

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, AuthSettings auth)
    {
        var csp = ContentSecurityPolicy(auth);
        return app.Use(async (context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var h = context.Response.Headers;
                h.ContentSecurityPolicy = csp;
                h.XContentTypeOptions = "nosniff";
                h.XFrameOptions = "DENY";
                h["Referrer-Policy"] = "no-referrer"; // clip ids and show ids never leave in a Referer
                h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
                h["Cross-Origin-Opener-Policy"] = "same-origin";
                h["Cross-Origin-Resource-Policy"] = "same-origin";
                if (auth.IsHttps) h.StrictTransportSecurity = "max-age=31536000";
                // Pages, the API and audio are private: never kept by the browser or a proxy. Static assets set their own.
                if (!h.ContainsKey(HeaderNames.CacheControl)) h.CacheControl = "no-store";
                return Task.CompletedTask;
            });
            await next(context);
        });
    }

    /// <summary>A cross-origin POST or connection to the Blazor hub is refused, whatever cookies it carries.</summary>
    public static IApplicationBuilder UseSameOriginGuard(this IApplicationBuilder app, AuthSettings auth) => app.Use(async (context, next) =>
    {
        var request = context.Request;
        var safe = HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) || HttpMethods.IsOptions(request.Method);
        if ((!safe || request.Path.StartsWithSegments("/_blazor"))
            && request.Headers.Origin.Count > 0
            && !string.Equals(request.Headers.Origin.ToString(), auth.Origin, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        await next(context);
    });
}
```

- [ ] **Step 5: Rate limits, forwarded headers and protected keys** (`Accounts/AccountsHosting.cs`; add `using System.Security.Cryptography.X509Certificates; using System.Threading.RateLimiting; using Microsoft.AspNetCore.HttpOverrides; using WebAudioArchive.Web.Clips;`)

Add the policy names:
```csharp
public static class ArchiveRateLimits
{
    public const string Clip = "clip";
    public const string Media = "media";
}
```
In `AddArchiveAccounts` replace the data protection statement with:
```csharp
        var protection = s.AddDataProtection()
            .SetApplicationName("WebAudioArchive.Web")
            .PersistKeysToFileSystem(new DirectoryInfo(auth.KeysDir));
        if (auth.DataProtectionCertFile is { } certFile)
        {
            // Keys at rest encrypted with this certificate; without one they rely on the folder being private (mode 700).
            var cert = X509CertificateLoader.LoadPkcs12FromFile(certFile, auth.DataProtectionCertPassword);
            protection.ProtectKeysWithCertificate(cert).UnprotectKeysWithAnyCertificate(cert);
        }
```
and add:
```csharp
        s.AddSingleton(new ClipBuildGate(2));
        s.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // Sign-in starts and the provider callbacks, per client: they run before authentication, so they are partitioned by address.
            o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => IsSignInPath(context.Request.Path)
                ? RateLimitPartition.GetFixedWindowLimiter("signin|" + Client(context), _ => Window(20))
                : RateLimitPartition.GetNoLimiter("none"));
            o.AddPolicy(ArchiveRateLimits.Clip, context => RateLimitPartition.GetFixedWindowLimiter("clip|" + Client(context), _ => Window(30)));
            o.AddPolicy(ArchiveRateLimits.Media, context => RateLimitPartition.GetFixedWindowLimiter("media|" + Client(context), _ => Window(1200)));
        });
```
with these helpers in `AccountsHosting`:
```csharp
    static FixedWindowRateLimiterOptions Window(int perMinute) => new() { PermitLimit = perMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 };

    static bool IsSignInPath(PathString path) =>
        path.StartsWithSegments("/auth") || path.StartsWithSegments(AuthMagicServerDefaults.GoogleCallbackPath) || path.StartsWithSegments(AuthMagicServerDefaults.GitHubCallbackPath);

    static string Client(HttpContext context) => ArchiveGate.RemoteIp(context) ?? "unknown";
```
Replace `UseArchiveSecurity` with:
```csharp
    public static void UseArchiveSecurity(this WebApplication app, AuthSettings auth)
    {
        if (auth.TrustedProxies.Count > 0)
        {
            // Only the named proxies are believed about the client address, scheme and host (primary behind its ingress).
            var forwarded = new ForwardedHeadersOptions
            {
                ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost,
                ForwardLimit = 1,
                AllowedHosts = [auth.PublicUrl.Host],
            };
            forwarded.KnownIPNetworks.Clear();
            forwarded.KnownProxies.Clear();
            foreach (var network in auth.TrustedProxies) forwarded.KnownIPNetworks.Add(network);
            app.UseForwardedHeaders(forwarded);
        }

        app.UseAuthMagicCanonicalOrigin("/healthz");
        app.UseSecurityHeaders(auth);
        app.UseRouting();
        app.UseRateLimiter();
        app.UseSameOriginGuard(auth);
        app.UseAuthentication();
        app.UseAuthorization();
        app.UseAntiforgery();
    }
```
(`KnownIPNetworks` takes `System.Net.IPNetwork` in .NET 10; the older `KnownNetworks` is obsolete there and would fail the build with warnings as errors.)

In `Program.cs` append `.RequireRateLimiting(ArchiveRateLimits.Clip)` to `/api/clip` and `.RequireRateLimiting(ArchiveRateLimits.Media)` to `/media/clip/{id}.mp3`.

- [ ] **Step 6: At most two clip builds at once**

`src/WebAudioArchive.Web/Clips/ClipBuildGate.cs`:
```csharp
namespace WebAudioArchive.Web.Clips;

/// <summary>Bounds concurrent clip builds (disk and CPU): a build waits up to <see cref="Wait"/> for a slot, then gets 503.</summary>
public sealed class ClipBuildGate(int concurrent = 2) : IDisposable
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);
    readonly SemaphoreSlim _slots = new(concurrent, concurrent);

    public IDisposable Enter(TimeSpan wait)
    {
        if (!_slots.Wait(wait)) throw new ClipException(503, "The server is already making other clips. Try again in a minute.");
        return new Slot(_slots);
    }

    public void Dispose() => _slots.Dispose();

    sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) slots.Release();
        }
    }
}
```
`Clips/ClipService.cs`: add a trailing optional constructor parameter `ClipBuildGate? gate = null` to the primary constructor (existing callers in tests keep working), and in `Prepare` wrap the build:
```csharp
        using (gate?.Enter(ClipBuildGate.Wait))
        {
            var file = _builder.Build(settings.Station, plan);
            return new PreparedClip(settings.Station, file, plan);
        }
```
(Keep whatever other changes the other agents made to this file, such as `TryPrepareCached`, which builds nothing and needs no slot.)

- [ ] **Step 7: Run all tests and commit**

```powershell
dotnet test -c Release
git add src/WebAudioArchive.Web tests/WebAudioArchive.Tests
git commit -m "Hardening: CSP without inline scripts, security headers, Origin check, rate limits, two clip builds at once, trusted proxies, protected keys (#53, #54, #55)"
```

### Task B12: Docker, docs and spec corrections

**Files:**
- Modify: `docker/docker-compose.yml` (the `web` service only), `docs/operations.md`, `README.md`, `docs/superpowers/specs/2026-10-15-webaudioarchive-design.md`

- [ ] **Step 1: The web service in compose** (the `recorder-a` and `recorder-b` services and the `x-recorder` anchor are not changed by a single character)

Replace the `web:` service with:
```yaml
  # The web app: Status, Shows, Recordings, Player, People, Assignments, Audit and the clip API, at http://localhost:8090.
  # Sign-in with Google or GitHub (docs/operations.md, Sign-in). Still published on loopback only. It reads the
  # recorders' data read-only, writes only its own folder data/web (database, keys, backups) and its clip cache, and
  # talks to the recorders only through /healthz. Rebuild it on its own: `docker compose -f docker/docker-compose.yml up -d --build web`.
  web:
    build:
      context: ..
      dockerfile: docker/Dockerfile.web
    image: webaudioarchive-web:local
    container_name: waa-web
    restart: always
    environment:
      WAA_STATION: burst
      WAA_RECORDERS: a=/data/a,b=/data/b
      WAA_RECORDER_HEALTH: a=http://recorder-a:8080/healthz,b=http://recorder-b:8080/healthz
      WAA_CACHE: /cache
      # The Shows tab reads the station's scheduleUrl and timezone from here (read-only).
      WAA_STATIONS_CONFIG: /config/stations.json
      # Sign-in. Values come from docker/.env (ignored by git); secrets are files outside the repository, mounted read-only.
      WAA_PUBLIC_URL: ${WAA_PUBLIC_URL:-http://localhost:8090}
      INSTANCE_MODE: ${INSTANCE_MODE:-manager-only}
      BOOTSTRAP_MANAGER_EMAIL: ${BOOTSTRAP_MANAGER_EMAIL:-}
      WAA_GOOGLE_CLIENT_ID: ${WAA_GOOGLE_CLIENT_ID:-}
      WAA_GOOGLE_CLIENT_SECRET_FILE: /run/secrets/waa/google_client_secret.txt
      WAA_GITHUB_CLIENT_ID: ${WAA_GITHUB_CLIENT_ID:-}
      WAA_GITHUB_CLIENT_SECRET_FILE: /run/secrets/waa/github_client_secret.txt
      WAA_APP_DIR: /data/web
    volumes:
      - ${WAA_DATA_DIR:-../data}:/data:ro
      # The only writable part of /data for the web app. The recorders mount data/a and data/b, never this.
      - ${WAA_DATA_DIR:-../data}/web:/data/web
      - ${WAA_SECRETS_DIR:-./secrets}:/run/secrets/waa:ro
      - ../config:/config:ro
      - web-cache:/cache
    ports:
      - "127.0.0.1:8090:8080"
```
No `${VAR:?}` (required) interpolation is used anywhere: compose interpolates the whole file for every command, so a missing sign-in value must never stop `docker compose ... up -d recorder-a`. A missing value makes only the web container fail, with a `config error:` line naming the setting.

Check the file still parses and the recorders are unchanged:
```powershell
docker compose -f docker/docker-compose.yml config --services
git diff --stat docker/docker-compose.yml
git diff docker/docker-compose.yml | Select-String '^[+-]' | Select-String 'recorder-' 
```
Expected: `recorder-a`, `recorder-b`, `web`; the diff touches only lines of the `web` service (the last command prints only the unchanged `WAA_RECORDER_HEALTH` line if at all).

- [ ] **Step 2: operations.md**

Replace the paragraph that starts with `**No sign-in.**` in "Web app (MVP)" with the new section below, rename the heading "Web app (MVP)" to "Web app", and add People, Assignments and Audit to the page list. New section:

````markdown
## Sign-in

Everything is behind a sign-in with **Google or GitHub**. Only people a manager has added, by the email address of
their Google or GitHub account, can sign in, and only when that provider says the address is verified. On the first
sign-in the provider account is bound to the person; later sign-ins match that account even if its email changes, and a
different account offering the same email is refused until a manager uses **Reset sign-in** on the People page.

- **Roles:** managers see everything and manage people; presenters see only the shows assigned to them (Assignments
  page, by show title, every occurrence or between two UTC dates), each with exactly 5 minutes either side, and can play
  and download them. Presenters do not see Status, Recordings, Player or any other show, not even its title; another
  show's address or clip id answers as if it did not exist.
- **First manager:** `BOOTSTRAP_MANAGER_EMAIL` is made, or restored as, an enabled manager at every start. Never the
  first person to sign in.
- **Instance mode:** `INSTANCE_MODE=manager-only` (the default, for the manager's own machine) refuses presenters and
  keeps no assignments; `primary` (the cluster) serves presenters.
- **Sessions:** a cookie (`authmagic` on http://localhost, `__Host-authmagic` on https) holding only a random session id;
  HttpOnly, SameSite=Lax, Secure on https. A session ends after 4 hours without activity (`WAA_SESSION_IDLE_MINUTES`),
  24 hours after sign-in at the latest (`WAA_SESSION_MAX_HOURS`), at sign-out, or when a manager disables the person,
  changes their role or uses **Sign out everywhere**. Signing out does not sign you out of Google or GitHub.
- **Audit:** every sign-in, refused sign-in and sign-out, every show viewed, clip made, play and download (by anyone),
  and every change to people and assignments, in UTC with the client address: the **Audit** page.
- **Export and import:** People page, "Export managers (JSON)", then "Import" on the other instance. Import only adds;
  it never removes or changes anyone. The file holds email addresses: keep it out of the repository and delete it after use.

Settings (all in `docker/.env`, which git ignores, except the secrets):

| Name | Default | Meaning |
|---|---|---|
| `WAA_PUBLIC_URL` | `http://localhost:8090` (compose) | The address people type. `http` only for localhost; any other host needs https. |
| `INSTANCE_MODE` | `manager-only` | Or `primary`. |
| `BOOTSTRAP_MANAGER_EMAIL` | none | The manager's Google or GitHub email. |
| `WAA_GOOGLE_CLIENT_ID`, `WAA_GITHUB_CLIENT_ID` | none | From the Google and GitHub consoles. A provider without an id is off. |
| `WAA_SECRETS_DIR` | `docker/secrets` | Folder (outside the repository) with `google_client_secret.txt` and `github_client_secret.txt`, mounted read-only. |
| `WAA_SESSION_IDLE_MINUTES`, `WAA_SESSION_MAX_HOURS` | 240, 24 | Session timeouts. |
| `WAA_TRUSTED_PROXIES` | none | Reverse proxy addresses or networks (primary behind an ingress). |
| `WAA_DP_CERT_FILE`, `WAA_DP_CERT_PASSWORD_FILE` | none | Optional certificate that encrypts the data protection keys at rest. |

Creating the OAuth clients: see "Prerequisites and owner actions" in `docs/superpowers/plans/2026-10-16-plan-4a-secure-sign-in.md`.
Redirect URIs are the public URL plus `/signin-google` and `/signin-github`, exactly.

Data: `data/web/app.db` (SQLite: people, sign-in bindings, sessions, assignments, audit), `data/web/keys/` (the keys
that sign and encrypt cookies; private to the container user), `data/web/backups/` (a copy at start-up and daily, the
newest 14 kept, plus one before any schema upgrade). All of it is personal data: back it up with the recordings, keep it
private. Create `data/web` before the first start (`sudo chown 1654:1654 data/web` on Linux).

Restore a backup: `docker compose -f docker/docker-compose.yml stop web`, copy the chosen `backups/app-*.db` over
`data/web/app.db`, delete `app.db-wal` and `app.db-shm` if present, `docker compose -f docker/docker-compose.yml start web`.
A database written by a newer build is refused at start (`config error: WAA_APP_DIR ... newer than this build`).

Hardening: Content-Security-Policy without inline scripts, no framing, `Referrer-Policy: no-referrer`, no caching of
pages, API or audio; cross-origin POSTs and cross-origin connections to the Blazor hub are refused; sign-in is limited to
20 attempts a minute per client, the clip API to 30 a minute, audio to 1200 range requests a minute; at most 2 clips are
built at once (others wait up to 20 s, then 503). `/healthz` gives details only to the container's own health check.

## Exposure (decision for #56)

- **Manager's machine (secondary, manager-only):** stays on `127.0.0.1:8090`, `WAA_PUBLIC_URL=http://localhost:8090`.
  Open it as `http://localhost:8090` (a request for `127.0.0.1` is redirected there, so the cookie and Google's redirect
  agree). Google and GitHub accept `http://localhost` redirect URIs. Possible later, not part of plan 4a: private
  remote access through Tailscale (`tailscale serve`, which gives an `https://<machine>.<tailnet>.ts.net` address inside
  the tailnet); it would need `WAA_PUBLIC_URL` set to that address, its redirect URIs added to both clients, and a
  check of what the proxy forwards. Not a public tunnel.
- **Cluster (primary):** an HTTPS ingress with a real certificate, `WAA_PUBLIC_URL=https://<host>`,
  `INSTANCE_MODE=primary`, `WAA_TRUSTED_PROXIES` set to the ingress controller's pod network, its own Google client and
  GitHub OAuth app with the https redirect URIs, secrets from a Kubernetes Secret mounted as files. Deploying it is a
  later plan; nothing is exposed by this one.
- Plain `http` on a LAN address is refused by configuration: neither provider allows it and the cookie would travel unencrypted.
````

- [ ] **Step 3: README.md**

Replace the paragraph starting `**There is no sign-in in this version.**` with:
```markdown
**Sign-in with Google or GitHub** is required for everything (managers see all; presenters only their own shows with
5 minutes either side). Before the first start: create the OAuth clients and put their ids in `docker/.env` and their
secrets in a folder outside the repository (see `docs/operations.md`, Sign-in, and the plan 4a prerequisites), and create
`data/web`. The web container is still published on 127.0.0.1 only. It mounts the recordings read-only and writes only
`data/web` and its clip cache. The UI uses PanoramicData.Blazor 10.0.366 and sign-in uses PanoramicData.AuthMagic.AspNetCore.
```
and change `Open http://127.0.0.1:8090` to `Open http://localhost:8090`.

- [ ] **Step 4: Spec corrections** (`docs/superpowers/specs/2026-10-15-webaudioarchive-design.md`)

- 3.1: in "Service-wide settings (... Google client id, bootstrap manager email ...)" change "Google client id" to "Google and GitHub client ids".
- 3.8: replace the bullet "**Sign-in is Google only** (OpenID Connect). ... shown "not authorised"." with: "**Sign-in is Google (OpenID Connect) or GitHub (OAuth)** (decision 2026-10-16, plan 4a, through the PanoramicData.AuthMagic.AspNetCore package). There are no local passwords. Only accounts whose verified email a manager has added can sign in; the provider account is bound to the person on first sign-in; anything else is shown a reason, never the archive."
- 3.8 "Assignment": "a manager assigns Google accounts" becomes "a manager assigns presenters (by the email of their Google or GitHub account)".
- 3.8 "Storage": `SQLite at /data/app.db` becomes "SQLite at `/data/web/app.db` (`data/web` on the host; the only writable folder under the web container's read-only `/data`), with sessions as well as users, assignments and the audit log; backed up daily".
- 3.8 "Sessions": add "server-side session rows; idle 4 hours, at most 24 hours".
- 3.8 "Web app MVP" paragraph: delete the sentence about "no sign-in, roles or audit log" and the "not permission checked" clause, and add: "Sign-in, roles, assignments, the presenter view and the audit log were added by plan 4a (2026-10-16)."
- 3.11 layout: `/data/app.db        users, assignments, audit log` becomes `/data/web/app.db    users, sign-in bindings, sessions, assignments, audit log (keys/ and backups/ beside it)`.
- 3.12 Personal data: "Google account emails" becomes "Google or GitHub account emails and client IP addresses in the audit log".
- 4: "ASP.NET Core Blazor with Google OIDC" becomes "ASP.NET Core Blazor with Google (OIDC) and GitHub sign-in via PanoramicData.AuthMagic.AspNetCore".
- 7 Risks, "Primary (internet-facing) instance exposed": "Google-only sign-in" becomes "Google or GitHub sign-in".
- 8 Decisions: "Web app: Google sign-in only" becomes "Web app: Google or GitHub sign-in"; replace the "Google OAuth clients: not yet created..." bullet with "OAuth clients: one Google client and one GitHub OAuth app per instance, created by the owner (plan 4a prerequisites); ids in `docker/.env`, secrets as files outside the repository."

- [ ] **Step 5: Check for em dashes, secrets and personal data, then commit**

```powershell
Select-String -Path docs\operations.md,README.md,docs\superpowers\specs\*.md,docker\docker-compose.yml -Pattern ([char]0x2014) 
git diff --cached --name-only
git add docker/docker-compose.yml docs/operations.md README.md docs/superpowers/specs/2026-10-15-webaudioarchive-design.md
git commit -m "Docs and compose for sign-in: data/web, secrets outside the repo, exposure decision, spec corrections (#56, #101)"
```
Expected: no em dash found; only the four files staged.

### Task B13: Trial with the real clients before the release (owner present)

Prerequisite: O4, O5 and O6 done, A8 done (local package). The web container is stopped for the trial and started again afterwards; the recorders are not touched.

- [ ] **Step 1: Stop only the web container**

```powershell
cd C:\Users\david\source\repos\davidnmbond\WebAudioArchive
docker compose -f docker/docker-compose.yml stop web
```

- [ ] **Step 2: Run the branch build on http://localhost:8090 as a primary, against the real recordings (read-only)**

```powershell
cd C:\Users\david\source\repos\davidnmbond\WebAudioArchive-4a
$main = "C:\Users\david\source\repos\davidnmbond\WebAudioArchive"
Get-Content "$main\docker\.env" | ForEach-Object { if ($_ -match '^\s*([A-Z_]+)\s*=\s*(.*)\s*$') { Set-Item "env:$($Matches[1])" $Matches[2] } }
$env:WAA_GOOGLE_CLIENT_SECRET_FILE = "$env:WAA_SECRETS_DIR\google_client_secret.txt"
$env:WAA_GITHUB_CLIENT_SECRET_FILE = "$env:WAA_SECRETS_DIR\github_client_secret.txt"
$env:WAA_RECORDERS = "a=$main\data\a,b=$main\data\b"
$env:WAA_RECORDER_HEALTH = "a=http://127.0.0.1:8080/healthz,b=http://127.0.0.1:8081/healthz"
$env:WAA_STATIONS_CONFIG = "$main\config\stations.json"
$env:WAA_CACHE = "$PWD\temp\trial-cache"
$env:WAA_APP_DIR = "$PWD\temp\trial-app"
$env:INSTANCE_MODE = "primary"   # the trial checks the presenter view too; the container stays manager-only
$env:ASPNETCORE_URLS = "http://localhost:8090"
dotnet run --project src/WebAudioArchive.Web -c Release
```
Expected log lines: `Database ... is at schema version 1`, `Sign-in at http://localhost:8090, mode Primary, providers Google and GitHub`. No `config error`.

- [ ] **Step 3: The owner runs the manual checklist, part 1 (items 1 to 16), in Chrome or Edge, then Firefox for items 1 and 2.** The agent watches the console output for errors and records results (pass or fail per item, UTC time) in the B13 commit message or a note to the owner, never the emails.

- [ ] **Step 4: Stop the trial and restore the container**

Ctrl+C the `dotnet run`, then:
```powershell
cd C:\Users\david\source\repos\davidnmbond\WebAudioArchive
docker compose -f docker/docker-compose.yml start web
Remove-Item -Recurse -Force C:\Users\david\source\repos\davidnmbond\WebAudioArchive-4a\temp\trial-app, C:\Users\david\source\repos\davidnmbond\WebAudioArchive-4a\temp\trial-cache
```
(`temp/` is ignored by git; the trial database held real emails, so it is deleted.) Fix any failure on the branch with a test first, then repeat the trial.

### Task B14: Switch to the published package, pinned exactly

Prerequisite: A9 (the maintainer has released and given the exact version, for example 2.1.7).

- [ ] **Step 1: Pin, remove the local source**

In `src/WebAudioArchive.Web/WebAudioArchive.Web.csproj` change the AuthMagic reference to the published version, exactly:
```xml
    <PackageReference Include="PanoramicData.AuthMagic.AspNetCore" Version="2.1.7" />
```
(use the version the maintainer gave; never a range or a floating version). Then:
```powershell
dotnet nuget remove source local-authmagic --configfile "$env:APPDATA\NuGet\NuGet.Config"
Remove-Item C:\Users\david\source\repos\local-nuget\PanoramicData.AuthMagic.AspNetCore.*.nupkg
dotnet restore --force
dotnet list src/WebAudioArchive.Web package | Select-String AuthMagic
```
Expected: restored from nuget.org; the list shows the exact published version.

- [ ] **Step 2: Build, test, and build both images (no compose, nothing is started)**

```powershell
dotnet build -c Release
dotnet test -c Release
docker build -f docker/Dockerfile.web -t webaudioarchive-web:plan4a .
```
Expected: 0 warnings, all tests pass, the image builds (it restores from nuget.org inside the build).

- [ ] **Step 3: Commit**

```powershell
git add src/WebAudioArchive.Web/WebAudioArchive.Web.csproj
git commit -m "Use the published PanoramicData.AuthMagic.AspNetCore, pinned exactly (#100)"
```

### Task B15: Merge, rebuild the web container, owner checklist, close the issues

- [ ] **Step 1: Bring the branch up to date and onto main**

```powershell
cd C:\Users\david\source\repos\davidnmbond\WebAudioArchive-4a
git fetch origin
git rebase origin/main
dotnet test -c Release
Select-String -Path (git diff --name-only origin/main | Where-Object { Test-Path $_ }) -Pattern ([char]0x2014)
git log origin/main..HEAD --format=%s
git push origin HEAD:main
```
Expected: tests pass; no em dash in any changed file; the push is a fast-forward (if it is refused because main moved, rebase again). This repository commits to `main` without pull requests; CI runs on the push and must be green (`gh run watch`).

- [ ] **Step 2: Update the main working tree and rebuild only the web container**

```powershell
cd C:\Users\david\source\repos\davidnmbond\WebAudioArchive
git pull --rebase --autostash
Test-Path data\web
git check-ignore -v docker/.env
docker compose -f docker/docker-compose.yml up -d --build web
docker logs --tail 30 waa-web
curl.exe -s http://127.0.0.1:8080/healthz
curl.exe -s http://127.0.0.1:8081/healthz
```
Expected: `data\web` exists; `.env` is ignored; the web log shows `Sign-in at http://localhost:8090, mode ManagerOnly`; both recorders are connected with an `uptimeSeconds` that kept growing (they were not restarted).

- [ ] **Step 3: The owner runs the manual checklist, part 2 (items 17 to 22).**

- [ ] **Step 4: Close the issues (only after the checklist passes)**

```powershell
foreach ($n in 100, 101, 88, 92, 86, 90, 87, 91, 53, 54, 55, 56) { gh issue close $n --comment "Done in plan 4a (docs/superpowers/plans/2026-10-16-plan-4a-secure-sign-in.md); checked by the owner's checklist on $([DateTime]::UtcNow.ToString('yyyy-MM-dd HH:mm')) UTC." }
gh issue close 1 --comment "Sign-in, the station manager and presenter (slot owner) roles, the mapping of people to shows and the UTC audit trail are done in plan 4a. Editing rights for song details move to the song identification issue."
```
#56 gets an extra comment pointing at "Exposure" in `docs/operations.md`: secondary stays on loopback; the primary will use an HTTPS ingress in the cluster plan.

- [ ] **Step 5: Tidy up**

```powershell
git worktree remove ..\WebAudioArchive-4a
git branch -d plan-4a-secure-sign-in
```

---

## Manual checklist (owner)

Run with the real clients. Note pass or fail and the UTC time for each. Use a private window per account so sessions do not mix.

Part 1, B13 trial (`dotnet run`, `INSTANCE_MODE=primary`, http://localhost:8090):

1. Open http://localhost:8090. Expected: the sign-in page with "Sign in with Google" and "Sign in with GitHub"; nothing else of the archive.
2. Sign in with Google as the manager (the bootstrap email). Expected: Google's account chooser (and possibly "Google hasn't verified this app": Continue), then the Status page; the header shows your email (the bootstrap manager has no display name) and "(manager)".
3. Sign out (header link, then the button). Expected: "You have signed out". Press Back: no archive content is shown from the cache; reloading goes to the sign-in page.
4. Sign in with GitHub as the manager (an account whose verified email is the same). Expected: GitHub's authorise screen the first time, then signed in. The People page shows both a google and a github badge for you.
5. Sign in with a Google account that is not on the list. Expected: "This account is not on this archive's list". Audit shows `sign-in-refused`.
6. On the People page add the presenter (their Google or GitHub email, role Presenter). On Assignments assign them one show title.
7. In a private window sign in as the presenter. Expected: only the Shows link; the calendar shows only their show; no other title anywhere (check week, month and agenda).
8. Open their show. Expected: the player plays, the clip runs from 5 minutes before to 5 minutes after the show, there is no margin box, Download gives the MP3.
9. As the presenter, type http://localhost:8090/recordings, /player, / and /api/clip?from=...&to=... (copy a query from the manager's Player). Expected: "This page is for managers" or 403, and / goes to Shows.
10. As the presenter, open a different show's address (copy one from the manager's calendar). Expected: "No scheduled show ... starts at ...", exactly as for a time with no show.
11. As the manager, disable the presenter. Expected: within a minute the presenter's open page drops to the sign-in page; their next sign-in says access is turned off. Enable them again.
12. As the manager, open Audit. Expected: sign-ins, the refusal, show views, clip, plays and the download, newest first, in UTC.
13. Export managers (JSON). Expected: a file `webaudioarchive-users-<UTC>.json` with your email and no account ids. Import it back. Expected: "Will add: nobody", nothing changed. Delete the file.
14. Browser developer tools, Application: cookies `authmagic` (HttpOnly, SameSite Lax) and `waa-af`; Local Storage holds only `waa-theme`, no tokens.
15. Developer tools, Console, on Status, Shows, a show with the player, Recordings, Player, People, Assignments, Audit, and the sign-in page: no Content-Security-Policy errors. (Any error: report it; the CSP is adjusted narrowly with a test.)
16. Open http://127.0.0.1:8090. Expected: redirected to http://localhost:8090.

Part 2, B15 container (`INSTANCE_MODE=manager-only`):

17. Sign in with Google and with GitHub as the manager: both work against the container.
18. Sign in as the presenter. Expected: "This copy of the archive is for managers only".
19. `docker compose -f docker/docker-compose.yml restart web`, then reload the open page. Expected: still signed in (keys and sessions survived).
20. Recorders: http://127.0.0.1:8080/healthz and 8081: connected, uptime not reset.
21. `git status` in the main checkout: no `.env`, secret, database or export file listed.
22. Leave a tab idle for more than 4 hours (optional, can be checked later). Expected: the next action goes to sign-in.

## Risks

| # | Risk | Handling |
|---|---|---|
| R1 | The Content Security Policy blocks something PanoramicData.Blazor or Blazor needs (an inline script, `eval`, a blob URL for media) | Tests check for inline scripts; checklist item 15 checks the console on every page; any relaxation is narrow (one directive, one source) and tested |
| R2 | The AuthMagic release pipeline: git tags run to 2.0.82 while nuget.org has 2.0.19, so a tagged release may not publish; the API key may not allow a new package id | O3 and O8; group B waits at B13 with a working branch; nothing reaches `main` before B14 |
| R3 | Browser differences for cookies on `http://localhost` | Over loopback http no cookie is Secure and correlation cookies are Lax with GET callbacks; checklist items 1 to 4 run in Chrome or Edge and Firefox |
| R4 | .NET 10 API details assumed by the plan (`KnownIPNetworks`, `ExcludeFromInteractiveRouting` with `AcceptsInteractiveRouting`, `blazor.web.js` served by `MapStaticAssets`, `X509CertificateLoader`) | Each is exercised by a test that fails loudly; the plan says what to change if one differs |
| R5 | Other agents change `Program.cs`, `App.razor`, `ShowPage`, `Shows`, `ClipService` while group B is on its branch | Rebase at the start of every task; the edits are described by intent so they can be re-applied; the final rebase in B15 runs the whole suite |
| R6 | GitHub PKCE: GitHub accepts PKCE for OAuth apps; if it ever ignored it, the state check still binds the flow | The fake provider requires the verifier, so our side is proved to send it |
| R7 | Docker Desktop on Windows: the nested read-write mount under a read-only mount, and chmod on bind mounts | The app checks the folder is writable at start and fails with `config error: WAA_APP_DIR`; a failed chmod is a warning; CI on Linux checks the mode |
| R8 | Google Testing mode: test users only (up to 100), and an "unverified app" screen | Enough for a handful of people; documented in O4 |
| R9 | The 5 minute margins of back-to-back shows contain the neighbouring show's audio | By design of the spec (3.8); stated in the threat model |
| R10 | An email recycled before its real owner first signs in can be bound by the new holder | Managers add people shortly before they sign in; Reset sign-in fixes a wrong binding; the audit shows who bound when |
| R11 | The audit log and sessions table grow | Sessions are pruned daily; audit retention is undecided (spec 3.12) and stays until the manager decides |
| R12 | Clip grants are in memory | After a web restart a presenter reloads their show page, which grants again; documented in D8 |
| R13 | The AuthMagic pull request needs changes the maintainer asks for (Refit, AwesomeAssertions, naming) | A8 PR description raises them; the public contract in A2 is what group B depends on, and changes inside it are absorbed in B14 |

---

## Appendix F: The fake identity provider

Both test suites contain a fake written to this specification: in WebAudioArchive at `tests/WebAudioArchive.Tests/Support/FakeIdentityProvider.cs` (below, as written here), and in PanoramicData.AuthMagic at `PanoramicData.AuthMagic.Test/Server/FakeIdentityProvider.cs`, written separately in that repository's style (tabs, its namespace, its analyzers). It is the authentication handlers' back-channel (`BackchannelHttpHandler`), so the real handlers run unchanged; the test plays the browser by calling `Authorize` with the redirect the app sent.

Behaviour:

| Endpoint | Answers |
|---|---|
| `GET https://accounts.google.com/.well-known/openid-configuration` | Discovery: issuer `https://accounts.google.com`, authorization, token and JWKS endpoints, RS256, S256 |
| `GET https://www.googleapis.com/oauth2/v3/certs` | One RSA signing key (`kid` `fake-key-1`) |
| `POST https://oauth2.googleapis.com/token` | Redeems a code once: client id and secret, redirect URI and PKCE verifier must match; returns an id token signed with the key (or another key), with `sub`, `email`, `email_verified`, `name`, `nonce`, `iss`, `aud`, `iat`, `nbf`, `exp`, each overridable by a `Tamper` |
| `POST https://github.com/login/oauth/access_token` | Same checks; 200 with an `access_token`, or 200 with `{"error":"bad_verification_code"}` as GitHub does |
| `GET https://api.github.com/user` | `{ id, login, name }` for the bearer token, or 401 |
| `GET https://api.github.com/user/emails` | The account's emails with `primary` and `verified` |

```csharp
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Web;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace WebAudioArchive.Tests.Support;

/// <summary>An account at the fake provider. GitHubEmails, when given, is what /user/emails returns. GitHub subjects are numeric.</summary>
public sealed record FakeAccount(
    string Subject, string? Email, bool EmailVerified = true, string? Name = null,
    IReadOnlyList<(string Email, bool Primary, bool Verified)>? GitHubEmails = null);

/// <summary>One way to make the provider or the browser misbehave, for one negative test.</summary>
public sealed record Tamper(
    string? Issuer = null, string? Audience = null, TimeSpan? ExpiresIn = null, string? Nonce = null, bool SignWithOtherKey = false,
    string? ReplaceState = null, bool DropState = false, bool GitHubTokenError = false, bool GitHubUserFails = false);

/// <summary>
/// Google (discovery, signing keys, token endpoint) and GitHub (token, /user, /user/emails) in memory. Codes are single
/// use and bound to the PKCE challenge, nonce, redirect URI and client of the authorization request.
/// </summary>
public sealed class FakeIdentityProvider : HttpMessageHandler
{
    public const string GoogleClientId = "test-client.apps.googleusercontent.com";
    public const string GoogleClientSecret = "test-google-secret-not-real";
    public const string GitHubClientId = "test-github-client-id";
    public const string GitHubClientSecret = "test-github-secret-not-real";
    const string Issuer = "https://accounts.google.com";
    const string KeyId = "fake-key-1";

    readonly RSA _key = RSA.Create(2048);
    readonly RSA _otherKey = RSA.Create(2048);
    readonly ConcurrentDictionary<string, Pending> _codes = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Pending> _githubTokens = new(StringComparer.Ordinal);
    int _pkceVerified;

    /// <summary>How many token requests proved PKCE (the verifier matched the challenge).</summary>
    public int PkceVerified => _pkceVerified;

    sealed record Pending(string Provider, FakeAccount Account, Tamper Tamper, string RedirectUri, string? Nonce, string CodeChallenge, string ClientId);

    /// <summary>The browser at the provider: checks the authorization request and returns the callback URL with a new code.</summary>
    public Uri Authorize(Uri challenge, FakeAccount account, Tamper? tamper = null)
    {
        var q = HttpUtility.ParseQueryString(challenge.Query);
        var provider = challenge.Host switch
        {
            "accounts.google.com" => "google",
            "github.com" => "github",
            _ => throw new InvalidOperationException($"The app sent the browser to {challenge.Host}, not to Google or GitHub."),
        };
        if (q["code_challenge_method"] != "S256" || string.IsNullOrEmpty(q["code_challenge"]))
            throw new InvalidOperationException("The authorization request has no PKCE S256 code challenge.");
        if (string.IsNullOrEmpty(q["state"]))
            throw new InvalidOperationException("The authorization request has no state.");
        if (provider == "google" && (q["response_type"] != "code" || string.IsNullOrEmpty(q["nonce"])))
            throw new InvalidOperationException("The Google request must ask for a code and carry a nonce.");

        var t = tamper ?? new Tamper();
        var code = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(16));
        _codes[code] = new Pending(provider, account, t, q["redirect_uri"]!, q["nonce"], q["code_challenge"]!, q["client_id"]!);
        var query = "code=" + Uri.EscapeDataString(code) + (t.DropState ? "" : "&state=" + Uri.EscapeDataString(t.ReplaceState ?? q["state"]!));
        return new Uri(q["redirect_uri"] + "?" + query);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var route = $"{request.Method} {request.RequestUri!.GetLeftPart(UriPartial.Path)}";
        return route switch
        {
            "GET https://accounts.google.com/.well-known/openid-configuration" => Json(new
            {
                issuer = Issuer,
                authorization_endpoint = "https://accounts.google.com/o/oauth2/v2/auth",
                token_endpoint = "https://oauth2.googleapis.com/token",
                jwks_uri = "https://www.googleapis.com/oauth2/v3/certs",
                response_types_supported = new[] { "code" },
                subject_types_supported = new[] { "public" },
                id_token_signing_alg_values_supported = new[] { "RS256" },
                code_challenge_methods_supported = new[] { "S256" },
            }),
            "GET https://www.googleapis.com/oauth2/v3/certs" => Json(new { keys = new[] { Jwk(_key) } }),
            "POST https://oauth2.googleapis.com/token" => GoogleToken(await FormAsync(request, cancellationToken)),
            "POST https://github.com/login/oauth/access_token" => GitHubToken(await FormAsync(request, cancellationToken)),
            "GET https://api.github.com/user" => GitHubUser(request),
            "GET https://api.github.com/user/emails" => GitHubEmails(request),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("fake provider: no route " + route) },
        };
    }

    HttpResponseMessage GoogleToken(Dictionary<string, string> form)
    {
        if (form.GetValueOrDefault("grant_type") != "authorization_code" || Redeem(form, "google", GoogleClientId, GoogleClientSecret) is not { } p)
            return Json(new { error = "invalid_grant" }, HttpStatusCode.BadRequest);
        var t = p.Tamper;
        var expires = DateTime.UtcNow + (t.ExpiresIn ?? TimeSpan.FromHours(1));
        var issued = expires - TimeSpan.FromHours(1);
        var claims = new Dictionary<string, object>
        {
            ["sub"] = p.Account.Subject,
            ["email_verified"] = p.Account.EmailVerified,
            ["nonce"] = t.Nonce ?? p.Nonce!,
            ["azp"] = GoogleClientId,
        };
        if (p.Account.Email is not null) claims["email"] = p.Account.Email;
        if (p.Account.Name is not null) claims["name"] = p.Account.Name;
        var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = t.Issuer ?? Issuer,
            Audience = t.Audience ?? GoogleClientId,
            IssuedAt = issued,
            NotBefore = issued,
            Expires = expires,
            Claims = claims,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(t.SignWithOtherKey ? _otherKey : _key) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
        });
        return Json(new { access_token = "ya29.fake-" + Guid.NewGuid().ToString("N"), id_token = idToken, token_type = "Bearer", expires_in = 3600, scope = "openid email profile" });
    }

    HttpResponseMessage GitHubToken(Dictionary<string, string> form)
    {
        // GitHub answers 200 with an error body when a code is wrong or used.
        if (Redeem(form, "github", GitHubClientId, GitHubClientSecret) is not { } p || p.Tamper.GitHubTokenError)
            return Json(new { error = "bad_verification_code", error_description = "The code passed is incorrect or expired." });
        var token = "gho_fake" + Guid.NewGuid().ToString("N");
        _githubTokens[token] = p;
        return Json(new { access_token = token, token_type = "bearer", scope = "read:user,user:email" });
    }

    HttpResponseMessage GitHubUser(HttpRequestMessage request) =>
        Bearer(request) is { Tamper.GitHubUserFails: false } p
            ? Json(new { id = long.Parse(p.Account.Subject, CultureInfo.InvariantCulture), login = "user" + p.Account.Subject, name = p.Account.Name })
            : new HttpResponseMessage(HttpStatusCode.Unauthorized);

    HttpResponseMessage GitHubEmails(HttpRequestMessage request)
    {
        if (Bearer(request) is not { } p) return new HttpResponseMessage(HttpStatusCode.Unauthorized);
        IReadOnlyList<(string Email, bool Primary, bool Verified)> emails = p.Account.GitHubEmails
            ?? (p.Account.Email is null ? [] : [(p.Account.Email, true, p.Account.EmailVerified)]);
        return Json(emails.Select(e => new { email = e.Email, primary = e.Primary, verified = e.Verified, visibility = "private" }));
    }

    Pending? Redeem(Dictionary<string, string> form, string provider, string clientId, string clientSecret)
    {
        if (!form.TryGetValue("code", out var code) || !_codes.TryRemove(code, out var p) || p.Provider != provider) return null; // single use
        if (form.GetValueOrDefault("client_id") != clientId || p.ClientId != clientId || form.GetValueOrDefault("client_secret") != clientSecret) return null;
        if (form.GetValueOrDefault("redirect_uri") != p.RedirectUri) return null;
        var verifier = form.GetValueOrDefault("code_verifier");
        if (verifier is null || Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) != p.CodeChallenge) return null;
        Interlocked.Increment(ref _pkceVerified);
        return p;
    }

    Pending? Bearer(HttpRequestMessage request) =>
        request.Headers.Authorization is { Scheme: "Bearer", Parameter: { } token } && _githubTokens.TryGetValue(token, out var p) ? p : null;

    static object Jwk(RSA key)
    {
        var parameters = key.ExportParameters(false);
        return new { kty = "RSA", use = "sig", alg = "RS256", kid = KeyId, n = Base64UrlEncoder.Encode(parameters.Modulus), e = Base64UrlEncoder.Encode(parameters.Exponent) };
    }

    static HttpResponseMessage Json(object body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json") };

    static async Task<Dictionary<string, string>> FormAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var parsed = HttpUtility.ParseQueryString(await request.Content!.ReadAsStringAsync(cancellationToken));
        return parsed.AllKeys.OfType<string>().ToDictionary(k => k, k => parsed[k]!, StringComparer.Ordinal);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _key.Dispose();
            _otherKey.Dispose();
        }

        base.Dispose(disposing);
    }
}
```
The test client ids and secrets are deliberately fake strings; no real credential is ever used in a test.

---

## Self-review

**Spec coverage (3.8, 3.10, 3.12, 5, 6):**
- Sign-in, allow-list, no local passwords, "not authorised" refusal: A2 to A6, B5, B6.
- Roles manager and presenter; presenters only their shows with the 5 minute margins and nothing else (titles included); manager sees all: B7, B8.
- Assignment by title, all occurrences, optional date range; derived hosts never grant access (no code path reads derived data): B8.
- Bootstrap manager by configured email, never first-come-first-served: B4.
- SQLite per instance with users, assignments, audit (plus logins and sessions), migrations, backups: B3, B4, B5, B6 (housekeeper), B12 (restore notes).
- Server-side authorisation on every request; unguessable clip URLs that are still permission checked; audit of every access: B6 (fallback policy and hub), B7 (pages, API, media grants), B9.
- Secure HttpOnly SameSite cookies, CSRF on all forms, sign-out ends the session: A3, B6, B11.
- Instance mode primary or manager-only, presenters refused and no assignments on manager-only: B2, B4, B5, B8.
- Two instances, two sets of accounts, export and import of the manager list: B10.
- Reachability: loopback secondary, HTTPS ingress primary, documented: B12 (#56).
- Personal data (emails, IPs in the audit log) noted in the spec and operations: B12.
- Deployment: config and secrets injected by environment and files, non-root, data directory a volume: B12.
- Testing (spec 6, web and security): presenter gets 404 for another show's page and audio by URL (B7, B8); unlisted account refused (B6); manager sees everything (B7, B8); audit records accesses (B9); exactly the 5 minute margins (B8).

**Deliberately not in this plan:** the weekly report (#89, needs analysis), transcripts, search and events in the player (later plans; when they arrive they must go through `ShowAccess` and the fallback policy), the cluster deployment and its manifest (later plan), song editing rights for slot owners (song identification issue), audit retention (undecided in the spec).

**Placeholders:** none besides two values that do not exist until an earlier step produces them, each named where it is produced: the local prerelease version (A8 Step 1, used in B6 Step 1) and the published version (A9, used in B14 Step 1).

**Type consistency:** `AuthMagicExternalAccount(Provider, Subject, VerifiedEmails, DisplayName)` (A2) maps one to one onto `ExternalAccount` (B5) in `ArchiveGate` (B6). Claim types: `AuthMagicServerDefaults.ProviderClaim` and `SubjectClaim` (A2) are added by `CreatePrincipal` (A3) and rebuilt by `ArchiveClaims.For` (B6); `ArchiveClaims.SessionId` and `UserId` with `ClaimTypes.Name`, `Email` and `Role` are read by `Viewer.From` (B4). Role names `manager` and `presenter` are the same in the SQL `CHECK` (B3), `Roles` (B4) and the policies (B6). Refusal codes produced by `AccountGate` (B5) are exactly the codes the denied page explains (B6) and all pass `AuthMagicAdmission.Refuse` validation (A2). Scheme names used by the test configuration (`AuthMagicServerDefaults.GoogleScheme`, `GitHubScheme`, `CookieScheme`) are the ones `AddAuthMagicServer` registers (A3 to A5). `SessionStore.Validate(raw, touch)` is called with `touch: true` from HTTP requests (B6 `ArchiveGate`) and `touch: false` from circuit revalidation (B6 `SessionRevalidator.Check`), with circuit activity touching through `SessionActivityHandler`.
