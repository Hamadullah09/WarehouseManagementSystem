import { useEffect, useRef, useState } from 'react'
import { api } from '../api/client'
import type { EpcImportOutcome, GateSnapshot } from '../api/types'

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
  const [generateDocuments, setGenerateDocuments] = useState(false)
  const [documentType, setDocumentType] = useState<'Inward' | 'Outward'>('Inward')
  const [epcsPerDocument, setEpcsPerDocument] = useState(30)
  const [gateCode, setGateCode] = useState('')
  const [gates, setGates] = useState<GateSnapshot[]>([])
  const [outcome, setOutcome] = useState<EpcImportOutcome | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    void api.gates().then(setGates).catch(() => setGates([]))
  }, [])

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()

    const file = fileRef.current?.files?.[0]
    if (!file) return

    setBusy(true)
    setError(null)
    setOutcome(null)

    try {
      setOutcome(await api.importEpcs(file, {
        updateExisting,
        generateDocuments,
        documentType,
        epcsPerDocument,
        gateCode: gateCode || undefined,
      }))
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

        <div className="field">
          <label className="row" style={{ gap: 8 }}>
            <input
              type="checkbox"
              checked={generateDocuments}
              onChange={(e) => setGenerateDocuments(e.target.checked)}
              style={{ width: 'auto' }}
            />
            <span>Raise documents from these EPCs</span>
          </label>
        </div>

        {generateDocuments && (
          <div className="row" style={{ gap: 12, flexWrap: 'wrap' }}>
            <div className="field" style={{ flex: '1 1 150px' }}>
              <label htmlFor="doctype">Movement</label>
              <select
                id="doctype"
                value={documentType}
                onChange={(e) => setDocumentType(e.target.value as 'Inward' | 'Outward')}
              >
                <option value="Inward">Inward</option>
                <option value="Outward">Outward</option>
              </select>
            </div>

            <div className="field" style={{ flex: '1 1 150px' }}>
              <label htmlFor="perdoc">EPCs per document</label>
              <input
                id="perdoc"
                type="number"
                min={1}
                value={epcsPerDocument}
                onChange={(e) => setEpcsPerDocument(Number(e.target.value))}
              />
            </div>

            <div className="field" style={{ flex: '1 1 180px' }}>
              <label htmlFor="gate">Release to gate</label>
              <select id="gate" value={gateCode} onChange={(e) => setGateCode(e.target.value)}>
                <option value="">Leave as drafts</option>
                {gates.map((g) => (
                  <option key={g.gateCode} value={g.gateCode}>{g.gateCode}</option>
                ))}
              </select>
            </div>
          </div>
        )}

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

      {outcome && (
        <>
          <h2>Result</h2>

          <div className="cards" style={{ maxWidth: 640 }}>
            <div className="card">
              <div className="card__label">Rows read</div>
              <div className="card__value">{outcome.import.totalRows}</div>
            </div>
            <div className="card card--ok">
              <div className="card__label">Imported</div>
              <div className="card__value">{outcome.import.imported}</div>
            </div>
            <div className="card">
              <div className="card__label">Updated</div>
              <div className="card__value">{outcome.import.updated}</div>
            </div>
            <div className="card">
              <div className="card__label">Skipped</div>
              <div className="card__value">{outcome.import.skipped}</div>
            </div>
            <div className={`card${outcome.import.errors.length > 0 ? ' card--bad' : ''}`}>
              <div className="card__label">Rejected</div>
              <div className="card__value">{outcome.import.errors.length}</div>
            </div>
          </div>

          {outcome.documents.length > 0 && (
            <>
              <h2>Documents raised</h2>
              <p className="muted" style={{ fontSize: 13 }}>
                Planned from the rows in this file, in the order the file listed them. The reader
                app will pick these up on its next refresh.
              </p>
              <table>
                <thead>
                  <tr>
                    <th>Document</th>
                    <th>Movement</th>
                    <th>Articles</th>
                    <th>Units</th>
                    <th>Gate</th>
                  </tr>
                </thead>
                <tbody>
                  {outcome.documents.map((d) => (
                    <tr key={d.id}>
                      <td className="mono">{d.documentNumber}</td>
                      <td>{d.type}</td>
                      <td>{d.expectedArticles}</td>
                      <td>{d.expectedQuantity}</td>
                      <td>{d.gateCode ?? '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          )}

          {outcome.import.errors.length > 0 && (
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
                  {outcome.import.errors.map((e, i) => (
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
