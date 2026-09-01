import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { Reader } from '../api/types'

/** RFID reader inventory and manual connection control (§41). */
export default function Readers() {
  const [readers, setReaders] = useState<Reader[]>([])
  const [error, setError] = useState<string | null>(null)

  const load = () =>
    api.readers().then(setReaders).catch((e: Error) => setError(e.message))

  useEffect(() => {
    void load()
    const timer = window.setInterval(load, 10_000)
    return () => window.clearInterval(timer)
  }, [])

  const act = async (fn: () => Promise<unknown>) => {
    try {
      await fn()
      await load()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  return (
    <>
      <h1>RFID readers</h1>
      {error && <div className="error">{error}</div>}

      {readers.length === 0 ? (
        <p className="empty">No readers are configured.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Reader</th>
              <th>Gate</th>
              <th>Address</th>
              <th>Model</th>
              <th>Status</th>
              <th>Antennas</th>
              <th>GPIO</th>
              <th>Temp</th>
              <th>Last seen</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {readers.map((r) => (
              <tr key={r.readerId}>
                <td>
                  <strong>{r.name}</strong>
                  <br />
                  <span className="muted mono">{r.readerId}</span>
                </td>
                <td className="mono">{r.gateCode}</td>
                <td className="mono">
                  {r.ipAddress ?? '—'}{r.port ? `:${r.port}` : ''}
                </td>
                <td>
                  {r.model}
                  {r.firmwareVersion && <><br /><span className="muted">{r.firmwareVersion}</span></>}
                </td>
                <td>
                  <span className={`badge ${r.isOnline ? 'badge--ok' : 'badge--bad'}`}>
                    {r.isOnline ? 'ONLINE' : 'OFFLINE'}
                  </span>
                  {r.isInventorying && <> <span className="badge badge--info">READING</span></>}
                  {r.lastError && <div className="muted" style={{ marginTop: 4 }}>{r.lastError}</div>}
                </td>
                <td>{r.antennas.length > 0 ? r.antennas.join(', ') : '—'}</td>
                <td className="mono">{r.gpio.length > 0 ? r.gpio.join(' ') : '—'}</td>
                <td>{r.temperatureCelsius != null ? `${r.temperatureCelsius.toFixed(0)} °C` : '—'}</td>
                <td className="muted">
                  {r.lastSeenAt ? new Date(r.lastSeenAt).toLocaleTimeString() : '—'}
                </td>
                <td>
                  <div className="row">
                    <button onClick={() => act(() => api.connectReader(r.readerId))} disabled={r.isOnline}>
                      Connect
                    </button>
                    <button onClick={() => act(() => api.disconnectReader(r.readerId))} disabled={!r.isOnline}>
                      Disconnect
                    </button>
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
