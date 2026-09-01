import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { Alarm } from '../api/types'

/** Alarm register with acknowledge and resolve (§18, §41). */
export default function Alarms() {
  const [alarms, setAlarms] = useState<Alarm[]>([])
  const [status, setStatus] = useState('Active')
  const [error, setError] = useState<string | null>(null)

  const load = () =>
    api
      .alarms(status || undefined)
      .then(setAlarms)
      .catch((e: Error) => setError(e.message))

  useEffect(() => {
    void load()
    const timer = window.setInterval(load, 15_000)
    return () => window.clearInterval(timer)
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [status])

  const act = async (fn: () => Promise<unknown>) => {
    setError(null)
    try {
      await fn()
      await load()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  return (
    <>
      <h1>Alarms</h1>
      {error && <div className="error">{error}</div>}

      <div className="toolbar">
        <select value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">All</option>
          <option value="Active">Active</option>
          <option value="Acknowledged">Acknowledged</option>
          <option value="Resolved">Resolved</option>
        </select>
        <button onClick={() => void load()}>Refresh</button>
      </div>

      {alarms.length === 0 ? (
        <p className="empty">Nothing to show.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Raised</th>
              <th>Alarm</th>
              <th>Type</th>
              <th>Gate</th>
              <th>Document</th>
              <th>Cycle</th>
              <th>EPC</th>
              <th>Message</th>
              <th>Status</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {alarms.map((a) => (
              <tr key={a.id}>
                <td className="muted">{new Date(a.raisedAt).toLocaleString()}</td>
                <td className="mono">{a.alarmId}</td>
                <td><span className="badge badge--bad">{a.alarmType}</span></td>
                <td className="mono">{a.gateCode ?? '—'}</td>
                <td className="mono">{a.documentNumber ?? '—'}</td>
                <td className="mono">{a.cycleId ?? '—'}</td>
                <td className="mono">
                  {a.epc ?? '—'}
                  {a.epcs.length > 1 && <div className="muted">+{a.epcs.length - 1} more</div>}
                </td>
                <td>{a.message}</td>
                <td>
                  <span className={`badge ${a.status === 'Resolved' ? 'badge--ok' : a.status === 'Acknowledged' ? 'badge--warn' : 'badge--bad'}`}>
                    {a.status}
                  </span>
                  {a.resolvedBy && <div className="muted">{a.resolvedBy}</div>}
                </td>
                <td>
                  <div className="row">
                    {a.status === 'Active' && (
                      <button onClick={() => act(() => api.acknowledgeAlarm(a.id))}>Acknowledge</button>
                    )}
                    {a.status !== 'Resolved' && (
                      <button
                        className="primary"
                        onClick={() => {
                          const notes = window.prompt('Resolution notes:')
                          if (notes !== null) void act(() => api.resolveAlarm(a.id, notes))
                        }}
                      >
                        Resolve
                      </button>
                    )}
                  </div>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}
