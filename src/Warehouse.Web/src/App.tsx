import { NavLink, Navigate, Route, HashRouter as Router, Routes, useNavigate } from 'react-router-dom'
import { useEffect, useState } from 'react'
import { api, session } from './api/client'
import GateDisplay from './pages/GateDisplay'
import Dashboard from './pages/Dashboard'
import Documents from './pages/Documents'
import Readers from './pages/Readers'
import Alarms from './pages/Alarms'
import Cycles from './pages/Cycles'
import EpcImport from './pages/EpcImport'

/**
 * Two shells share one bundle.
 *
 * `/gate/:code` is the wall display: full-bleed, no chrome, no navigation.
 * Everything else is the admin console behind a sidebar. Both authenticate
 * the same way, so a gate screen still needs a signed-in session.
 */
export default function App() {
  return (
    <Router>
      <Routes>
        <Route path="/login" element={<Login />} />
        <Route path="/gate/:gateCode" element={<Guard><GateDisplay /></Guard>} />
        <Route path="/*" element={<Guard><AdminShell /></Guard>} />
      </Routes>
    </Router>
  )
}

function Guard({ children }: { children: React.ReactNode }) {
  return session.get() ? <>{children}</> : <Navigate to="/login" replace />
}

function AdminShell() {
  const navigate = useNavigate()
  const current = session.get()

  const signOut = () => {
    session.clear()
    navigate('/login', { replace: true })
  }

  return (
    <div className="shell">
      <nav className="sidebar">
        <div className="sidebar__brand">
          Warehouse Gate
          <small>RFID control</small>
        </div>

        <NavLink to="/" end>Dashboard</NavLink>
        <NavLink to="/documents">Documents</NavLink>
        <NavLink to="/cycles">Gate cycles</NavLink>
        <NavLink to="/alarms">Alarms</NavLink>
        <NavLink to="/readers">RFID readers</NavLink>
        <NavLink to="/epcs">EPC import</NavLink>

        <div className="sidebar__spacer" />

        <div style={{ padding: '0 10px 10px', fontSize: 12 }} className="muted">
          {current?.displayName}
          <br />
          {current?.roles.join(', ')}
        </div>

        <button onClick={signOut}>Sign out</button>
      </nav>

      <main className="content">
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/documents" element={<Documents />} />
          <Route path="/cycles" element={<Cycles />} />
          <Route path="/alarms" element={<Alarms />} />
          <Route path="/readers" element={<Readers />} />
          <Route path="/epcs" element={<EpcImport />} />
          <Route path="*" element={<Navigate to="/" replace />} />
        </Routes>
      </main>
    </div>
  )
}

function Login() {
  const navigate = useNavigate()
  const [userName, setUserName] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  useEffect(() => {
    if (session.get()) navigate('/', { replace: true })
  }, [navigate])

  const submit = async (e: React.FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(null)

    try {
      const result = await api.login(userName, password)
      session.set(result)

      navigate('/', { replace: true })
    } catch (err) {
      setError((err as Error).message)
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login">
      <form onSubmit={submit}>
        <h1>Warehouse Gate</h1>
        <p className="muted">Sign in to continue</p>

        {error && <div className="error">{error}</div>}

        <div className="field">
          <label htmlFor="user">User name</label>
          <input
            id="user"
            value={userName}
            onChange={(e) => setUserName(e.target.value)}
            autoComplete="username"
            autoFocus
            required
          />
        </div>

        <div className="field">
          <label htmlFor="pass">Password</label>
          <input
            id="pass"
            type="password"
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            autoComplete="current-password"
            required
          />
        </div>

        <button className="primary" type="submit" disabled={busy} style={{ width: '100%' }}>
          {busy ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
    </div>
  )
}
