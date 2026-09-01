# U300 bridge

A small Java service that exposes one Chainway U300 reader to the warehouse
backend over a line-delimited JSON protocol on TCP.

It exists because the U300 runs Android 11 and Chainway ships a **Java-only**
SDK. The bridge speaks `com.rscja.deviceapi` on one side, exactly as the
vendor documents it, and our own JSON on the other. No part of the vendor's
wire protocol is reimplemented.

## Build

Needs a JDK 11 or later. No Gradle or Maven required.

First populate the vendor SDK, which is **not** in source control — the U300
manual grants a non-transferable, non-exclusive licence and states that no
right to copy the licensed program is granted, so redistributing those jars
would be sublicensing them:

```powershell
.\scriptsetch-vendor-libs.ps1
```

That extracts them from your own copy of `U300.rar` into `libs/` and
`native/`. Then build:

```bash
./build.sh
```

```powershell
.\build.ps1
```

Both produce a self-contained `build/` folder — the jar plus copies of `libs/`
and `native/`. Copy that folder to the target host and run it; nothing else is
needed but a JRE.

`Class-Path` entries in a jar manifest resolve relative to the jar, not the
working directory, which is why the dependencies are copied in beside it rather
than referenced a level up.

## Configure

```bash
cp bridge.properties.example bridge.properties
```

Every property can be overridden by an environment variable using the
uppercased name with dots replaced by underscores, so `reader.host` becomes
`READER_HOST`. That makes the bridge deployable as a container or a service
without editing files on the host.

## Run

```bash
java -jar build/u300-bridge.jar bridge.properties
```

One process per reader. Run several instances on different `listen.port`
values to serve several gates.

Set `BRIDGE_LOG_LEVEL=FINE` for verbose SDK tracing.

## Serial ports

RS-232 needs the RXTX native library on `java.library.path`. The binaries are
in `native/`:

```bash
java -Djava.library.path=native -jar build/u300-bridge.jar bridge.properties
```

Ethernet needs nothing extra.

## What it does

- Opens the reader with `init(ip, port)` or `init(comPort)` and keeps it open
  while at least one client is attached.
- Streams tag reads from `setInventoryCallback`, GPI edges from
  `setGPIStateCallback`, and connection changes from
  `setConnectionStateCallback`.
- Reports only genuine GPI *edges*; a repeated level is not a second event.
- Answers `readGpi` from a cache of those callbacks, because `inputStatus()`
  is package-private in the host jar (see `docs/U300-INTEGRATION.md` §4).
- Releases the reader when the last client disconnects, so a restarted backend
  never fights a stale session for the device.
- Never lets an SDK exception escape — including `UnsatisfiedLinkError` from a
  missing native library.

## Protocol

See `docs/U300-INTEGRATION.md` §5.
