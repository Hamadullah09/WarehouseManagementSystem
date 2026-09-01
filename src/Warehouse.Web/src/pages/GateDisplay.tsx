import { useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { useParams } from 'react-router-dom'
import { api, session } from '../api/client'
import { useGateHub } from '../hooks/useGateHub'
import PrintableGateDocument from '../components/PrintableGateDocument'
import type {
  AlarmRaisedUpdate,
  CycleCompletedUpdate,
  EpcDetectedUpdate,
  GateSnapshot,
  GateState,
} from '../api/types'

/** Human wording and colour band for each gate state (§20). */
const STATE_PRESENTATION: Record<GateState, { label: string; tone: string }> = {
  Idle: { label: 'IDLE', tone: 'neutral' },
  Ready: { label: 'READY', tone: 'ready' },
  WaitingForGate: { label: 'WAITING FOR GATE', tone: 'ready' },
  Reading: { label: 'READING RFID', tone: 'active' },
  Processing: { label: 'PROCESSING', tone: 'active' },
  Validating: { label: 'VALIDATING', tone: 'active' },
  Passed: { label: 'PASS', tone: 'pass' },
  Alarm: { label: 'ALARM', tone: 'alarm' },
  Error: { label: 'ERROR', tone: 'alarm' },
  ReaderDisconnected: { label: 'RFID READER OFFLINE', tone: 'offline' },
}

/**
 * Severity order, lowest number worst. Mirrors the server's AlarmType ordering.
 *
 * One cycle can raise several alarms at once — an unknown tag and a shortfall,
 * say — and they arrive as separate pushes. The banner must show the worst of
 * them, not whichever happened to land last.
 */
const ALARM_SEVERITY: Record<string, number> = {
  UnknownEpc: 1,
  UnexpectedEpc: 2,
  MissingEpc: 3,
  NoEpc: 4,
  DocumentMismatch: 5,
  ReaderError: 6,
  GpioError: 7,
  ReaderDisconnected: 8,
  Timeout: 9,
  DuplicateGateEvent: 10,
}

const ALARM_HEADLINE: Record<string, string> = {
  UnknownEpc: 'UNKNOWN EPC DETECTED',
  UnexpectedEpc: 'UNEXPECTED EPC DETECTED',
  MissingEpc: 'INCOMPLETE MOVEMENT',
  NoEpc: 'NO RFID TAG DETECTED',
  DocumentMismatch: 'DOCUMENT MISMATCH',
  ReaderError: 'READER ERROR',
  GpioError: 'GPIO ERROR',
  ReaderDisconnected: 'READER DISCONNECTED',
  Timeout: 'GATE CYCLE TIMED OUT',
  DuplicateGateEvent: 'DUPLICATE GATE EVENT',
}

/**
 * The screen mounted above the gate (§19, §42).
 *
 * Built for a wall-mounted monitor read from several metres away: the two
 * numbers that matter are enormous, the status band changes colour before it
 * changes words, and no operator input is required for the normal flow.
 *
 * It is driven entirely by SignalR pushes. The REST snapshot is used once on
 * load and again if the socket drops, so a network blip degrades the display
 * rather than desynchronising it.
 */
export default function GateDisplay() {
  const { gateCode = '' } = useParams()

  const [snapshot, setSnapshot] = useState<GateSnapshot | null>(null)
  const [lastEpc, setLastEpc] = useState<string | null>(null)
  const [liveDetected, setLiveDetected] = useState(0)
  const [liveExpected, setLiveExpected] = useState(0)
  const [verdict, setVerdict] = useState<CycleCompletedUpdate | null>(null)
  const [alarm, setAlarm] = useState<AlarmRaisedUpdate | null>(null)
  const [error, setError] = useState<string | null>(null)

  const verdictTimer = useRef<number | undefined>(undefined)

  const onGateStatus = useCallback((update: GateSnapshot) => {
    setSnapshot(update)

    if (update.state === 'Reading') {
      // A new cycle has opened: clear the previous verdict from the screen.
      setVerdict(null)
      setAlarm(null)
      setLiveDetected(0)
    }

    if (update.lastEpc) setLastEpc(update.lastEpc)
  }, [])

  const onEpcDetected = useCallback((update: EpcDetectedUpdate) => {
    setLastEpc(update.epc)
    setLiveDetected(update.detectedCount)
    setLiveExpected(update.expectedCount)
  }, [])

  const onCycleCompleted = useCallback((update: CycleCompletedUpdate) => {
    setVerdict(update)

    // A pass clears itself so the screen returns to READY for the next load.
    // An alarm stays until an operator deals with it.
    window.clearTimeout(verdictTimer.current)

    if (update.passed) {
      verdictTimer.current = window.setTimeout(() => setVerdict(null), 6000)
    }
  }, [])

  const onAlarmRaised = useCallback((update: AlarmRaisedUpdate) => {
    setAlarm((current) => {
      if (!current) return update

      const worst = ALARM_SEVERITY[current.alarmType] ?? 99
      const incoming = ALARM_SEVERITY[update.alarmType] ?? 99

      // Only a more severe alarm displaces the one on screen.
      return incoming < worst ? update : current
    })
  }, [])

  const { connected } = useGateHub(gateCode, {
    onGateStatus,
    onEpcDetected,
    onCycleCompleted,
    onAlarmRaised,
  })

  useEffect(() => {
    let active = true

    api
      .gate(gateCode)
      .then((s) => active && setSnapshot(s))
      .catch((e: Error) => active && setError(e.message))

    return () => {
      active = false
      window.clearTimeout(verdictTimer.current)
    }
  }, [gateCode])

  const presentation = useMemo(() => {
    if (!snapshot) return STATE_PRESENTATION.Idle

    // A raised alarm outranks the state band: the operator must see it first.
    if (alarm) return { label: 'ALARM', tone: 'alarm' }
    if (verdict) return verdict.passed
      ? { label: 'PASS', tone: 'pass' }
      : { label: 'ALARM', tone: 'alarm' }

    return STATE_PRESENTATION[snapshot.state] ?? STATE_PRESENTATION.Idle
  }, [snapshot, alarm, verdict])

  if (error) {
    return (
      <div className="display display--offline">
        <h1>Gate unavailable</h1>
        <p>{error}</p>
      </div>
    )
  }

  if (!snapshot) {
    return (
      <div className="display display--offline">
        <h1>Connecting to {gateCode}…</h1>
      </div>
    )
  }

  const reading = snapshot.state === 'Reading' || snapshot.state === 'Processing'
  const detected = reading ? liveDetected : snapshot.detectedArticles
  const expected = reading ? liveExpected || snapshot.expectedArticles : snapshot.expectedArticles
  const movement = snapshot.movementType ?? '—'

  return (
    <>
      <div className={`display display--${presentation.tone}`}>
        <header className="display__header">
          <div className="display__title">
            <span className="display__eyebrow">Warehouse entry / exit gate</span>
            <h1>{snapshot.gateName}</h1>
          </div>

          <div className={`movement movement--${String(movement).toLowerCase()}`}>{movement}</div>

          <div className="display__health">
            <span className={`pill ${snapshot.readerOnline ? 'pill--ok' : 'pill--bad'}`}>
              {snapshot.readerOnline ? 'READER ONLINE' : 'READER OFFLINE'}
            </span>
            <span className={`pill ${connected ? 'pill--ok' : 'pill--warn'}`}>
              {connected ? 'LIVE' : 'RECONNECTING'}
            </span>

            <button
              type="button"
              className="pill pill--action no-print"
              onClick={() => window.print()}
              title="Print the balance document, or save it as a PDF"
            >
              PRINT / PDF
            </button>
          </div>
        </header>

        <section className="meta">
          <Meta label="Document" value={snapshot.documentNumber ?? '—'} wide />
          <Meta label="User" value={snapshot.userDisplayName ?? '—'} />
          <Meta label="Total articles" value={snapshot.expectedArticles} />
          <Meta label="Total unit qty" value={snapshot.expectedQuantity} />
        </section>

        <main className="display__body">
          <section className="balance-list">
            <h2>Balance articles</h2>

            {snapshot.balanceEpcs.length === 0 ? (
              <p className="balance-list__empty">Nothing outstanding</p>
            ) : (
              <ol className="balance-list__items">
                {snapshot.balanceEpcs.map((epc) => (
                  <li key={epc} className={epc === lastEpc ? 'is-current' : undefined}>
                    {epc}
                  </li>
                ))}
              </ol>
            )}
          </section>

          <section className="counters">
            <div className="counter">
              <span className="counter__label">Balance articles</span>
              <span className="counter__value">{snapshot.balanceArticles}</span>
            </div>

            <div className="counter counter--secondary">
              <span className="counter__label">Balance qty</span>
              <span className="counter__value">{snapshot.balanceQuantity}</span>
            </div>

            {reading && (
              <div className="progress">
                <span className="progress__text">
                  Detected {detected} / {expected}
                </span>
                <div className="progress__track">
                  <div
                    className="progress__fill"
                    style={{ width: expected > 0 ? `${Math.min(100, (detected / expected) * 100)}%` : '0%' }}
                  />
                </div>
              </div>
            )}
          </section>
        </main>

        <footer className={`status status--${presentation.tone}`}>
          <div className="status__main">
            <span className="status__label">{presentation.label}</span>

            <span className="status__detail">
              {alarm
                ? (ALARM_HEADLINE[alarm.alarmType] ?? alarm.alarmType)
                : verdict
                  ? verdict.passed
                    ? verdict.documentStatus === 'Completed'
                      ? 'Document completed'
                      : 'Cycle accepted'
                    : verdict.summary
                  : (snapshot.statusMessage ?? '')}
            </span>
          </div>

          {lastEpc && <div className="status__epc">{lastEpc}</div>}

          {alarm && (
            <div className="status__offenders">
              <span>{alarm.message}</span>
              {alarm.epcs.length > 0 && (
                <ul>
                  {alarm.epcs.slice(0, 8).map((epc) => (
                    <li key={epc}>{epc}</li>
                  ))}
                  {alarm.epcs.length > 8 && <li>+{alarm.epcs.length - 8} more</li>}
                </ul>
              )}
            </div>
          )}

          {!alarm && verdict && !verdict.passed && verdict.missing.length > 0 && (
            <div className="status__offenders">
              <span>Missing EPCs</span>
              <ul>
                {verdict.missing.slice(0, 8).map((epc) => (
                  <li key={epc}>{epc}</li>
                ))}
                {verdict.missing.length > 8 && <li>+{verdict.missing.length - 8} more</li>}
              </ul>
            </div>
          )}
        </footer>
      </div>

      <PrintableGateDocument snapshot={snapshot} printedBy={session.get()?.displayName} />
    </>
  )
}

function Meta({
  label,
  value,
  wide,
}: {
  label: string
  value: string | number
  wide?: boolean
}) {
  return (
    <div className={`meta__cell${wide ? ' meta__cell--wide' : ''}`}>
      <span className="meta__label">{label}</span>
      <span className="meta__value">{value}</span>
    </div>
  )
}
