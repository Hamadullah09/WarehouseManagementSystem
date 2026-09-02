# DENIM ROLLS

Android app for the **Chainway U300**, branded for SMA Technology. It runs on
the reader itself and turns it into a self-contained gate terminal: sign in,
pick a document, press START, run the rolls through, press STOP.

## What it does

1. **Sign in** against the warehouse API. Every movement is attributed to the
   person signed in here.
2. **List documents** — inward and outward, workable ones first.
3. **Open a document** and show what the operator needs: document number, user,
   direction, total articles, total unit quantity, balance articles, balance
   quantity, and every roll with its stock code.
4. **START / STOP.** Between them the reader inventories continuously and the
   app releases **one roll per second** to the screen.
5. **Immediate rejection.** A tag that is not on the document raises `INVALID`
   on screen with the offending EPC, sounds the buzzer and pulses the alarm
   output. A session that read nothing raises the no-tag alarm.
6. **Submit.** On STOP the whole session goes to the server, which re-validates
   and is the only thing that moves stock.

## Why one roll per second

The gate procedure admits one roll at a time, but the reader reports the same
tag many times a second and several tags at once. Slowing the reader down would
only make it miss tags. Instead every *distinct* tag is queued as it arrives and
released one per interval, so nothing is lost and the operator sees them at the
pace the procedure expects. The interval is a setting, not a constant.

## Local check, server decision

The app validates against the document as reads arrive, so the operator is told
a roll does not belong while the load is still in front of them. That check is a
courtesy. On STOP the session is posted to
`POST /api/documents/{id}/scan-sessions`, and the server re-runs the same
validation engine against the database. A device with a stale EPC list, a
tampered build or a clock problem cannot talk the warehouse into accepting a bad
movement.

Submissions carry a `sessionKey`. If the network drops after the server has
committed, the device retries and gets the *original verdict* back rather than
moving the stock twice.

## Nothing is hardcoded

Server address, gate code, device identity, read interval, transmit power,
antenna port and which optocoupler drives the beacon are all set on the device,
under **Settings**. The APK is identical in every warehouse.

## Building

Needs JDK 17+, the Android SDK (platform 34, build-tools 34) and Gradle 8.7.

The Chainway SDK is **not** in source control — their licence is
non-transferable and grants no right to copy. Populate it from your own U300
media first:

```powershell
..\..\scripts\fetch-vendor-libs.ps1
```

Then copy the AAR into `app/libs/`, point `local.properties` at your SDK, and:

```bash
gradle assembleRelease
```

The release build is signed when `keystore.properties` is present and unsigned
otherwise, so a clone builds without anyone's signing material.

## Installing on the reader

```bash
adb connect <reader-ip>:5555
```

```bash
adb install -r app/build/outputs/apk/release/app-release.apk
```

USB works too: enable **Settings → USB → Connect to PC** on the reader, then
`adb install`.

On first run, open **Settings** and set the server address and gate code before
signing in.

## What it talks to

| Call | Purpose |
|---|---|
| `POST /api/auth/login` | Sign in, obtain a bearer token |
| `GET /api/documents` | The work list |
| `GET /api/documents/{id}` | One document with EPCs and stock codes |
| `POST /api/documents/{id}/scan-sessions` | Submit a completed session |

## Running the gate

Two ways, chosen by **Start and stop from the gate signal** in Reader settings.

**Buttons (default).** START opens the session and the reader reads until STOP.
Distinct tags are released one per *Read interval* so the list fills at a pace a
person can follow, and *No-tag alarm after* raises the alarm on a quiet spell.

**Gate signal.** START only opens the session; the reading is driven by the
gate's own sensor:

```
START  ──▶ session open, gate powered (if an output is configured)
             │
             ▼
   12V on the input  ──▶ inventory on, read this roll
   12V drops         ──▶ inventory off, judge the roll
                           no tag  ──▶ alarm for "Alarm length"
                           not on the document ──▶ same alarm, when it was read
             │
             ▼  (repeats for every roll)
STOP   ──▶ session closed, gate unpowered, the whole load sent to the server
```

Pacing is switched off in this mode: the gate already admits one roll at a
time, and holding tags back on top of a signal that lasts a second or two
would only lose them. The GPI is polled at 150 ms, since the window that
brackets a single roll is far shorter than one that bracketed a whole session.

The buttons bound the session in both modes, so the gate can do nothing unless
a document is open on the screen. Where the sensor is fed from one of the
reader's outputs (*Power the gate sensor from output*), START and STOP switch
that supply, making it true electrically as well as in software.

## SDK calls used

All from `com.rscja.deviceapi.RFIDWithUHFA4`, the on-device DeviceAPI:

```
getInstance() / init(context)     open the UHF module
setPower(dBm) / setEPCMode()      configure
setInventoryCallback(...)         receive tags
startInventoryTag() / stopInventory()
output1On() .. output4Off()       drive the alarm beacon
buzzer() / led() / successNotify()
free()                            release
```

Note this differs from the host-side jar used by the desktop bridge: on-device,
`inputStatus()` is public and GPI is **polled**, whereas the host SDK pushes GPI
through a callback and keeps `inputStatus()` package-private. The two are not
interchangeable.
