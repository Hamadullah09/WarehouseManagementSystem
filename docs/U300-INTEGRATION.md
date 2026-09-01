# Chainway U300 — SDK inspection and integration notes

Everything here comes from the material shipped in `U300.rar`: the datasheet,
the user manual, the Android fixed-reader integration guide, the web-client
guide, the `DeviceAPI_ver20231208_release.aar` javadoc, and the
`UHFAPI20221125.jar` host SDK together with its `URA4Demo` source. Where the
package is silent, this document says so rather than guessing.

---

## 1. What the U300 actually is

**It is not a PC-peripheral reader. It is an Android 11 computer with a UHF
radio in it.**

| | |
|---|---|
| Vendor | Shenzhen Chainway Information Technology (`com.rscja.*`) |
| Model | U300-4 / U300-8 fixed RFID reader |
| OS | Android 11, quad-core 2.0 GHz, 2 GB RAM / 16 GB ROM |
| Radio | Impinj E710, EPC Class 1 Gen 2 / ISO 18000-6C, Gen2X |
| Power out | 1 W (30 dBm, 5–30 dBm adjustable); 2 W option (33 dBm) |
| Read rate | 950+ tags/sec, RSSI supported |
| Antennas | 4 or 8 ports, reverse-polarity TNC female |
| Interfaces | RJ45, RS-232, USB host + device, HDMI 720p, WiFi, BT 4.0 |
| GPIO | 4 isolated optocoupler inputs, 4 optocoupler outputs, 1 Wiegand |
| Power in | DC 10–24 V, PoE (802.3af), PoE+ (802.3at) |
| SDK language | **Java only.** No C, C++, .NET or Python SDK is supplied. |

---

## 2. The two vendor-supported integration paths

The integration guide splits deployment into **host mode** and **slave mode**.

### Host mode (embedded)

An APK built against `DeviceAPI_ver20231208_release.aar` runs **on the reader
itself**. Full access to UHF and GPIO in-process, lowest possible latency.
Costs an Android toolchain and APK lifecycle management on every unit.

### Slave mode (what this system uses)

The reader runs a built-in transmission service, enabled by default at
power-on, and a host drives it over the network or RS-232.

| Service | Default port | Notes |
|---|---|---|
| RAW data | **9160** | Hexadecimal protocol; this is what `UHFAPI20221125.jar` speaks |
| JSON data | 9260 | Documented in `UHFService JSON format V1.1.pdf` — **not supplied** |
| HTTP / web client | 8080 | `admin` / `123456` by default |
| MQTT | broker-defined | Publishes tag + heartbeat topics, subscribes for commands |

Default device address is `192.168.1.100`. Serial is RS-232 at 115200 baud.

**Why slave mode via the vendor jar:** it is the only path in the shipped
package that is both fully documented and reachable from a .NET backend
without inventing protocol. See §7 for the gap that rules out the alternative.

---

## 3. Confirmed host-SDK API

Taken from `UHFAPI20221125.jar` (class-file inspection) and cross-checked
against the vendor's own `URA4Demo` source, which compiles against it.

### Type hierarchy

```
IUHFAx  (common supertype — everything except the output pins)
  ├── IMultipleAntenna : setPower, getPower, getPowerAll, setAntenna, getAntenna,
  │                      setAntennaWorkTime, getAntennaWorkTime
  ├── IUHF            : init, free, startInventoryTag, stopInventory,
  │                      inventorySingleTag, readTagFromBuffer, setFilter,
  │                      readData, writeData, eraseData, killTag, lockMem,
  │                      getVersion, getTemperature, setEPCMode, ...
  └── (own)           : setInventoryCallback, setGPIStateCallback,
                        setConnectionStateCallback, setGPIStateReverse,
                        getAndroidDeviceHardwareVersion, getReaderInfo,
                        getReaderCurrentIp, setEthernetConfigInfo,
                        setTcpServicePort, buzzerOn/Off, rebootDevice, ...

IUHFA4 : IUHFAx  → output1..4OnAndOff, outputWgData0/1OnAndOff, outputOnAndOff
IUHFA8 : IUHFAx  → output3/4OnAndOff, outputOnAndOff

RFIDWithUHFNetworkA4/A8   extends RFIDWithUHFNetworkAx   → init(ip, port), init(ip), free()
RFIDWithUHFSerialPortA4/A8 extends RFIDWithUHFSerialPortAxBase → init(comPort), free()
```

### Callbacks (push, not poll)

```java
setConnectionStateCallback(ConnectionStateCallback)  // getState(ConnectionState, Object)
setInventoryCallback(IUHFInventoryCallback)          // callback(UHFTAGInfo)
setGPIStateCallback(IGPIStateCallback)               // callback(List<GPIStateEntity>)
```

`ConnectionState` values are `CONNECTED`, `DISCONNECTED`, `CONNTCTING`
(the vendor's spelling, not a typo here).

### Entities

`UHFTAGInfo` — `getEPC()`, `getTid()`, `getUser()`, `getPc()`, `getRssi()`,
`getAnt()`, `getCount()`. **RSSI and antenna are returned as strings.**

`GPIStateEntity` — constants `GPI1`..`GPI4`; accessors **`getGPIName()`**,
**`getGPIState()`** (int).

`GPOEntity` — constants `GPO1`..`GPO4`, `WiegandData0`, `WiegandData1`;
accessors **`getGpoName()`**, **`getGpoState()`**; constructor
`GPOEntity(String name, int state)`.

> The casing genuinely differs between the two entities — `getGPIName` but
> `getGpoName`. The Android AAR javadoc documents `getGpiName`, which does not
> exist in the host jar. Code written from the javadoc will not compile against
> the jar.

### Constants

`Bank_RESERVED=0`, `Bank_EPC=1`, `Bank_TID=2`, `Bank_USER=3`;
`LockBank_KILL=16`, `LockBank_ACCESS=32`, `LockBank_EPC=48`,
`LockBank_TID=64`, `LockBank_USER=80`.

---

## 4. GPIO — corrections to the supplied pin table

**The 16-pin table in the original brief is the URA4 terminal, not the U300's.**
The U300 uses a 24-way terminal (integration guide table 4.3.1.3):

| Pin | Definition | Pin | Definition |
|----:|---|----:|---|
| 1 | BOOT_CTRL (reserved) | 13 | SYS_GND |
| 2 | RST (reset) | 14 | SYS_GND |
| 3 | SYS_GND | 15 | NC |
| 4 | VOUT POWER DC IN | 16 | NC |
| **5** | **Input 1** (optocoupler anode, 12 V max, 50 mA) | **17** | **Input 2** |
| **6** | **Input 3** | **18** | **Input 4** |
| **7** | **Output 1** (optocoupler OC) | **19** | **Output 2** |
| **8** | **Output 3** | **20** | **Output 4** |
| 9 | IO_GND (IO reference) | 21 | Data 0 (Wiegand, 5 V) |
| 10 | SYS_GND | 22 | Data 1 (Wiegand, 5 V) |
| 11 | SYS_GND | 23 | MTX_3V3 (reader TX → peripheral RX) |
| 12 | VDD5V / 3 A | 24 | MRX_3V3 (reader RX → peripheral TX) |

Wiring the 12 V gate signal to "pin 1" per the original table would land on
`BOOT_CTRL`, not `Input 1`.

**Output electrical behaviour:** open collector. Quoting the guide, *"when
program output 1, IO_GND will conduct with Output"* — the transistor sinks the
output pin to `IO_GND`. Relays and beacons must be wired accordingly, with the
load's return on the output pin and its supply from `VDD5V` or an external
5–12 V source.

**Input wiring:** optocoupler diode anode, 12 V maximum, 50 mA maximum. Either
externally powered (5–12 V) or fed from the reader's own `VDD5V`.

### Reading inputs: push only

`inputStatus()` is **public in the Android AAR** but **package-private in the
host jar**, where it returns `int[]` rather than `List<GPIStateEntity>`. The
vendor's own desktop demo therefore never calls it and reads inputs solely
through `setGPIStateCallback`.

The bridge follows the vendor demo: it caches the latest level per pin as
callbacks arrive and answers `readGpi` from that cache. A pin that has never
fired is reported as absent rather than assumed low. Gate logic depends only on
*edges*, which the callback delivers reliably.

### Firmware trigger mode

The reader can start and stop inventory **itself** on a GPI edge (integration
guide §4.3.3.2), with a configurable stop delay. Three modes exist:

- **Command** — host issues start/stop. Default; what `TriggerMode: Command` uses.
- **Trigger** — firmware starts/stops on the input edge. Hardware-timed and
  immune to network latency. Start and stop conditions must be opposites.
- **Automatic** — inventory runs continuously from power-on.

For a high-throughput gate, trigger mode is the more robust choice: set it in
the reader's web client and set `TriggerMode: Trigger` in configuration so the
backend brackets the cycle without issuing redundant start/stop commands.

---

## 5. How this system is wired together

```
┌────────────────────────────────────────┐
│  Warehouse Web UI (React + TypeScript) │
│  gate display · admin dashboard        │
└──────────────────┬─────────────────────┘
                   │  REST + SignalR
┌──────────────────▼─────────────────────┐
│  Warehouse.Api (ASP.NET Core 10)       │
│  Warehouse.Application                 │
│    DocumentService · GateCycleService  │
│    ValidationEngine · InventoryService │
│    AlarmService · AuditService         │
│  Warehouse.Infrastructure (EF Core 10) │
└──────────────────┬─────────────────────┘
                   │  IRfidReader  (vendor-neutral)
┌──────────────────▼─────────────────────┐
│  Warehouse.Rfid.U300                   │
│  newline-delimited JSON over TCP 9310  │
└──────────────────┬─────────────────────┘
┌──────────────────▼─────────────────────┐
│  u300-bridge (Java)                    │
│  com.rscja.deviceapi — vendor SDK      │
└──────────────────┬─────────────────────┘
                   │  vendor RAW protocol, TCP 9160
┌──────────────────▼─────────────────────┐
│  Chainway U300 — EPC reader + GPIO     │
└────────────────────────────────────────┘
```

Nothing above `Warehouse.Rfid.U300` references a Chainway type. Replacing the
reader means adding one `IRfidReader` implementation and changing
configuration.

### Bridge protocol

One JSON object per line, UTF-8. Commands carry `id` and get exactly one `ack`;
everything else is unsolicited.

**Commands** (adapter → bridge)

```json
{"id":"…","cmd":"connect"}
{"id":"…","cmd":"disconnect"}
{"id":"…","cmd":"status"}
{"id":"…","cmd":"startInventory"}
{"id":"…","cmd":"stopInventory"}
{"id":"…","cmd":"readGpi"}
{"id":"…","cmd":"setGpo","outputs":[{"pin":"GPO1","high":true}]}
{"id":"…","cmd":"setAntennaPower","power":{"1":30}}
{"id":"…","cmd":"ping"}
```

**Events** (bridge → adapter)

```json
{"type":"ack","id":"…","ok":true,"inputs":[{"pin":"GPI1","state":1}]}
{"type":"tag","epc":"E280…","tid":null,"rssi":"-52","ant":"1","count":3,"ts":1737000000000}
{"type":"gpi","pin":"GPI1","state":"1","ts":1737000000000}
{"type":"state","connection":"CONNECTED","reason":null,"ts":…}
{"type":"error","op":"startInventoryTag","message":"…","code":"START_FAILED","ts":…}
{"type":"heartbeat","connection":"CONNECTED","inventorying":true,"ts":…}
```

Command-to-SDK mapping is one-to-one and listed in `ReaderSession.java`.

---

## 6. Gate cycle

```
12 V applied to Input 1
        │
        ├─ GPI edge pushed by setGPIStateCallback
        ├─ cycle record created, bound to the gate's active document
        ├─ startInventoryTag()            (command mode only)
        └─ EPC reads accumulate in memory, deduplicated by EPC
12 V removed
        │
        ├─ stopInventory()                (command mode only)
        ├─ drain window for in-flight reads (configurable, default 750 ms)
        ├─ EPC set frozen
        ├─ ValidationEngine: expected vs detected vs catalogue
        ├─ PASS  → inventory transaction commits, GPO pass indicator pulses
        └─ FAIL  → alarms raised, GPO alarm indicator pulses, nothing moves
```

Reads arriving between the electrical edge and the cycle record existing are
staged and replayed into the cycle, so a fast conveyor cannot lose the first
carton to a database round trip.

---

## 7. Known gaps in the supplied package

1. **The JSON protocol specification is missing.** Both
   `UHFService JSON format V1.1.pdf` and `读写器TCP JSON格式20240118.docx` are
   referenced by the guides but neither is in the archive, and the docx is not
   embedded in the PDF either. Only two commands appear anywhere in the shipped
   material:

   ```json
   {"type":"Reader-startInventoryRequest"}
   {"type":"Reader-stopInventoryRequest"}
   ```

   A direct .NET client for port 9260 cannot be written from what is supplied
   without guessing. Request the document from Chainway if that path is wanted;
   `Warehouse.Rfid.U300` can then gain a second transport behind the same
   interface.

2. **No RAW (9160) protocol document.** The guide says one exists in the SDK
   package under `slave mode development`. It is not present. This does not
   block anything — the vendor jar implements that protocol — but it does mean
   the RAW protocol should not be reimplemented by hand.

3. **`inputStatus()` is not usable from the host jar** (§4). GPI is push-only.

4. **Model naming.** The host SDK exposes `…A4` and `…A8` classes. U300-4 maps
   to the A4 classes and U300-8 to the A8 classes; the integration guide covers
   URA4, URA8, U300-4 and U300-8 as one family. Set `reader.eightPort=true` in
   `bridge.properties` for an 8-port unit. **Confirm against the actual unit on
   first connect** — `getReaderInfo().getAntennaNumber()` reports the true port
   count.

---

## 8. Commissioning checklist

Do these in order the first time a physical reader is connected.

1. Power the reader, connect RJ45, wait for the startup beeps (~1 minute).
2. Confirm reachability: `ping 192.168.1.100`. If the IP was changed and
   forgotten, connect RS-232 or HDMI to recover it (FAQ §5.1).
3. Open `http://192.168.1.100:8080` (`admin` / `123456`). **Change that
   password.**
4. In the web client, confirm: enabled antennas, transmit power, frequency band
   for your region, and inventory mode (EPC only is fastest).
5. On the GPIO page, watch the GPI status while a colleague applies the 12 V
   gate signal. Confirm the pin that changes is the one wired to `GateSignalInput`.
6. Toggle each GPO from the web client and confirm the beacon and pass lamp
   respond. Note which output drives which.
7. Decide command mode or trigger mode; if trigger mode, set the start/stop
   conditions there and set `TriggerMode: Trigger` in `appsettings`.
8. Start the bridge; confirm `init(...) -> true` in its log.
9. Start the API; confirm the reader shows **ONLINE** on the admin dashboard.
10. Run one loaded pallet through with a known document and compare the cycle's
    `rawReadCount` and `detectedEpcCount` against what physically passed. Tune
    antenna power until every tag is seen and nothing from the next aisle is.

Step 10 is the one that matters. Over-powered antennas reading adjacent stock
is the most common cause of false unexpected-EPC alarms.

---

## 9. Source material

| File | What it gave us |
|---|---|
| `U300-EN DATASHEET.pdf` | Hardware envelope, Java-only SDK, GPIO count |
| `USER MANUAL.pdf` | On-device demo, antenna and power operation |
| `Android Fixed Reader Integration…20240806.pdf` | Host/slave modes, ports 9160/9260/8080, MQTT, **U300 24-pin GPIO table**, trigger mode |
| `Android Fixed Reader Web user description V1.0.0CN240912.pdf` | Web client, service-mode configuration, TCP port configuration |
| `API_Ver20231208.rar` | `DeviceAPI…aar` + javadoc (on-device API) |
| `Demo-URA4-JAVA_EN.rar` | `UHFAPI20221125.jar` (**host SDK, used here**) + working demo source |
| `Demo-uhf-fixed_as.rar` | Android Studio demo for host mode |
