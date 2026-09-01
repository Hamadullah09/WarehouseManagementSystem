import type { GateSnapshot } from '../api/types'

/**
 * The gate document in its printed form.
 *
 * Deliberately a separate rendering rather than a print stylesheet over the
 * wall display: the screen version is dark, enormous and built to be read from
 * across an aisle, none of which survives contact with paper. This one follows
 * the operational form the warehouse already uses — header block, balance
 * article list, and the two totals boxed on the right.
 *
 * It lives in the DOM at all times but is only visible to the print renderer,
 * so there is no popup to be blocked and nothing to keep in sync.
 */
export default function PrintableGateDocument({
  snapshot,
  printedBy,
}: {
  snapshot: GateSnapshot
  printedBy?: string | null
}) {
  const movement = snapshot.movementType ?? '—'
  const printedAt = new Date()

  // Two columns of EPCs read far better on a page than one long ribbon.
  const epcs = snapshot.balanceEpcs
  const half = Math.ceil(epcs.length / 2)
  const columns = epcs.length > 14 ? [epcs.slice(0, half), epcs.slice(half)] : [epcs]

  return (
    <section className="print-doc" aria-hidden="true">
      <h1 className="print-doc__title">Warehouse Entry / Exit Gate Display</h1>

      <div className="print-doc__frame">
        <header className="print-doc__head">
          <div className="print-doc__fields">
            <Field label="Document #" value={snapshot.documentNumber ?? '—'} mono wide />
            <Field label="Total articles" value={snapshot.expectedArticles} />
            <Field label="Total unit qty" value={snapshot.expectedQuantity} />
            <Field label="User" value={snapshot.userDisplayName ?? '—'} wide />
            <div className="print-doc__movement">{String(movement).toUpperCase()}</div>
          </div>
        </header>

        <div className="print-doc__body">
          <div className="print-doc__list">
            <h2>Balance articles</h2>

            {epcs.length === 0 ? (
              <p className="print-doc__none">Nothing outstanding</p>
            ) : (
              <div className="print-doc__columns">
                {columns.map((column, index) => (
                  <ol key={index} start={index === 0 ? 1 : half + 1}>
                    {column.map((epc) => (
                      <li key={epc}>{epc}</li>
                    ))}
                  </ol>
                ))}
              </div>
            )}
          </div>

          <aside className="print-doc__totals">
            <div className="print-doc__total">
              <span className="print-doc__total-label">Balance articles</span>
              <span className="print-doc__total-value">{snapshot.balanceArticles}</span>
            </div>

            <div className="print-doc__total">
              <span className="print-doc__total-label">Balance qty</span>
              <span className="print-doc__total-value print-doc__total-value--small">
                {snapshot.balanceQuantity}
              </span>
            </div>
          </aside>
        </div>

        <footer className="print-doc__foot">
          <span>Gate {snapshot.gateName} ({snapshot.gateCode})</span>
          <span>
            Detected {snapshot.detectedArticles} / {snapshot.expectedArticles} articles
            {' · '}
            {snapshot.detectedQuantity} / {snapshot.expectedQuantity} units
          </span>
          <span>
            Printed {printedAt.toLocaleString()}
            {printedBy ? ` by ${printedBy}` : ''}
          </span>
        </footer>
      </div>
    </section>
  )
}

function Field({
  label,
  value,
  mono,
  wide,
}: {
  label: string
  value: string | number
  mono?: boolean
  wide?: boolean
}) {
  return (
    <div className={`print-doc__field${wide ? ' print-doc__field--wide' : ''}`}>
      <span className="print-doc__label">{label}</span>
      <span className={`print-doc__value${mono ? ' print-doc__value--mono' : ''}`}>{value}</span>
    </div>
  )
}
