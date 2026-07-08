# Matmon.Cloud & Licensing - Concept / Roadmap

> Status: **planning**. This is the reference for the commercial direction so it doesn't get lost.
> Not legal advice - have a lawyer review licensing/EULA before commercial launch.

## 1. Goal
Turn Matmon (self-hosted monitor) into a product with a commercial control plane
**Matmon.Cloud** (separate git repo, same stack) and a tiered license model.

## 2. Licensing decision (done / in progress)
- The code `LICENSE` was changed **MIT → proprietary** (relicensed before any public
  download; prior MIT grant on already-published versions can't be revoked, but there
  were none in the wild).
- Repo goes **private**. Public distribution happens via the **Docker images**
  (GHCR + Docker Hub), not the source.
- Strategy going forward: **Open-Core** - a freely usable core drives adoption; money
  comes from **Matmon.Cloud + entitlements**, not from locking the core. If we ever
  re-open parts, use a source-available license (BSL 1.1 or Elastic License 2.0 - ELv2
  is a good fit because it forbids circumventing license keys and reselling as a service).
- **Action:** introduce a **Contributor License Agreement (CLA)** before accepting
  outside contributions, so future relicensing stays possible.

## 3. Product license tiers
| | Free Home | Business | Enterprise |
|---|---|---|---|
| Price | 0 € (cloud account) | per **probe** / month | custom / from X €/yr |
| Probes | 1 | per licensed probe | unlimited |
| Sensors | limited (~50) | generous / unlimited | unlimited |
| Cloud notification gateway | basic (limits) | full | full + custom channels |
| Public dashboard | 1, Matmon-branded | multiple | **white-label** |
| PDF audit (N4) | branded | unbranded | white-label |
| Backup location / template store | – | included quota | included + on-prem |
| Self-hosted Matmon.Cloud | – | – | yes (air-gapped) |
| Support / SLA | community | e-mail/priority | SLA, SSO/RBAC, dedicated |

**⚠️ Open question - "Cloud Required" for Free:** forcing cloud on free users conflicts
with the self-hosted selling point and costs us infra money. Recommendation: make Free
**cloud-optional** (cloud *features* gated by account, but the app runs offline), or if
cloud stays mandatory, cap free-tier cloud cost hard (metadata only, aggressive limits).

## 4. Matmon.Cloud architecture
Matmon.Cloud is to a **Primary node** what a **Secondary probe** is to a Primary - reuse
the existing outbound heartbeat + `X-Matmon-Probe-Token` pattern (firewall-friendly).

```
[Secondary Probes] -> [Primary Node (on-prem)] -> [Matmon.Cloud (multi-tenant)]
```

Responsibilities:
1. **Primary heartbeat / dead-man-switch** - alert when an on-prem Primary goes offline.
2. **Notification gateway** - Primaries push notification intents; the cloud delivers
   (email/SMS/push/webhook). Builds on the on-prem N0 dispatch: add a "cloud gateway"
   sender alongside MailKit.
3. **Public metadata dashboard** - aggregated status/alerts/uptime only (NOT full
   telemetry - that stays on-prem for privacy + volume). Shareable status pages.
4. *(later)* **Template store** - share/curate sensor templates (copy+origin model exists).
5. *(later)* **Backup location** - encrypted off-site backups (backup jobs/sections exist).
6. **License authority** - issues/validates license tokens, counts probes.

Stack: ASP.NET Core .NET 10 (same), but **multi-tenant + PostgreSQL** (not the JSON
workspace), S3-compatible object storage for backups, horizontally scalable.
**New repo: `Matmon.Cloud`.**

## 4a. Identity & Cloud SSO (UniFi-style)
Goal: one **cloud account (e-mail + password, later MFA)** that can log into the cloud
**and** into local instances, and that owns **multiple instances**.

Model (like Ubiquiti account / UniFi):
- **Matmon.Cloud is the Identity Provider (IdP)** - OIDC/OAuth2 (or a simple token
  exchange). The cloud account is keyed by **e-mail**.
- **Local accounts stay and work offline** (e-mail + password) - a monitor must log in
  even when the cloud is unreachable. Cloud SSO is **additive, not required**.
- **Instance linking / claim:** an instance is "claimed" by a cloud account (reusing the
  outbound primary→cloud connection + token). Once linked, the Login page offers
  **"Sign in with Matmon Cloud"** (redirect/token exchange); the cloud identity maps to a
  local user (auto-provisioned with a role), and the cloud caches enough for offline login.
- **One account → many instances:** in the cloud, an **account/org owns N linked
  instances** with **per-instance roles** (owner/admin/viewer). From the cloud dashboard
  you see all instances and SSO into any - same multi-tenant model as §3/§4.

Steps already done / next:
- ✅ Local login switched from *username* to **e-mail + password** (`LoginModel.Email`;
  `ValidateUser` still matches a legacy username for old accounts). This makes the local
  identity key the same as the future cloud identity.
- ✅ **Cloud accounts + instance ownership + roles (multi-tenant base)** - Matmon.Cloud has
  e-mail/password accounts (PBKDF2) and an `InstanceMembership` model: **one account → many
  instances**, **one instance → many accounts**, each with a role **Owner/Admin/Viewer**
  (`InstanceService.AddMember/RemoveMember/ListMembers`, owner-gated; owner membership is
  created with the instance and backfilled for older ones). The cloud dashboard/instances
  list show the per-instance role; a Members page grants/revokes access. This is the shared
  base for cloud-side alerts and later Full Access.
- ⬜ Cloud IdP proper (OIDC/token exchange + MFA), instance-claim flow, "Sign in with
  Matmon Cloud" button, cloud-identity → local-user mapping, offline credential cache.
  (The role model above already carries the per-instance role Full Access will assert.)

## 5. License enforcement (to build in Matmon first)
- **License token**: signed (Ed25519/JWT) by Matmon.Cloud; contains tier, probe limit,
  feature flags, expiry, customer id. Public key baked into the binary → **offline
  validation**; periodic online refresh only for revocation/probe counting.
- **`ILicenseService`** (Core): `HasFeature(x)`, `ProbeLimit`, current tier. UI +
  executors query it.
- **Per-probe counting**: Primary counts connected secondaries (already have
  `InMemoryProbeRegistry`/heartbeat). Over limit → **graceful degrade** (no new probes,
  banner, grace period) - never hard-kill, never drop data.
- **Offline grace**: keep running for N days without cloud (a monitor must not fail
  because licensing/cloud is unreachable).

## 6. Market / positioning
- Between "too simple" (Uptime Kuma) and "too complex/expensive" (PRTG, Datadog).
- **Strongest angle: MSPs** - per-probe pricing scales with their client sites;
  white-label public dashboards + per-customer PDF audits are directly sellable.

## 7. Roadmap
1. **License foundation in Matmon** - `ILicenseService`, entitlement checks,
   cloud-connect client (token auth like secondary→primary). *Can start now.*
2. **Matmon.Cloud MVP** - accounts (multi-tenant), Primary heartbeat/dead-man-switch,
   notification gateway, one public dashboard.
3. **Monetization** - billing (Stripe), per-probe counting, tiers enforced.
4. **Expansion** - template store, backup location, white-label, MSP features.
5. **Enterprise** - SSO/RBAC, self-hosted Matmon.Cloud, SLA.

## 8. Risks
- MIT history → monetize via cloud/entitlements, not by locking the core; CLA now.
- Free-tier cloud cost → metadata-only + hard limits.
- Cloud-required vs self-hosted ethos → prefer cloud-optional free tier.
- Free competitors (Uptime Kuma, Zabbix) → differentiate via reports/MSP/cloud value.
- A monitor must never fail due to licensing/cloud → generous offline grace, degrade.

## 9. Open inputs needed
Target region/currency, price points, bootstrapping vs funding, team.

## 10. Backlog - Cloud Sensors ("Cloud Probe")
Idea (license-gated): a user's Primary shows a **"Cloud Probe"** that IS Matmon.Cloud.
On it you can create only **specific, approved external sensor types** - Ping, HTTP/HTTPS,
TCP, DNS, SSL-certificate, NTP - i.e. things that monitor a company **from the outside**
(is my site up / latency / cert expiry as seen from the internet). Deliberately **not**
script/SNMP/credentialed/internal sensors (safety + multi-tenant isolation).

Architecture (elegant, unified code): the Matmon.Cloud stack **runs a normal Matmon
instance** (or reuses the `Matmon.Core` `ISensorExecutor` engine + the secondary-probe
worker) to actually execute those sensors from the cloud's vantage point. Results flow
back to the user's Primary as the "Cloud Probe" (same primary↔probe model we already have).
So: one codebase, the cloud is "just another probe" that only exposes an allow-listed set
of externally-safe sensor types.

To build: an executor allow-list for the cloud probe; per-tenant quota/limits; wire the
cloud-hosted executor to a tenant's account/instance; surface the Cloud Probe + its
permitted sensor types in the Primary UI (license-gated).

## 11. Full remote access (UniFi-style) - design for it now, build later
Goal: operate a Matmon instance **fully through the cloud with the SAME UI** - like
unifi.ui.com proxying into a local console. Key stance: **the cloud PROXIES the local
Matmon UI (reverse tunnel); we do NOT rebuild a parallel management UI in the cloud.**
Same philosophy as Cloud Sensors: reuse one codebase, don't duplicate.

**Tiered, modular access - per user, per instance:**
1. **Status only** (what exists) - heartbeat/metadata + public page.
2. **Alerts / ack** - see + acknowledge alerts via the cloud.
3. **Full access** - the cloud reverse-proxies the local Matmon UI so the user drives the
   real UI remotely (behind NAT).
One cloud account → **many instances**, each with a **role** (owner/admin/viewer) +
**access tier**.

**How Full Access works:** the Primary already connects **outbound** to the cloud. Upgrade
that to a **persistent bidirectional channel (WebSocket)**; the cloud tunnels the user's
browser HTTP through it to the local UI. Auth: the cloud (SSO) asserts "user X has role Y on
instance Z"; the local instance trusts that assertion (signed by the instance link).

**Vorsehen now (cheap, keeps the door open - don't build the tunnel yet):**
- Cloud stays **portal + proxy**; don't invest in a duplicate cloud-side management UI.
- Keep the outbound `CloudConnectionService` link **upgradeable to a WS tunnel** (it's the seed).
- Keep the local Matmon UI **reverse-proxy / base-path friendly** - relative URLs, honor
  forwarded headers (already configured), no hardcoded absolute hosts.
- Model **per-instance role + access tier** in the cloud ownership schema when we add roles.
- **Cloud SSO is the prerequisite** for Full Access (local must trust a cloud-issued identity).
- Full Access is powerful → per-instance capability checks + audit + strong auth (MFA).
