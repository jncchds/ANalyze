import { useState, type FormEvent } from 'react'
import { api, ApiError } from '../api/client'

export function Login({ onSuccess }: { onSuccess: () => void }) {
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string>()
  const [busy, setBusy] = useState(false)

  const submit = async (e: FormEvent) => {
    e.preventDefault()
    setBusy(true)
    setError(undefined)
    try {
      await api('/auth/login', { method: 'POST', json: { password } })
      onSuccess()
    } catch (err) {
      setError(err instanceof ApiError && err.status === 429 ? 'Too many attempts, wait a minute.' : 'Wrong password.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="flex min-h-dvh items-center justify-center bg-bg px-4">
      <form onSubmit={submit} className="card w-full max-w-sm space-y-4 p-6">
        <div className="flex items-center gap-3">
          <img src="/favicon.svg" alt="" className="h-9 w-9" />
          <div>
            <h1 className="text-lg font-bold">ANalyze</h1>
            <p className="text-sm muted">Enter the password to continue.</p>
          </div>
        </div>
        <input type="password" className="input" value={password} onChange={(e) => setPassword(e.target.value)} placeholder="Password" autoFocus autoComplete="current-password" />
        {error && <p className="text-sm text-danger">{error}</p>}
        <button className="btn-primary w-full" disabled={busy || !password}>
          Sign in
        </button>
      </form>
    </div>
  )
}
