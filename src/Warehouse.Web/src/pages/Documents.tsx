import { useEffect, useState } from 'react'
import { ApiError, api } from '../api/client'
import type { DocumentSummary, GateSnapshot } from '../api/types'

/** Document register, plus creation and gate release (§21, §41). */
export default function Documents() {
  const [documents, setDocuments] = useState<DocumentSummary[]>([])
  const [gates, setGates] = useState<GateSnapshot[]>([])
  const [type, setType] = useState('')
  const [status, setStatus] = useState('')
  const [search, setSearch] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [offending, setOffending] = useState<string[]>([])
  const [notice, setNotice] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)

  const load = async () => {
    try {
      const result = await api.documents({ type, status, search, pageSize: 100 })
      setDocuments(result.items)
      setError(null)
    } catch (e) {
      setError((e as Error).message)
    }
  }

  useEffect(() => {
    void load()
    void api.gates().then(setGates).catch(() => setGates([]))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [type, status])

  const act = async (fn: () => Promise<unknown>, message: string) => {
    setError(null)
    setOffending([])

    try {
      await fn()
      setNotice(message)
      await load()
    } catch (e) {
      setNotice(null)
      setError((e as Error).message)

      if (e instanceof ApiError) setOffending(e.offending)
    }
  }

  return (
    <>
      <div className="row" style={{ justifyContent: 'space-between' }}>
        <h1>Documents</h1>
        <button className="primary" onClick={() => setCreating(true)}>New document</button>
      </div>

      {error && (
        <div className="error">
          {error}
          {offending.length > 0 && (
            <ul>
              {offending.slice(0, 20).map((e) => <li key={e}>{e}</li>)}
              {offending.length > 20 && <li>+{offending.length - 20} more</li>}
            </ul>
          )}
        </div>
      )}

      {notice && <div className="success">{notice}</div>}

      {creating && (
        <CreateDocument
          gates={gates}
          onCancel={() => setCreating(false)}
          onCreated={async (number) => {
            setCreating(false)
            setNotice(`Created ${number}`)
            await load()
          }}
          onError={(message, values) => {
            setError(message)
            setOffending(values)
          }}
        />
      )}

      <div className="toolbar">
        <select value={type} onChange={(e) => setType(e.target.value)}>
          <option value="">All types</option>
          <option value="Inward">Inward</option>
          <option value="Outward">Outward</option>
        </select>

        <select value={status} onChange={(e) => setStatus(e.target.value)}>
          <option value="">All statuses</option>
          {['Draft', 'Released', 'InProgress', 'Completed', 'Cancelled', 'Failed'].map((s) => (
            <option key={s} value={s}>{s}</option>
          ))}
        </select>

        <input
          placeholder="Document number or reference"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          onKeyDown={(e) => e.key === 'Enter' && void load()}
        />

        <button onClick={() => void load()}>Search</button>
      </div>

      {documents.length === 0 ? (
        <p className="empty">No documents match these filters.</p>
      ) : (
        <table>
          <thead>
            <tr>
              <th>Document</th>
              <th>Type</th>
              <th>User</th>
              <th>Gate</th>
              <th>Articles</th>
              <th>Quantity</th>
              <th>Status</th>
              <th>Created</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {documents.map((d) => (
              <tr key={d.id}>
                <td className="mono">{d.documentNumber}</td>
                <td>{d.type}</td>
                <td>{d.userDisplayName ?? '—'}</td>
                <td className="mono">{d.gateCode ?? '—'}</td>
                <td>{d.detectedArticles} / {d.expectedArticles}</td>
                <td>{d.detectedQuantity} / {d.expectedQuantity}</td>
                <td><StatusBadge status={d.status} /></td>
                <td className="muted">{new Date(d.createdAt).toLocaleString()}</td>
                <td>
                  <div className="row">
                    {(d.status === 'Draft' || d.status === 'Released') && gates.length > 0 && (
                      <select
                        defaultValue=""
                        onChange={(e) =>
                          e.target.value &&
                          act(
                            () => api.releaseDocument(d.id, e.target.value),
                            `${d.documentNumber} released to ${e.target.value}`,
                          )
                        }
                        style={{ width: 130 }}
                      >
                        <option value="">Release to…</option>
                        {gates.map((g) => (
                          <option key={g.gateCode} value={g.gateCode}>{g.gateCode}</option>
                        ))}
                      </select>
                    )}

                    {d.status !== 'Completed' && d.status !== 'Cancelled' && (
                      <>
                        <button
                          onClick={() =>
                            act(() => api.retryDocument(d.id), `${d.documentNumber} reset for retry`)
                          }
                        >
                          Retry
                        </button>

                        <button
                          className="danger"
                          onClick={() => {
                            const reason = window.prompt(`Cancel ${d.documentNumber}? Reason:`)
                            if (reason !== null) {
                              void act(
                                () => api.cancelDocument(d.id, reason),
                                `${d.documentNumber} cancelled`,
                              )
                            }
                          }}
                        >
                          Cancel
                        </button>
                      </>
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

function CreateDocument({
  gates,
  onCancel,
  onCreated,
  onError,
}: {
  gates: GateSnapshot[]
  onCancel: () => void
  onCreated: (documentNumber: string) => void | Promise<void>
  onError: (message: string, offending: string[]) => void
}) {
  const [type, setType] = useState<'inward' | 'outward'>('inward')
  const [gateCode, setGateCode] = useState('')
  const [reference, setReference] = useState('')
  const [raw, setRaw] = useState('')
  const [busy, setBusy] = useState(false)

  const epcs = raw
    .split(/[\s,;]+/)
    .map((v) => v.trim())
    .filter(Boolean)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)

    try {
      const created = await api.createDocument(type, {
        epcs,
        gateCode: gateCode || undefined,
        reference: reference || undefined,
      })

      await onCreated(created.documentNumber)
    } catch (err) {
      onError((err as Error).message, err instanceof ApiError ? err.offending : [])
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="card" onSubmit={submit} style={{ marginBottom: 20 }}>
      <h2 style={{ marginTop: 0 }}>New document</h2>

      <div className="row" style={{ alignItems: 'flex-start' }}>
        <div className="field" style={{ flex: '0 0 150px' }}>
          <label>Type</label>
          <select value={type} onChange={(e) => setType(e.target.value as 'inward' | 'outward')}>
            <option value="inward">Inward</option>
            <option value="outward">Outward</option>
          </select>
        </div>

        <div className="field" style={{ flex: '0 0 190px' }}>
          <label>Release to gate (optional)</label>
          <select value={gateCode} onChange={(e) => setGateCode(e.target.value)}>
            <option value="">Leave as draft</option>
            {gates.map((g) => (
              <option key={g.gateCode} value={g.gateCode}>{g.gateCode}</option>
            ))}
          </select>
        </div>

        <div className="field" style={{ flex: 1, minWidth: 180 }}>
          <label>Reference (optional)</label>
          <input value={reference} onChange={(e) => setReference(e.target.value)} />
        </div>
      </div>

      <div className="field">
        <label>Expected EPCs — one per line, or separated by commas ({epcs.length} entered)</label>
        <textarea
          value={raw}
          onChange={(e) => setRaw(e.target.value)}
          placeholder="E28011606000020000000001&#10;E28011606000020000000002"
          required
        />
      </div>

      <div className="row row--end">
        <button type="button" onClick={onCancel}>Cancel</button>
        <button className="primary" type="submit" disabled={busy || epcs.length === 0}>
          {busy ? 'Creating…' : `Create with ${epcs.length} EPC${epcs.length === 1 ? '' : 's'}`}
        </button>
      </div>
    </form>
  )
}

export function StatusBadge({ status }: { status: string }) {
  const tone =
    status === 'Completed' ? 'badge--ok'
    : status === 'Cancelled' || status === 'Failed' ? 'badge--bad'
    : status === 'InProgress' || status === 'Released' ? 'badge--info'
    : ''

  return <span className={`badge ${tone}`}>{status}</span>
}
