import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { api } from '../api/client'
import { useGateHub } from '../hooks/useGateHub'
import type { Dashboard as DashboardDto, GateSnapshot } from '../api/types'

/** Operations overview with live gate tiles (§41). */
export default function Dashboard() {
  const [stats, setStats] = useState<DashboardDto | null>(null)
  const [gates, setGates] = useState<GateSnapshot[]>([])
  const [error, setError] = useState<string | null>(null)

  const onGateStatus = useCallback((update: GateSnapshot) => {
    setGates((current) => {
      const index = current.findIndex((g) => g.gateCode === update.gateCode)
      if (index < 0) return [...current, update]

      const next = [...current]
      next[index] = update

      return next
    })
  }, [])

  // Joining with a null gate code subscribes to the dashboard group, which
  // receives every gate's traffic.
  const { connected } = useGateHub(null, { onGateStatus })

  useEffect(() => {
    let active = true

    const load = () =>
      Promise.all([api.dashboard(), api.gates()])
        .then(([d, g]) => {
          if (!active) return
          setStats(d)
          setGates(g)
        })
        .catch((e: Error) => active && setError(e.message))

    void load()

    // The counters are aggregates rather than events, so a slow refresh keeps
    // them honest without polling for gate state, which arrives by push.
    const timer = window.setInterval(load, 30_000)

    return () => {
      active = false
      window.clearInterval(timer)
    }
  }, [])

  return (
    <>
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <h1>Dashboard</h1>
        <span className={`badge ${connected ? 'badge--ok' : 'badge--warn'}`}>
          {connected ? 'LIVE' : 'RECONNECTING'}
        </span>
      </div>

      {error && <div className="error">{error}</div>}

      {stats && (
        <div className="cards">
          <Card label="Active gates" value={stats.activeGates} />
          <Card label="Readers online" value={stats.onlineReaders} tone="ok" />
          <Card label="Readers offline" value={stats.offlineReaders} tone={stats.offlineReaders > 0 ? 'bad' : undefined} />
          <Card label="Active alarms" value={stats.activeAlarms} tone={stats.activeAlarms > 0 ? 'bad' : 'ok'} />
          <Card label="Unknown EPCs today" value={stats.unknownEpcsToday} tone={stats.unknownEpcsToday > 0 ? 'warn' : undefined} />
          <Card label="Inward today" value={stats.todayInward} />
          <Card label="Outward today" value={stats.todayOutward} />
          <Card label="Pending documents" value={stats.pendingDocuments} />
          <Card label="Completed today" value={stats.completedDocuments} tone="ok" />
          <Card label="Registered EPCs" value={stats.totalEpcs} />
          <Card label="EPCs in stock" value={stats.epcsInStock} />
        </div>
      )}

      <h2>Gates</h2>

      {gates.length === 0 ? (
        <p className="empty">No gates are configured.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Gate</th>
              <th>State</th>
              <th>Reader</th>
              <th>Document</th>
              <th>Movement</th>
              <th>Progress</th>
              <th>Balance</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {gates.map((gate) => (
              <tr key={gate.gateCode}>
                <td>
                  <strong>{gate.gateName}</strong>
                  <br />
                  <span className="muted mono">{gate.gateCode}</span>
                </td>
                <td><StateBadge state={gate.state} /></td>
                <td>
                  <span className={`badge ${gate.readerOnline ? 'badge--ok' : 'badge--bad'}`}>
                    {gate.readerOnline ? 'ONLINE' : 'OFFLINE'}
                  </span>
                </td>
                <td className="mono">{gate.documentNumber ?? '—'}</td>
                <td>{gate.movementType ?? '—'}</td>
                <td>
                  {gate.expectedArticles > 0
                    ? `${gate.detectedArticles} / ${gate.expectedArticles}`
                    : '—'}
                </td>
                <td>{gate.balanceArticles} art · {gate.balanceQuantity} qty</td>
                <td>
                  <Link className="button" to={`/gate/${encodeURIComponent(gate.gateCode)}`}>
                    Open display
                  </Link>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </>
  )
}

function Card({ label, value, tone }: { label: string; value: number; tone?: 'ok' | 'bad' | 'warn' }) {
  return (
    <div className={`card${tone ? ` card--${tone}` : ''}`}>
      <div className="card__label">{label}</div>
      <div className="card__value">{value}</div>
    </div>
  )
}

export function StateBadge({ state }: { state: string }) {
  const tone =
    state === 'Passed' ? 'badge--ok'
    : state === 'Alarm' || state === 'Error' ? 'badge--bad'
    : state === 'ReaderDisconnected' ? 'badge--warn'
    : state === 'Reading' || state === 'Validating' || state === 'Processing' ? 'badge--info'
    : ''

  return <span className={`badge ${tone}`}>{state}</span>
}
