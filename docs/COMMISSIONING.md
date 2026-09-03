# Commissioning the gate

What to do when the reader is back on the network and the beam sensor is
wired, and what "working" should look like at each step. Written to be
followed at the gate with the reader in front of you.

---

## What is already proven, and what is not

Nothing below is guesswork about the software; it is about the installation.

| Proven away from the hardware | How |
|---|---|
| The gate rules — one roll per beam break, latching, reset | 19 unit tests, `GateCycleTest` |
| Validation, stock movement, replay, dedup | 98 tests across `Warehouse.Domain/Application/Integration.Tests` |
| The reader app's HTTP contract against the API | `scripts/preflight.py` |
| Every message argument and view id in the APK | `scripts/preflight.py` |

| Only provable at the gate | Why |
|---|---|
| Which input the sensor is actually on | Nobody can read a terminal block from source |
| Whether the beam breaks and closes cleanly for one roll | Depends on sensor placement and roll speed |
| Read range and antenna power | Depends on the physical gate |
| Whether the alarm is loud enough | The buzzer cannot be heard over adb |

Run the first two before you start:

```bash
python scripts/preflight.py
```

---

## 1. Network

The reader needs to reach the API, and the API address must be set on the
reader.

- Reader on the warehouse network, note its IP.
- API reachable from it — from a PC on the same LAN, `http://<server>:5080`
  should answer.
- On the reader: account menu → **Reader settings** → **Server address**.

Only an administrator sees Reader settings. Before anyone has signed in, a
long press on the logo opens the same screen.

## 2. Wiring

The U300 uses a 24-way terminal. The inputs are **not** the pins in the
original brief — that table is the URA4's.

| | Pin | | Pin |
|---|---:|---|---:|
| Input 1 | 5 | Input 2 | 17 |
| Input 3 | 6 | Input 4 | 18 |
| Output 1 | 7 | Output 2 | 19 |
| Output 3 | 8 | Output 4 | 20 |
| IO_GND | 9 | VDD5V (3 A) | 12 |

- The beam sensor has **its own supply**. Nothing in the app powers it, and
  START does not switch it. A roll breaking the beam is what puts 12 V on the
  input.
- Input is an optocoupler anode: **12 V maximum, 50 mA maximum**, referenced
  to `IO_GND`.
- The beacon is **open collector** — turning an output on ties that pin to
  `IO_GND`. The beacon takes its supply from `VDD5V` or an external 5–12 V
  source and its *return* goes to the output pin. Wired the other way round it
  will do nothing.

## 3. Find the input, do not assume it

Reader settings → **Gate signal (GPIO)** → **Live inputs**.

All four pins read `HIGH` with nothing wired: an optocoupler input sits high
at rest. **It is the change that tells you something.**

- Have someone break the beam and watch which line moves.
- That number goes in **Gate signal input (1 to 4)**.
- Which way it moves sets **12V on the input means the gate is active** — turn
  it off for a normally closed sensor, where losing the signal marks the gate
  active.

Then turn on **Start and stop from the gate signal** and press Save.

## 4. Check the beacon

Set **Alarm output** to the output the beacon is on (0 disables it). Trigger an
alarm in step 6 and confirm it lights.

## 5. Antenna power

Start at 25 dBm. Step 10 below is what tunes it.

---

## 6. The commissioning run

Open a document, press **START once**, and send rolls through. After START the
operator should not need to touch the screen again unless something is wrong.

| Step | Do | Expect |
|---|---|---|
| 1 | Press START | Band: *Ready. Send the rolls through the gate.* Nothing is read yet |
| 2 | Send a roll that **is** on the document | **One beep.** Its row turns green `READ`, Balance drops by one |
| 3 | Send four more | Same each time. No screen input between rolls |
| 4 | Send a roll that is **not** on the document | **Four fast beeps, repeating** + beacon. Band red, `WRONG ROLL`. Row `NOT ON LIST` pinned to the top. **RESET** appears |
| 5 | Send another roll without resetting | **Nothing happens.** It is not counted. The alarm keeps sounding |
| 6 | Take the wrong roll off, press RESET | Alarm stops, band: *Ready. Send the next roll through.* |
| 7 | Send a roll with the tag **covered by hand or foil** | **Two slow beeps, repeating** + beacon. `NO TAG`. RESET appears |
| 8 | Remove it, press RESET | Back to waiting |
| 9 | Press STOP | Verdict dialog, stock updated on a pass |
| 10 | Repeat with the next aisle loaded | Nothing from the next aisle should be read. If it is, lower the transmit power |

Step 10 is the one that matters. Over-powered antennas reading adjacent stock
is the most common cause of false wrong-roll alarms.

---

## If something is wrong

| Symptom | Look at |
|---|---|
| START does nothing, band stays *Ready. Send the rolls through the gate.* | Correct — it is waiting for the beam. If a roll passes and still nothing happens, the input number or the polarity is wrong. Watch Live inputs |
| Nothing at all reads, `READER NOT READY` | Another program holds the UHF module. `adb shell pm disable-user --user 0 cn.cw.uhf.tcpserver` |
| Every roll raises `NO TAG` | Beam is breaking and closing faster than the read, or transmit power is too low. Raise power; check sensor placement |
| A good roll raises `WRONG ROLL` | That EPC is not on this document. Check the import, or the roll |
| Alarms fire while the gate is idle | Only happens with gate control **off**. Turn it on, or raise *No-tag alarm after* |
| Beacon never lights | Wiring is inverted. See §2 — the output sinks to `IO_GND` |
| Cannot tell the beeps apart | Count them: 1 = good, 4 fast = wrong roll, 2 slow = no tag. `adb logcat -s ReaderController:D \| grep beep` shows them with timestamps |

---

## Settings that matter, and their defaults

| Setting | Default | Notes |
|---|---|---|
| Start and stop from the gate signal | **off** | Turn on once the sensor is wired |
| Gate signal input | 1 | Terminal pin 5 |
| 12V means the gate is active | on | Off for a normally closed sensor |
| Transmit power | 25 dBm | Tune with step 10 |
| Antenna port | 1 | |
| Alarm output | 1 | 0 disables the beacon |
| Read interval | 1000 ms | Ignored in gate mode — the beam paces it |
| No-tag alarm after | 1000 ms | Only used with gate control off |
| Alarm length | 4000 ms | Only used for the end-of-load verdict; gate alarms latch |

Nothing here is compiled into the APK. The same file runs at every gate.
