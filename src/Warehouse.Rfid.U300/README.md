# Warehouse.Rfid.U300

.NET driver for the Chainway U300 fixed RFID reader.

The U300 runs Android 11 and Chainway ships a **Java-only** SDK, so this
assembly does not open a socket to the reader itself. It talks to the
**U300 bridge** (`bridge/u300-bridge`), a small Java process that uses the
vendor classes against the reader's slave-mode service on TCP 9160.

See `docs/U300-INTEGRATION.md` for the full SDK inspection notes and the
bridge wire protocol.
