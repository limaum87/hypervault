# HyperVault Manager

Central web console for managing multiple **HyperVBackupAgent** instances
installed on Hyper-V hosts. The Manager runs on Linux (via Docker Compose),
keeps a SQLite database, and drives every Hyper-V operation through each
agent's HTTP API — **the Manager never touches Hyper-V directly.**

> This directory implements the brief described below. The original project
> specification is preserved at the end of this file.

---

## Features (MVP)

- **Hosts** — register Hyper-V hosts (name, IP/FQDN, port, API token, HTTPS).
  Tokens are stored **server-side only** and never returned to the browser.
- **Test connection** — probes `GET /health/live` + `/health/ready` on the agent.
- **Sync VMs** — pulls `GET /vms` from the agent and stores them.
- **Storages** — local path / SMB destinations.
- **Jobs** — scheduled (5-field cron) or manual backup jobs, full or incremental,
  per-VM retention in days.
- **Manual backup** — preflight → enqueue agent job → poll → history.
- **History** — every backup run with status, size, duration, agent job id,
  correlation id, error/message.
- **Verify** — `verify-chain` / `verify-restore` against the agent.
- **Restore** — pick a backup/restore point + target host + new name; overwrite
  is **off by default** and requires explicit confirmation (safety check).
- **Dashboard** — hosts online/offline, VMs without backup, last-24h backups &
  failures, estimated storage used, recent activity.
- **Multilanguage UI** — **English (default)** and **Portuguese**, switchable from
  the sidebar; the choice is remembered per browser.
- **Authentication & users** — admin login (cookie-based session), user accounts
  with `admin` / `user` roles, managed from the **Settings** screen. The Manager
  is fully protected: every `/api/*` call requires a valid session.

## Authentication

The console is **not** open by default. Every API endpoint (except
`POST /api/auth/login`) requires an authenticated session cookie.

**Default account (seeded on first run):**

| Username | Password | Role  |
|----------|----------|-------|
| `admin`  | `admin`  | admin |

> ⚠️ **Change the default password immediately** from *Settings → My account*
> after first login.

### Roles

- **admin** — full access, including user management (Settings → Users).
- **user** — can use all backup/restore/verify features but **cannot** manage
  users (the Users section is hidden and `/api/users` returns `403`).

### Settings screen

Reached from the sidebar (`⚙ Settings`):

- **My account** — view your username/role and change your own password.
- **Users** (admin only) — create / edit / enable-disable / reset-password /
  delete users.

### Safety rules

The API enforces these so you can never lock yourself out:

- You **cannot delete your own account**.
- You **cannot delete or demote the last enabled admin**.
- Passwords must be at least 4 characters (PBKDF2-HMAC-SHA256, 100k iterations).
- Disabling a user immediately blocks their login (existing sessions expire on
  cookie timeout, 8 h sliding).

## Architecture

```
┌────────────┐   HTTPS + Bearer token   ┌────────────────────┐
│  Browser   │  (JSON + X-Correlation-Id)│  HyperVBackupAgent │  ──► Hyper-V (PowerShell/Native RCT)
│  (EN / PT) │ ◄──────────────────────► │  API (Windows)     │
└─────┬──────┘                           └────────────────────┘
      │ HTTP (same container)
┌─────▼──────────────────────────────────────────┐
│ HyperVaultManager.Web  (ASP.NET Core 8)        │
│  ├─ Minimal API (/api/...)                      │
│  ├─ SQLite (EF Core, stored as ISO strings)     │
│  ├─ Background services:                        │
│  │   • JobRunner  (preflight→enqueue→poll)       │
│  │   • Scheduler  (cron trigger)                 │
│  │   • Health     (host status)                  │
│  └─ Static frontend (wwwroot: HTML/CSS/JS)       │
└────────────────────────────────────────────────┘
```

Long operations (backup/restore of large VHDXs) run as **async jobs**: the
Manager enqueues a job on the agent (`202 Accepted` + `jobId`), then polls
`GET /jobs/{jobId}` until terminal. The HTTP request to the agent itself is
short; only the polling loop is long.

## Quick start

Requirements: Docker + Docker Compose on a Linux host.

```bash
cd HyperVaultManager
docker compose up -d --build
```

Then open **http://localhost:8096**.

- Host port **8096** is used to avoid collisions with other services on this
  host (8088 was already taken here). Change the left side of
  `ports: ["8096:8080"]` in `docker-compose.yml` freely.
- The SQLite database lives in a named volume (`manager-data`) at `/data`
  inside the container, so it survives restarts/redeploys.

### Configuration (environment / `appsettings.json`)

| Setting                                | Default | Description                                  |
|----------------------------------------|---------|----------------------------------------------|
| `Manager:DataPath`                     | `/data` | Where the SQLite file is written.            |
| `Manager:HealthCheckIntervalSeconds`   | `60`    | How often host status is re-probed.          |
| `Manager:JobPollIntervalSeconds`       | `5`     | Polling cadence for in-flight agent jobs.    |
| `Manager:BackupTimeoutHours`           | `8`     | Max wall time for a backup job.              |
| `Manager:VerifyTimeoutHours`           | `4`     | Max wall time for a verify job.              |
| `Manager:RestoreTimeoutHours`          | `12`    | Max wall time for a restore job.             |

Override via the compose `environment:` map (use `__` for sections), e.g.
`Manager__HealthCheckIntervalSeconds: 30`.

> The Manager trusts the agents' self-signed certificates (they are reached
> over HTTPS by default). This is intentional for the MVP; the agents only
> accept a Bearer token and the Manager never exposes tokens to the browser.

## Manager API (`/api`)

| Method | Path                                         | Purpose                                  |
|--------|----------------------------------------------|------------------------------------------|
| POST   | `/api/auth/login`                            | Authenticate, set session cookie.         |
| POST   | `/api/auth/logout`                           | Clear session.                            |
| GET    | `/api/auth/me`                               | Current session user.                     |
| POST   | `/api/auth/change-password`                  | Change own password.                      |
| GET    | `/api/users`                                 | List users **(admin)**.                   |
| POST   | `/api/users`                                 | Create user **(admin)**.                  |
| PUT    | `/api/users/{id}`                            | Edit user **(admin)**.                    |
| POST   | `/api/users/{id}/reset-password`             | Reset password **(admin)**.               |
| DELETE | `/api/users/{id}`                             | Delete user **(admin)**.                   |
| GET    | `/api/dashboard`                             | Aggregated dashboard metrics.            |
| GET/POST/PUT/DELETE | `/api/hosts` · `/api/hosts/{id}`  | Host CRUD.                               |
| POST   | `/api/hosts/{id}/test`                       | Probe agent `/health`, update status.    |
| POST   | `/api/hosts/{id}/sync-vms`                   | Pull `GET /vms`, upsert VMs.             |
| GET    | `/api/hosts/{id}/vms/{vmId}/restore-points`  | Proxy to agent restore-points.           |
| GET/POST/PUT/DELETE | `/api/storages` · `/api/storages/{id}` | Storage CRUD.                          |
| GET    | `/api/vms?hostId=`                           | VMs with last-backup info.               |
| POST   | `/api/hosts/{h}/vms/{v}/backup`              | Manual backup (full/incremental).        |
| GET/POST/PUT/DELETE | `/api/jobs` · `/api/jobs/{id}`    | Job CRUD.                                |
| POST   | `/api/jobs/{id}/run-now`                     | Trigger a scheduled job immediately.     |
| GET    | `/api/backups?hostId=&vmId=&status=&limit=`  | Backup history.                          |
| POST   | `/api/backups/{id}/verify`                   | Verify a completed backup's chain.       |
| GET    | `/api/verifications`                         | Verification history.                    |
| POST   | `/api/verify`                                | Standalone verify (chain/restore).       |
| POST   | `/api/restore`                               | Restore (safety-gated overwrite).        |
| GET    | `/api/restores`                              | Restore history.                         |

Agent communication always sends `Authorization: Bearer <token>` and an
`X-Correlation-Id`; the Manager records the agent `jobId` and `correlationId`
on each run for traceability.

## Multilanguage (i18n)

- Default language: **English**.
- Available: **English (en)**, **Portuguese (pt)**.
- Translation files: `wwwroot/locales/en.json`, `wwwroot/locales/pt.json`.
- Switch in the sidebar (EN / PT). Preference is stored in `localStorage`.
- The engine (`wwwroot/js/i18n.js`) translates any element with
  `data-i18n`, `data-i18n-ph`, or `data-i18n-html`, and exposes `window.i18n.t(key)`
  for dynamic strings. Adding a language = add a `wwwroot/locales/<code>.json`
  and the matching button.

## Project layout

```
HyperVaultManager/
├── Dockerfile · docker-compose.yml · .dockerignore
├── HyperVaultManager.Web.csproj · Program.cs · appsettings.json
├── Models/            # EF entities + DTOs
├── Data/              # ManagerDbContext (SQLite)
├── Services/          # AgentClient, JobRunner, Scheduler, Health, Cron, Queue
├── wwwroot/
│   ├── index.html · css/styles.css
│   ├── js/{i18n,api,app}.js
│   └── locales/{en,pt}.json
└── README.md
```

## Building / running without Docker

```bash
dotnet run -c Release --project HyperVaultManager.Web.csproj
# Web UI at http://localhost:5000  (ASPNETCORE_URLS)
```

---

## Specification (original brief)

> Desenvolver um servidor central para Linux que gerencie múltiplos agentes
> HyperVBackupAgent instalados em hosts Hyper-V, permitindo cadastrar hosts,
> listar VMs, criar jobs de backup, acompanhar histórico, verificar backups e
> disparar restore.

**Funcionalidades principais:** cadastro de hosts (nome, IP/FQDN, porta, token,
status); testar conexão (`GET /health`); sincronizar VMs (`GET /vms`);
cadastro de storages (`local_path` / `smb`); criação de jobs (VM, host, storage,
horário, retenção, tipo); execução manual de backup; histórico de backups;
verificação de backup; restore (nunca sobrescrever sem confirmação explícita);
dashboard (hosts online/offline, últimos backups, falhas recentes, VMs sem
backup, uso estimado de storage).

**Requisitos:** o Manager não acessa o Hyper-V diretamente — toda operação é
feita pelo agente Windows via HTTPS, com token por host; logs estruturados;
tratamento claro de falhas; jobs agendados via `BackgroundService`; Dockerfile +
`docker-compose.yml`; README com instruções de instalação no Linux.

**Limitações aceitas no MVP:** backup full (+ incremental já suportado pelo
agente), storage SMB/local path, sem multiusuário, sem criptografia de backup.
**Preparado para o futuro:** retenção por cadeia full+incrementais, múltiplos
storages, S3, alertas (Nagios/webhook/email), restore para host diferente,
verify-chain e verify-restore.
