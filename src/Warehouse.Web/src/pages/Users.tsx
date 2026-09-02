import { useEffect, useState } from 'react'
import { api } from '../api/client'
import type { WarehouseUser } from '../api/types'

/**
 * Account administration.
 *
 * An account that has signed documents is switched off rather than removed, so
 * the audit trail keeps naming a real person. The server decides which happens
 * and says so in its reply; this page repeats that rather than guessing.
 */
export default function Users() {
  const [users, setUsers] = useState<WarehouseUser[]>([])
  const [roles, setRoles] = useState<string[]>([])
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [editing, setEditing] = useState<WarehouseUser | 'new' | null>(null)

  const load = async () => {
    try {
      setUsers(await api.users())
      setRoles((await api.roles()).map((r) => r.name))
      setError(null)
    } catch (e) {
      setError((e as Error).message)
    }
  }

  useEffect(() => {
    void load()
  }, [])

  const act = async (run: () => Promise<unknown>, message: string) => {
    try {
      const result = await run()
      setNotice(
        result && typeof result === 'object' && 'message' in result
          ? String((result as { message: unknown }).message)
          : message,
      )
      setError(null)
      await load()
    } catch (e) {
      setError((e as Error).message)
    }
  }

  const statusOf = (u: WarehouseUser) => {
    if (!u.isActive) return { label: 'Switched off', colour: 'var(--muted)' }
    if (u.isLockedOut) return { label: 'Locked out', colour: 'var(--bad)' }
    if (u.resetRequested) return { label: 'Wants a new password', colour: 'var(--warn)' }
    if (u.mustChangePassword) return { label: 'Must set a password', colour: 'var(--warn)' }
    return { label: 'Ready', colour: 'var(--ok)' }
  }

  return (
    <>
      <div className="row" style={{ alignItems: 'center' }}>
        <h1 style={{ flex: 1 }}>Users</h1>
        <button className="primary" onClick={() => setEditing('new')}>Add user</button>
      </div>

      {error && <div className="error">{error}</div>}
      {notice && <div className="notice">{notice}</div>}

      <table>
        <thead>
          <tr>
            <th>Name</th>
            <th>User name</th>
            <th>Role</th>
            <th>Status</th>
            <th style={{ width: 320 }}>Actions</th>
          </tr>
        </thead>
        <tbody>
          {users.map((u) => {
            const status = statusOf(u)

            return (
              <tr key={u.id}>
                <td><strong>{u.displayName}</strong></td>
                <td className="mono">{u.userName}</td>
                <td>{u.roles.join(', ')}</td>
                <td>
                  <span className="pill--chip" style={{ background: status.colour }}>
                    {status.label}
                  </span>
                </td>
                <td>
                  <div className="row" style={{ gap: 8, flexWrap: 'wrap' }}>
                    <button className="info" onClick={() => setEditing(u)}>Edit</button>

                    <button
                      className="warn"
                      onClick={() => {
                        const password = window.prompt(`New password for ${u.displayName}`)
                        if (password) {
                          void act(
                            () => api.resetUserPassword(u.id, password),
                            `Password set for ${u.displayName}.`,
                          )
                        }
                      }}
                    >
                      Reset password
                    </button>

                    <button
                      className={u.isActive ? 'danger' : 'good'}
                      onClick={() =>
                        void act(
                          () => api.updateUser(u.id, { isActive: !u.isActive }),
                          u.isActive
                            ? `${u.displayName} switched off.`
                            : `${u.displayName} switched on.`,
                        )
                      }
                    >
                      {u.isActive ? 'Switch off' : 'Switch on'}
                    </button>

                    <button
                      className="danger"
                      onClick={() => {
                        if (window.confirm(`Remove ${u.displayName}?`)) {
                          void act(() => api.deleteUser(u.id), `${u.displayName} removed.`)
                        }
                      }}
                    >
                      Remove
                    </button>
                  </div>
                </td>
              </tr>
            )
          })}
        </tbody>
      </table>

      {editing && (
        <UserForm
          user={editing === 'new' ? null : editing}
          roles={roles}
          onCancel={() => setEditing(null)}
          onSaved={async (message) => {
            setEditing(null)
            setNotice(message)
            await load()
          }}
          onError={setError}
        />
      )}
    </>
  )
}

function UserForm({
  user,
  roles,
  onCancel,
  onSaved,
  onError,
}: {
  user: WarehouseUser | null
  roles: string[]
  onCancel: () => void
  onSaved: (message: string) => void | Promise<void>
  onError: (message: string) => void
}) {
  const [userName, setUserName] = useState(user?.userName ?? '')
  const [displayName, setDisplayName] = useState(user?.displayName ?? '')
  const [email, setEmail] = useState(user?.email ?? '')
  const [password, setPassword] = useState('')
  const [role, setRole] = useState(user?.roles[0] ?? 'Operator')
  const [busy, setBusy] = useState(false)

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)

    try {
      if (user) {
        await api.updateUser(user.id, { displayName, email, roles: [role] })
        await onSaved(`${displayName} updated.`)
      } else {
        await api.createUser({ userName, displayName, email, password, roles: [role] })
        await onSaved(`${displayName} added.`)
      }
    } catch (err) {
      onError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="card" onSubmit={submit} style={{ maxWidth: 560, marginTop: 24 }}>
      <h2 style={{ marginTop: 0 }}>{user ? 'Edit user' : 'Add user'}</h2>

      <div className="field">
        <label htmlFor="u-name">User name</label>
        <input
          id="u-name"
          value={userName}
          onChange={(e) => setUserName(e.target.value)}
          disabled={user !== null}
          required
        />
      </div>

      <div className="field">
        <label htmlFor="u-display">Full name</label>
        <input
          id="u-display"
          value={displayName}
          onChange={(e) => setDisplayName(e.target.value)}
          required
        />
      </div>

      <div className="field">
        <label htmlFor="u-email">Email (optional)</label>
        <input id="u-email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} />
      </div>

      {!user && (
        <div className="field">
          <label htmlFor="u-password">First password</label>
          <input
            id="u-password"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            required
          />
          <small className="muted">They will be asked to change it when they first sign in.</small>
        </div>
      )}

      <div className="field">
        <label htmlFor="u-role">Role</label>
        <select id="u-role" value={role} onChange={(e) => setRole(e.target.value)}>
          {roles.map((r) => (
            <option key={r} value={r}>{r}</option>
          ))}
        </select>
      </div>

      <div className="row row--end" style={{ gap: 10 }}>
        <button type="button" onClick={onCancel}>Cancel</button>
        <button className="primary" type="submit" disabled={busy}>
          {busy ? 'Saving…' : 'Save'}
        </button>
      </div>
    </form>
  )
}
