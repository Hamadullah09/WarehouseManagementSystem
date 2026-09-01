import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { GateCycle, GateSnapshot } from '../api/types'

/** Gate cycle history: the audit view of what physically passed each gate (§41). */
export default function Cycles() {
  const [gates, setGates] = useState<GateSnapshot[]>([])
  const [gateCode, setGateCode] = useState('')
  const [cycles, setCycles] = useState<GateCycle[]>([])
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    api
      .gates()
      .then((g) => {
        setGates(g)
        if (g.length > 0 && !gateCode) setGateCode(g[0].gateCode)
      })
      .catch((e: Error) => setError(e.message))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  useEffect(() => {
    if (!gateCode) return

    let active = true

    const load = () =>
      api
        .cycles(gateCode, 100)
        .then((c) => active && setCycles(c))
        .catch((e: Error) => active && setError(e.message))

    void load()
    const timer = window.setInterval(load, 15_000)

    return () => {
      active = false
      window.clearInterval(timer)
    }
  }, [gateCode])

  return (
    <>
      <h1>Gate cycles</h1>
      {error && <div className="error">{error}</div>}

      <div className="toolbar">
        <select value={gateCode} onChange={(e) => setGateCode(e.target.value)}>
          {gates.map((g) => (
            <option key={g.gateCode} value={g.gateCode}>
              {g.gateName} ({g.gateCode})
            </option>
          ))}
        </select>
      </div>

      {cycles.length === 0 ? (
        <p className="empty">No cycles recorded for this gate.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Cycle</th>
              <th>Document</th>
              <th>Started</th>
              <th>Duration</th>
              <th>Expected</th>
              <th>Detected</th>
              <th>Raw reads</th>
              <th>Unknown</th>
              <th>Unexpected</th>
              <th>Missing</th>
              <th>Result</th>
              <th>Committed</th>
            </tr>
          </thead>
          <tbody>
            {cycles.map((c) => (
              <tr key={c.id}>
                <td className="mono">{c.cycleId}</td>
                <td className="mono">{c.documentNumber ?? '—'}</td>
                <td className="muted">{new Date(c.startedAt).toLocaleString()}</td>
                <td>{duration(c)}</td>
                <td>{c.expectedEpcCount}</td>
                <td>{c.detectedEpcCount}</td>
                <td className="muted">{c.rawReadCount}</td>
                <td className={c.unknownEpcCount > 0 ? 'mono' : 'muted'}>{c.unknownEpcCount}</td>
                <td className={c.unexpectedEpcCount > 0 ? 'mono' : 'muted'}>{c.unexpectedEpcCount}</td>
                <td className={c.missingEpcCount > 0 ? 'mono' : 'muted'}>{c.missingEpcCount}</td>
                <td>
                  <span
                    className={`badge ${
                      c.validationResult === 'Pass'
                        ? 'badge--ok'
                        : c.validationResult === 'Fail'
                          ? 'badge--bad'
                          : 'badge--warn'
                    }`}
                  >
                    {c.validationResult ?? c.status}
                  </span>
                  {!c.readerHealthy && (
                    <div className="muted" style={{ marginTop: 4 }}>reader unhealthy</div>
                  )}
                  {c.validationSummary && (
                    <div className="muted" style={{ marginTop: 4 }}>{c.validationSummary}</div>
                  )}
                </td>
                <td>
                  <span className={`badge ${c.inventoryCommitted ? 'badge--ok' : ''}`}>
                    {c.inventoryCommitted ? 'YES' : 'NO'}
                  </span>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}

function duration(cycle: GateCycle): string {
  if (!cycle.completedAt) return '—'

  const ms = new Date(cycle.completedAt).getTime() - new Date(cycle.startedAt).getTime()

  return ms < 1000 ? `${ms} ms` : `${(ms / 1000).toFixed(1)} s`
}
