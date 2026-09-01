"""End-to-end walk of the Definition of Done (brief section 48), over HTTP."""
import io
import json
import time
import urllib.request
import urllib.error

BASE = "http://localhost:5080"
TOKEN = None
FAILURES = []


def call(method, path, body=None, raw=None, content_type=None):
    url = BASE + path
    data = None
    headers = {}

    if TOKEN:
        headers["Authorization"] = "Bearer " + TOKEN

    if raw is not None:
        data = raw
        headers["Content-Type"] = content_type
    elif body is not None:
        data = json.dumps(body).encode()
        headers["Content-Type"] = "application/json"

    req = urllib.request.Request(url, data=data, headers=headers, method=method)

    try:
        with urllib.request.urlopen(req, timeout=30) as r:
            text = r.read().decode()
            return r.status, (json.loads(text) if text else None)
    except urllib.error.HTTPError as e:
        text = e.read().decode()
        try:
            return e.code, json.loads(text)
        except Exception:
            return e.code, text


def check(label, condition, detail=""):
    mark = "PASS" if condition else "FAIL"
    print(f"  [{mark}] {label}" + (f" -- {detail}" if detail and not condition else ""))
    if not condition:
        FAILURES.append(label)
    return condition


def epc(n):
    return f"E2801160600002{n:010d}"


print("=" * 72)
print("STEP 1  Authenticate")
status, body = call("POST", "/api/auth/login",
                    {"userName": "admin", "password": "ChangeMe.Development.1"})
check("login returns a token", status == 200 and "token" in (body or {}), f"{status} {body}")
TOKEN = body["token"]
print(f"        roles: {body['roles']}, must change password: {body['mustChangePassword']}")

print()
print("STEP 2  Import the EPC catalogue")
rows = ["Epc,ItemCode,ItemName,CartonNumber,UnitQuantity,Status"]
for i in range(1, 451):
    rows.append(f"{epc(i)},SKU-{(i % 7) + 1},Widget {(i % 7) + 1},CTN-{i:05d},4,Registered")
# Two rows that must be rejected.
rows.append("NOTHEXVALUE,SKU-1,Bad row,CTN-BAD,4,Registered")
rows.append(f"{epc(1)},SKU-1,Duplicate,CTN-DUP,4,Registered")
csv = "\n".join(rows).encode()

boundary = "----warehouse-e2e"
payload = (
    f"--{boundary}\r\n"
    'Content-Disposition: form-data; name="file"; filename="epcs.csv"\r\n'
    "Content-Type: text/csv\r\n\r\n"
).encode() + csv + f"\r\n--{boundary}--\r\n".encode()

status, body = call("POST", "/api/epcs/import?updateExisting=false", raw=payload,
                    content_type=f"multipart/form-data; boundary={boundary}")
check("import succeeds", status == 200, f"{status} {body}")
check("450 EPCs registered", body["imported"] + body["skipped"] == 450,
      f"imported={body['imported']} skipped={body['skipped']}")
check("2 bad rows rejected", len(body["errors"]) == 2, str(body.get("errors")))
print(f"        {body['imported']} imported, {len(body['errors'])} rejected")

print()
print("STEP 3  Create an INWARD document for 30 EPCs and release it to GATE-01")
inward = [epc(i) for i in range(1, 31)]
status, doc = call("POST", "/api/documents/inward",
                   {"epcs": inward, "gateCode": "GATE-01", "reference": "E2E-1"})
check("document created", status == 201, f"{status} {doc}")
check("number is database generated", doc["documentNumber"].startswith("IN-"), doc.get("documentNumber"))
check("30 expected articles", doc["expectedArticles"] == 30)
check("120 expected units", doc["expectedQuantity"] == 120)
check("released to the gate", doc["status"] == "Released")
print(f"        {doc['documentNumber']}: {doc['expectedArticles']} articles / {doc['expectedQuantity']} units")
doc_id = doc["id"]

print()
print("STEP 4  Arm the gate")
status, gate = call("POST", "/api/gates/GATE-01/start")
check("gate armed", status == 200 and gate["state"] == "WaitingForGate", f"{status} {gate}")

print()
print("STEP 5  Run the gate cycle: 12V ON, tags read, 12V OFF")
status, _ = call("POST", "/api/simulation/readers/SIM-01/gpio-on")
check("gate input active", status == 204, str(status))

# Each carton is seen several times, as a real antenna would report it.
status, _ = call("POST", "/api/simulation/readers/SIM-01/tags", {"epcs": inward, "repeats": 5})
check("tags emitted", status == 204, str(status))

status, _ = call("POST", "/api/simulation/readers/SIM-01/gpio-off")
check("gate input cleared", status == 204, str(status))
time.sleep(2.5)

print()
print("STEP 6  Verify the movement was validated and committed")
status, doc = call("GET", f"/api/documents/{doc_id}")
check("document completed", doc["status"] == "Completed", doc.get("status"))
check("30 articles detected", doc["detectedArticles"] == 30, str(doc.get("detectedArticles")))
check("120 units detected", doc["detectedQuantity"] == 120, str(doc.get("detectedQuantity")))
check("balance is zero", doc["balanceArticles"] == 0 and doc["balanceQuantity"] == 0)

status, cycles = call("GET", "/api/gates/GATE-01/cycles")
cycle = cycles[0]
check("cycle id is database generated", cycle["cycleId"].startswith("GC-"), cycle.get("cycleId"))
check("cycle passed", cycle["validationResult"] == "Pass", str(cycle.get("validationResult")))
check("inventory committed", cycle["inventoryCommitted"] is True)
check("150 raw reads deduplicated to 30", cycle["rawReadCount"] == 150 and cycle["detectedEpcCount"] == 30,
      f"raw={cycle['rawReadCount']} distinct={cycle['detectedEpcCount']}")
print(f"        {cycle['cycleId']}: {cycle['validationSummary']}")

status, dash = call("GET", "/api/dashboard")
check("stock reflects the movement", dash["epcsInStock"] == 30, str(dash.get("epcsInStock")))

print()
print("=" * 72)
print("FAILURE SCENARIOS")

def clear_gate():
    """A gate may hold only one active document, so retire the previous one."""
    _, page = call("GET", "/api/documents?gateCode=GATE-01&pageSize=100")
    for d in page["items"]:
        if d["status"] in ("Released", "InProgress"):
            call("POST", f"/api/documents/{d['id']}/cancel", {"reason": "E2E scenario reset"})


def run_cycle(epcs, repeats=3):
    call("POST", "/api/gates/GATE-01/start")
    call("POST", "/api/simulation/readers/SIM-01/gpio-on")
    if epcs:
        call("POST", "/api/simulation/readers/SIM-01/tags", {"epcs": epcs, "repeats": repeats})
    call("POST", "/api/simulation/readers/SIM-01/gpio-off")
    time.sleep(2.0)


def latest_alarms():
    _, alarms = call("GET", "/api/alarms?take=20")
    return alarms


print()
print("SCENARIO A  Unknown EPC")
batch = [epc(i) for i in range(101, 106)]
clear_gate()
status, doc_a = call("POST", "/api/documents/inward", {"epcs": batch, "gateCode": "GATE-01"})
check("document created", status == 201, f"{status} {doc_a}")
run_cycle(batch + ["DEADBEEFDEADBEEF"])
alarms = latest_alarms()
unknown = [a for a in alarms if a["alarmType"] == "UnknownEpc"]
check("UNKNOWN_EPC alarm raised", len(unknown) > 0)
check("offending EPC recorded", unknown and unknown[0]["epc"] == "DEADBEEFDEADBEEF",
      unknown[0]["epc"] if unknown else "none")
_, doc_a = call("GET", f"/api/documents/{doc_a['id']}")
check("document not completed", doc_a["status"] != "Completed", doc_a["status"])
_, cycles = call("GET", "/api/gates/GATE-01/cycles")
check("inventory not committed", cycles[0]["inventoryCommitted"] is False)

print()
print("SCENARIO B  Known but unexpected EPC")
batch = [epc(i) for i in range(111, 116)]
stray = epc(200)
clear_gate()
status, doc_b = call("POST", "/api/documents/inward", {"epcs": batch, "gateCode": "GATE-01"})
check("document created", status == 201)
run_cycle(batch + [stray])
alarms = latest_alarms()
unexpected = [a for a in alarms if a["alarmType"] == "UnexpectedEpc"]
check("UNEXPECTED_EPC alarm raised", len(unexpected) > 0)
check("distinguished from unknown", unexpected and unexpected[0]["epc"] == stray,
      unexpected[0]["epc"] if unexpected else "none")

print()
print("SCENARIO C  Missing EPC")
batch = [epc(i) for i in range(121, 131)]
clear_gate()
status, doc_c = call("POST", "/api/documents/inward", {"epcs": batch, "gateCode": "GATE-01"})
check("document created", status == 201)
run_cycle(batch[:9])
alarms = latest_alarms()
missing = [a for a in alarms if a["alarmType"] == "MissingEpc"]
check("MISSING_EPC alarm raised", len(missing) > 0)
check("shortfall reported", missing and "Expected 10" in missing[0]["message"] and "detected 9" in missing[0]["message"],
      missing[0]["message"] if missing else "none")
check("missing EPC named", missing and missing[0]["epc"] == batch[9],
      missing[0]["epc"] if missing else "none")

print()
print("SCENARIO D  Item with no RFID tag")
batch = [epc(i) for i in range(141, 144)]
clear_gate()
status, doc_d = call("POST", "/api/documents/inward", {"epcs": batch, "gateCode": "GATE-01"})
check("document created", status == 201)
run_cycle([])
alarms = latest_alarms()
noepc = [a for a in alarms if a["alarmType"] == "NoEpc"]
check("NO_EPC alarm raised", len(noepc) > 0)
check("message names the cause", noepc and "without an RFID tag" in noepc[0]["message"],
      noepc[0]["message"] if noepc else "none")

print()
print("SCENARIO E  Reader offline blocks the gate")
batch = [epc(i) for i in range(151, 154)]
clear_gate()
status, doc_e = call("POST", "/api/documents/inward", {"epcs": batch, "gateCode": "GATE-01"})
check("document created", status == 201)
call("POST", "/api/gates/GATE-01/start")
_, before = call("GET", "/api/gates/GATE-01/cycles")
call("POST", "/api/simulation/readers/SIM-01/disconnect?reason=E2E+cable+pull")
time.sleep(1.0)
_, gate = call("GET", "/api/gates/GATE-01/status")
check("gate reports reader offline", gate["state"] == "ReaderDisconnected", gate.get("state"))
call("POST", "/api/simulation/readers/SIM-01/gpio-on")
call("POST", "/api/simulation/readers/SIM-01/tags", {"epcs": batch, "repeats": 2})
call("POST", "/api/simulation/readers/SIM-01/gpio-off")
time.sleep(1.5)
_, after = call("GET", "/api/gates/GATE-01/cycles")
check("no cycle opened while offline", len(after) == len(before), f"{len(before)} -> {len(after)}")
_, doc_e = call("GET", f"/api/documents/{doc_e['id']}")
check("no stock moved", doc_e["detectedArticles"] == 0, str(doc_e["detectedArticles"]))

print()
print("SCENARIO F  Reader recovers and the retry succeeds")
call("POST", "/api/simulation/readers/SIM-01/reconnect")
time.sleep(1.0)
_, gate = call("GET", "/api/gates/GATE-01/status")
check("reader back online", gate["readerOnline"] is True, str(gate.get("readerOnline")))
run_cycle(batch)
_, doc_e = call("GET", f"/api/documents/{doc_e['id']}")
check("document completed on retry", doc_e["status"] == "Completed", doc_e["status"])

print()
print("SCENARIO G  Duplicate gate event")
batch = [epc(i) for i in range(161, 164)]
clear_gate()
status, doc_g = call("POST", "/api/documents/inward", {"epcs": batch, "gateCode": "GATE-01"})
check("document created", status == 201)
call("POST", "/api/gates/GATE-01/start")
_, before = call("GET", "/api/gates/GATE-01/cycles")
call("POST", "/api/simulation/readers/SIM-01/gpio-on")
call("POST", "/api/simulation/readers/SIM-01/gpio-on")  # contact bounce
call("POST", "/api/simulation/readers/SIM-01/tags", {"epcs": batch, "repeats": 3})
call("POST", "/api/simulation/readers/SIM-01/gpio-off")
time.sleep(2.0)
_, after = call("GET", "/api/gates/GATE-01/cycles")
check("bounce produced exactly one cycle", len(after) == len(before) + 1, f"{len(before)} -> {len(after)}")
_, doc_g = call("GET", f"/api/documents/{doc_g['id']}")
check("stock counted once", doc_g["detectedArticles"] == 3, str(doc_g["detectedArticles"]))

print()
print("STEP 7  Audit trail")
_, audit = call("GET", "/api/dashboard/audit?take=500")
actions = {a["action"] for a in audit}
for required in ["DocumentCreated", "GpioOn", "GateCycleStarted", "GateCycleCompleted",
                 "InventoryCommitted", "DocumentCompleted", "AlarmTriggered",
                 "UnknownEpc", "UnexpectedEpc", "MissingEpc", "EpcImported", "UserLoggedIn"]:
    check(f"audit records {required}", required in actions)

print()
print("=" * 72)
if FAILURES:
    print(f"RESULT: {len(FAILURES)} check(s) failed")
    for f in FAILURES:
        print("  -", f)
else:
    print("RESULT: every check passed")
