# Matmon.Cloud & Licensing — Concept / Roadmap

> Status: **planning**. This is the reference for the commercial direction so it doesn't get lost.
> Not legal advice — have a lawyer review licensing/EULA before commercial launch.

## 1. Goal
Turn Matmon (self-hosted monitor) into a product with a commercial control plane
**Matmon.Cloud** (separate git repo, same stack) and a tiered license model.

## 2. Licensing decision (done / in progress)
- The code `LICENSE` was changed **MIT → proprietary** (relicensed before any public
  download; prior MIT grant on already-published versions can't be revoked, but there
  were none in the wild).
- Repo goes **private**. Public distribution happens via the **Docker images**
  (GHCR + Docker Hub), not the source.
- Strategy going forward: **Open-Core** — a freely usable core drives adoption; money
  comes from **Matmon.Cloud + entitlements**, not from locking the core. If we ever
  re-open parts, use a source-available license (BSL 1.1 or Elastic License 2.0 — ELv2
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

**⚠️ Open question — "Cloud Required" for Free:** forcing cloud on free users conflicts
with the self-hosted selling point and costs us infra money. Recommendation: make Free
**cloud-optional** (cloud *features* gated by account, but the app runs offline), or if
cloud stays mandatory, cap free-tier cloud cost hard (metadata only, aggressive limits).

## 4. Matmon.Cloud architecture
Matmon.Cloud is to a **Primary node** what a **Secondary probe** is to a Primary — reuse
the existing outbound heartbeat + `X-Matmon-Probe-Token` pattern (firewall-friendly).

```
[Secondary Probes] -> [Primary Node (on-prem)] -> [Matmon.Cloud (multi-tenant)]
```

Responsibilities:
1. **Primary heartbeat / dead-man-switch** — alert when an on-prem Primary goes offline.
2. **Notification gateway** — Primaries push notification intents; the cloud delivers
   (email/SMS/push/webhook). Builds on the on-prem N0 dispatch: add a "cloud gateway"
   sender alongside MailKit.
3. **Public metadata dashboard** — aggregated status/alerts/uptime only (NOT full
   telemetry — that stays on-prem for privacy + volume). Shareable status pages.
4. *(later)* **Template store** — share/curate sensor templates (copy+origin model exists).
5. *(later)* **Backup location** — encrypted off-site backups (backup jobs/sections exist).
6. **License authority** — issues/validates license tokens, counts probes.

Stack: ASP.NET Core .NET 10 (same), but **multi-tenant + PostgreSQL** (not the JSON
workspace), S3-compatible object storage for backups, horizontally scalable.
**New repo: `Matmon.Cloud`.**

## 5. License enforcement (to build in Matmon first)
- **License token**: signed (Ed25519/JWT) by Matmon.Cloud; contains tier, probe limit,
  feature flags, expiry, customer id. Public key baked into the binary → **offline
  validation**; periodic online refresh only for revocation/probe counting.
- **`ILicenseService`** (Core): `HasFeature(x)`, `ProbeLimit`, current tier. UI +
  executors query it.
- **Per-probe counting**: Primary counts connected secondaries (already have
  `InMemoryProbeRegistry`/heartbeat). Over limit → **graceful degrade** (no new probes,
  banner, grace period) — never hard-kill, never drop data.
- **Offline grace**: keep running for N days without cloud (a monitor must not fail
  because licensing/cloud is unreachable).

## 6. Market / positioning
- Between "too simple" (Uptime Kuma) and "too complex/expensive" (PRTG, Datadog).
- **Strongest angle: MSPs** — per-probe pricing scales with their client sites;
  white-label public dashboards + per-customer PDF audits are directly sellable.

## 7. Roadmap
1. **License foundation in Matmon** — `ILicenseService`, entitlement checks,
   cloud-connect client (token auth like secondary→primary). *Can start now.*
2. **Matmon.Cloud MVP** — accounts (multi-tenant), Primary heartbeat/dead-man-switch,
   notification gateway, one public dashboard.
3. **Monetization** — billing (Stripe), per-probe counting, tiers enforced.
4. **Expansion** — template store, backup location, white-label, MSP features.
5. **Enterprise** — SSO/RBAC, self-hosted Matmon.Cloud, SLA.

## 8. Risks
- MIT history → monetize via cloud/entitlements, not by locking the core; CLA now.
- Free-tier cloud cost → metadata-only + hard limits.
- Cloud-required vs self-hosted ethos → prefer cloud-optional free tier.
- Free competitors (Uptime Kuma, Zabbix) → differentiate via reports/MSP/cloud value.
- A monitor must never fail due to licensing/cloud → generous offline grace, degrade.

## 9. Open inputs needed
Target region/currency, price points, bootstrapping vs funding, team.
