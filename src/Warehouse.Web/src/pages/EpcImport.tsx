import { useRef, useState } from 'react'
import { api } from '../api/client'

interface ImportResult {
  totalRows: number
  imported: number
  updated: number
  skipped: number
  errors: { row: number; epc?: string; reason: string }[]
}

/**
 * CSV import for the EPC catalogue (§44).
 *
 * The catalogue is what every gate decision is measured against, so the
 * import reports each rejected row with its line number rather than silently
 * dropping it.
 */
export default function EpcImport() {
  const fileRef = useRef<HTMLInputElement>(null)
  const [updateExisting, setUpdateExisting] = useState(false)
  const [result, setResult] = useState<ImportResult | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()

    const file = fileRef.current?.files?.[0]
    if (!file) return

    setBusy(true)
    setError(null)
    setResult(null)

    try {
      setResult(await api.importEpcs(file, updateExisting))
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <h1>EPC import</h1>

      {error && <div className="error">{error}</div>}

      <form className="card" onSubmit={submit} style={{ maxWidth: 640 }}>
        <div className="field">
          <label htmlFor="csv">CSV file</label>
          <input id="csv" ref={fileRef} type="file" accept=".csv,text/csv" required />
        </div>

        <div className="field">
          <label className="row" style={{ gap: 8 }}>
            <input
              type="checkbox"
              checked={updateExisting}
              onChange={(e) => setUpdateExisting(e.target.checked)}
              style={{ width: 'auto' }}
            />
            <span>Update EPCs that already exist</span>
          </label>
        </div>

        <p className="muted" style={{ fontSize: 12.5, lineHeight: 1.55 }}>
          Expected headers: <code>Epc</code>, <code>ItemCode</code>, <code>ItemName</code>,{' '}
          <code>SerialNumber</code>, <code>CartonNumber</code>, <code>ProductCode</code>,{' '}
          <code>Description</code>, <code>UnitQuantity</code>, <code>Status</code>. Only{' '}
          <code>Epc</code> is required. Rows that are malformed, duplicated, or reference an
          unknown product are reported below and nothing is half-imported.
        </p>

        <div className="row row--end">
          <button className="primary" type="submit" disabled={busy}>
            {busy ? 'Importing…' : 'Import'}
          </button>
        </div>
      </form>

      {result && (
        <>
          <h2>Result</h2>

          <div className="cards" style={{ maxWidth: 640 }}>
            <div className="card">
              <div className="card__label">Rows read</div>
              <div className="card__value">{result.totalRows}</div>
            </div>
            <div className="card card--ok">
              <div className="card__label">Imported</div>
              <div className="card__value">{result.imported}</div>
            </div>
            <div className="card">
              <div className="card__label">Updated</div>
              <div className="card__value">{result.updated}</div>
            </div>
            <div className="card">
              <div className="card__label">Skipped</div>
              <div className="card__value">{result.skipped}</div>
            </div>
            <div className={`card${result.errors.length > 0 ? ' card--bad' : ''}`}>
              <div className="card__label">Rejected</div>
              <div className="card__value">{result.errors.length}</div>
            </div>
          </div>

          {result.errors.length > 0 && (
            <>
              <h2>Rejected rows</h2>
              <table>
                <thead>
                  <tr>
                    <th>Line</th>
                    <th>EPC</th>
                    <th>Reason</th>
                  </tr>
                </thead>
                <tbody>
                  {result.errors.map((e, i) => (
                    <tr key={`${e.row}-${i}`}>
                      <td>{e.row}</td>
                      <td className="mono">{e.epc ?? '—'}</td>
                      <td>{e.reason}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          )}
        </>
      )}
    </>
  )
}
