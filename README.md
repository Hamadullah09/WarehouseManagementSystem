# Warehouse RFID Gate Management System

Entry/exit gate control for a warehouse using a **Chainway U300** fixed UHF
RFID reader. Cartons pass a gate, the reader sees their EPC tags, and the
system decides — against a planned document — whether that movement is
legitimate before any stock moves.

A movement is never accepted merely because tags were seen. It requires, all
at once: every expected EPC present, nothing unknown, nothing unexpected, a
non-empty read, and a reader that stayed healthy for the whole cycle.

---

## Contents

- [How it fits together](#how-it-fits-together)
- [Prerequisites](#prerequisites)
- [Running it locally](#running-it-locally)
- [Connecting a real U300](#connecting-a-real-u300)
- [Configuration](#configuration)
- [API](#api)
- [Tests](#tests)
- [Production deployment](#production-deployment)

---

## How it fits together

```
React SPA ──REST + SignalR──> ASP.NET Core 10 ──IRfidReader──> U300 driver
                                     │                              │
                                 EF Core 10                   TCP 9310 (ours)
                                     │                              │
                                 SQL Server                   Java bridge
                                                                    │
                                                            TCP 9160 (vendor)
                                                                    │
                                                              Chainway U300
```

| Project | Responsibility |
|---|---|
| `Warehouse.Domain` | Entities, the validation engine, the gate state machine. No I/O. |
| `Warehouse.Rfid.Abstractions` | `IRfidReader` and its models. No vendor types. |
| `Warehouse.Application` | Documents, gate cycles, inventory, alarms, audit, EPC import. |
| `Warehouse.Infrastructure` | EF Core, SQL Server, migrations, identity, seeding. |
| `Warehouse.Rfid.U300` | Chainway driver, speaking to the Java bridge. |
| `Warehouse.Rfid.Simulation` | Deterministic fake reader. Development only. |
| `Warehouse.Api` | REST API, SignalR hub, hosted services, DI. |
| `Warehouse.Web` | React + TypeScript gate display and admin console. |
| `bridge/u300-bridge` | Java sidecar wrapping the vendor SDK (slave mode). |
| `android/denim-rolls` | DENIM ROLLS app that runs on the U300 itself (host mode). |

**Why a Java sidecar:** the U300 runs Android 11 and Chainway ships a Java-only
SDK. The bridge speaks the vendor API exactly as documented on one side and a
small JSON protocol on the other, so no part of the vendor's wire format is
reimplemented or guessed. See [`docs/U300-INTEGRATION.md`](docs/U300-INTEGRATION.md).

---

## Prerequisites

| | Version | Needed for |
|---|---|---|
| .NET SDK | 10.0+ | Backend |
| SQL Server | 2019+, LocalDB, or Express | Database |
| Node.js | 20+ | Front end |
| JDK | 11+ | Bridge only (not needed for simulation) |

---

## Running it locally

Simulation mode needs no reader and no JDK.

```bash
dotnet build Warehouse.slnx
```

```bash
cd src/Warehouse.Web && npm install && npm run build
```

```bash
dotnet run --project src/Warehouse.Api --environment Development
```

That applies migrations, seeds roles, a gate, a simulated reader and a
bootstrap administrator, then serves the SPA and the API on
<http://localhost:5080>.

Sign in with the credentials from `appsettings.Development.json`
(`admin` / `ChangeMe.Development.1`). A password change is required at first
login.

For front-end work with hot reload, run Vite separately — it proxies `/api`
and `/hubs` to port 5080:

```bash
cd src/Warehouse.Web && npm run dev
```

### Driving the simulated gate

Simulation endpoints exist **only** in the Development environment, and each
one re-checks that the target reader is genuinely a simulator.

```bash
curl -X POST http://localhost:5080/api/simulation/readers/SIM-01/cycle -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{"epcs":["E280116060000200000000 01"],"repeats":5,"holdMs":300}'
```

Or step by step: `…/gpio-on`, `…/tags`, `…/gpio-off`, plus `…/disconnect`,
`…/reconnect` and `…/error` for failure paths.

`scripts/e2e.py` walks the whole Definition of Done — import, document, gate
cycle, commit — plus every failure scenario, against a running instance:

```bash
python scripts/e2e.py
```

---

## Connecting a real U300

**Read [`docs/U300-INTEGRATION.md`](docs/U300-INTEGRATION.md) first**, especially
the GPIO pin table: the U300's terminal differs from the URA4's, and the
commissioning checklist in §8 exists because antenna power is the thing most
likely to cause false alarms.

1. **Build the bridge** (needs a JDK; no build tool required):

   ```bash
   cd bridge/u300-bridge && ./build.sh
   ```

   On Windows use `build.ps1`. The vendor jars are already vendored in `libs/`.

2. **Configure it** — copy `bridge.properties.example` to `bridge.properties`
   and set the reader address, antennas and power.

3. **Run it**, one process per reader:

   ```bash
   cd bridge/u300-bridge && java -jar build/u300-bridge.jar bridge.properties
   ```

4. **Point the backend at it** — in `appsettings.json`, set the reader's
   `Driver` to `U300`, `BridgeHost`/`BridgePort` to the bridge, and
   `Rfid:AllowSimulation` to `false`.

The host refuses to start with simulated readers outside Development, so a
misconfigured production box fails loudly instead of quietly accepting
fabricated reads as real stock movement.

---

## Configuration

Everything is configuration; no EPC, document, gate, reader address, GPIO pin
or threshold is hard-coded.

### Secrets

`Jwt:SigningKey` must be at least 32 characters and is **not** in
`appsettings.json`. Supply it as `Jwt__SigningKey` in the environment, a user
secret, or a key vault. The host refuses to start without it.

```bash
dotnet user-secrets set "Jwt:SigningKey" "$(openssl rand -base64 48)" --project src/Warehouse.Api
```

### Key sections

| Section | Notable settings |
|---|---|
| `Gate` | `CycleTimeoutMs`, `DrainMs`, `MinimumCycleIntervalMs`, `AutoRearmAfterPass`, `BlockCycleWhenReaderOffline`, and the validation policy toggles |
| `Documents` | `MaxEpcsPerDocument` (default 30 — a limit, not an assumption), number prefixes and padding, `MaxRetries` |
| `Alarms` | `DriveGpioOutput`, `RequireSupervisorToResolve` |
| `Rfid` | `AllowSimulation`, then one entry per reader |
| `Rfid:Readers[]` | `ReaderId`, `GateId`, `Driver`, bridge address, reader address, `Antennas`, `AntennaPowerDbm`, `TriggerMode`, `Gpio`, reconnect and timeout settings |
| `Rfid:Readers[]:Gpio` | `GateSignalInput`, `GateSignalActiveHigh`, `AlarmOutput`, `PassOutput`, pulse durations, `DebounceMs` |

### Validation policy

Defaults implement the strict reading: all expected present, nothing else, no
empty read, healthy reader. Sites that load in several passes can set
`Gate:RequireAllExpected` to `false`, which leaves a partial cycle
*in progress* rather than failing it while still reporting the balance.

---

## API

Swagger is served at `/swagger` in Development.

| Area | Endpoints |
|---|---|
| Auth | `POST /api/auth/login`, `POST /api/auth/change-password`, `GET /api/auth/me` |
| Documents | `GET /api/documents`, `GET /api/documents/{id}`, `GET /api/documents/by-number/{n}`, `POST /api/documents/inward`, `POST /api/documents/outward`, `POST /api/documents/{id}/release`, `/cancel`, `/retry` |
| Gates | `GET /api/gates`, `GET /api/gates/{code}/status`, `POST /api/gates/{code}/start`, `/stop`, `GET /api/gates/{code}/cycles`, `GET /api/gates/cycles/{cycleId}/epcs`, `GET /api/gates/{code}/transitions` |
| RFID | `GET /api/rfid/readers`, `GET /api/rfid/readers/{id}/status`, `POST …/connect`, `…/disconnect`, `GET/POST …/gpio` |
| Alarms | `GET /api/alarms`, `GET /api/alarms/active`, `POST /api/alarms/{id}/acknowledge`, `/resolve` |
| EPCs | `GET /api/epcs`, `POST /api/epcs/import` (CSV) |
| Dashboard | `GET /api/dashboard`, `GET /api/dashboard/audit` |
| Health | `GET /health` |

### Real time

SignalR hub at `/hubs/gate`. Displays call `JoinGate(gateCode)`; the dashboard
calls `JoinDashboard()`. Server events: `GateStatusChanged`, `EpcDetected`,
`CycleCompleted`, `AlarmRaised`, `ReaderStatusChanged`, `GpioChanged`.

Nothing polls the database for RFID events. If every display disconnects,
movements are still validated, committed and audited.

### Printing a balance document

The gate display carries a **Print / PDF** button. It renders the same
information as a paper form — header block, balance article list, and the two
totals boxed on the right — and hands it to the browser's print dialog, where
"Save as PDF" produces a file.

The printed form is a separate rendering, not the screen under a print
stylesheet: the wall display is dark, enormous and built to be read across an
aisle, none of which survives contact with paper. It carries the gate, the
detected-versus-expected counts, and who printed it and when, so a sheet found
on a desk later can still be accounted for.

### The DENIM ROLLS reader app

`android/denim-rolls` is an Android app that runs **on the U300**, turning the
reader into a self-contained gate terminal: sign in, pick a document, START,
run the rolls through, STOP. It releases one roll per second, rejects a tag that
is not on the document immediately, and posts the whole session to
`POST /api/documents/{id}/scan-sessions` for the server to rule on.

Two ways to reach the reader, and they are alternatives:

| | Slave mode | Host mode |
|---|---|---|
| Runs where | Server, via the Java bridge | On the reader |
| Trigger | GPIO 12V signal | START / STOP on screen |
| Use when | Unattended gate, hardware-timed | Operator-attended gate |

See [`android/denim-rolls/README.md`](android/denim-rolls/README.md).

### Loading a catalogue

`scripts/load_catalogue.py` imports EPCs from a CSV and generates documents,
deriving product styles from the stock codes. Nothing about the data is
assumed and no EPC, style or document number is written into the script:

```bash
python scripts/load_catalogue.py --csv 400_EPC_with_Stock_Code.csv --password "$PASSWORD"
```

Rows whose EPC is not valid hexadecimal are reported and skipped rather than
guessed at, because a wrong EPC in the catalogue means a real item alarms at
the gate for ever.

### Roles

`Operator` runs gates and reads documents. `Supervisor` also creates, cancels
and releases documents and resolves alarms. `Administrator` additionally
manages readers and GPIO outputs.

---

## Tests

```bash
dotnet test Warehouse.slnx
```

- `Warehouse.Domain.Tests` — the validation failure matrix (all expected,
  missing, unknown, unexpected, duplicate, zero, extra, blocked, unhealthy
  reader), the gate state machine including illegal transitions, EPC
  normalisation.
- `Warehouse.Application.Tests` — full gate cycles end to end against a real
  `GateCycleService` with a simulated reader: pass, alarm paths, reader
  disconnect and recovery, duplicate edges, transactional commit, document
  lifecycle, EPC import.

`scripts/e2e.py` covers the same ground over HTTP against a running instance
with SQL Server, which is what catches serialisation and transaction-strategy
problems that in-process tests cannot see.

---

## Production deployment

1. Provision SQL Server and create a login for the app. Migrations are applied
   at startup; set `Database:MigrateOnStartup` to `false` and run
   `dotnet ef database update` from a deployment step if you prefer.
2. Publish the API. `npm run build` emits the SPA into `Warehouse.Api/wwwroot`,
   so one deployable serves both.
3. Set `Jwt__SigningKey`, `ConnectionStrings__Warehouse`, and
   `Seed__AdminPassword` in the environment.
4. Install the bridge as a service, one per reader, and make it start on boot.
5. Set `Rfid:AllowSimulation` to `false` and confirm every reader has
   `Driver: U300`.
6. Terminate TLS in front of the API. HSTS and HTTPS redirection are on outside
   Development.
7. Logs are written to `logs/warehouse-*.log`, rolling daily, 31 days retained,
   and to the console for the service manager to collect.

### Operational notes

- **Reader offline is a hard stop.** No cycle opens and no transaction
  completes while a reader is unhealthy.
- **A gate holds one active document.** Complete or cancel before releasing
  another.
- **Alarms need a person.** Nothing auto-resolves them.
- **Retries do not re-move stock.** EPCs confirmed by a passed cycle stay
  committed; a retry only re-attempts the balance.
