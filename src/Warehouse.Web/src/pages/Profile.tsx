import { useState } from 'react'
import { api, session } from '../api/client'

/**
 * The signed-in person's own details, and the one thing they can change here.
 *
 * Nothing about the deployment appears on this page. A supervisor has no
 * reason to see a reader's IP address to change their own password.
 */
export default function Profile() {
  const me = session.get()
  const [current, setCurrent] = useState('')
  const [replacement, setReplacement] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError(null)
    setNotice(null)

    if (replacement !== confirm) {
      setError('The two new passwords do not match.')
      return
    }

    setBusy(true)

    try {
      await api.changePassword(current, replacement)
      setNotice('Your password has been changed.')
      setCurrent('')
      setReplacement('')
      setConfirm('')
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <>
      <h1>My profile</h1>

      {error && <div className="error">{error}</div>}
      {notice && <div className="notice">{notice}</div>}

      <div className="card" style={{ maxWidth: 560 }}>
        <div className="field">
          <label>Full name</label>
          <div style={{ fontSize: 22, fontWeight: 700 }}>{me?.displayName ?? '—'}</div>
        </div>

        <div className="field">
          <label>User name</label>
          <div className="mono" style={{ fontSize: 18 }}>{me?.userName ?? '—'}</div>
        </div>

        <div className="field">
          <label>Role</label>
          <div style={{ fontSize: 18 }}>{me?.roles?.join(', ') ?? '—'}</div>
        </div>
      </div>

      <h2>Change password</h2>

      <form className="card" onSubmit={submit} style={{ maxWidth: 560 }}>
        <div className="field">
          <label htmlFor="cur">Current password</label>
          <input
            id="cur"
            type="password"
            value={current}
            onChange={(e) => setCurrent(e.target.value)}
            required
          />
        </div>

        <div className="field">
          <label htmlFor="new">New password</label>
          <input
            id="new"
            type="password"
            value={replacement}
            onChange={(e) => setReplacement(e.target.value)}
            required
          />
        </div>

        <div className="field">
          <label htmlFor="cnf">Confirm new password</label>
          <input
            id="cnf"
            type="password"
            value={confirm}
            onChange={(e) => setConfirm(e.target.value)}
            required
          />
        </div>

        <div className="row row--end">
          <button className="primary" type="submit" disabled={busy}>
            {busy ? 'Saving…' : 'Change password'}
          </button>
        </div>
      </form>
    </>
  )
}
