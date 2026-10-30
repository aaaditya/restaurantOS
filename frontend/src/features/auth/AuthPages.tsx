import { useMutation } from '@tanstack/react-query'
import { useState, type FormEvent, type ReactNode } from 'react'
import { Link, Navigate, useNavigate } from 'react-router'
import { api, type Tokens } from '../../app/api'
import { useSession } from '../../app/session'
import { Button, Checkbox, ErrorBanner, Input } from '../../components/ui'

function AuthShell({ title, subtitle, children }: { title: string; subtitle: string; children: ReactNode }) {
  return (
    <main className="flex min-h-screen items-center justify-center bg-slate-50 px-4 py-12">
      <div className="w-full max-w-md">
        <div className="mb-8 text-center">
          <div className="mx-auto mb-4 flex size-12 items-center justify-center rounded-xl bg-brand-700 text-lg font-bold text-white">R</div>
          <h1 className="text-2xl font-semibold tracking-tight">{title}</h1>
          <p className="mt-1 text-sm text-slate-600">{subtitle}</p>
        </div>
        <div className="rounded-2xl bg-white p-6 shadow-sm ring-1 ring-slate-200">{children}</div>
      </div>
    </main>
  )
}

export function LoginPage() {
  const { signIn, isAuthenticated } = useSession()
  const navigate = useNavigate()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const login = useMutation({
    mutationFn: () => api<{ tokens: Tokens }>('/api/auth/login', { method: 'POST', body: { email, password }, auth: false }),
    onSuccess: (r) => {
      signIn(r.tokens)
      navigate('/')
    },
  })

  if (isAuthenticated) return <Navigate to="/" replace />

  const submit = (e: FormEvent) => {
    e.preventDefault()
    login.mutate()
  }

  return (
    <AuthShell title="Sign in to RestaurantOS" subtitle="POS, kitchen and back office in one place">
      <form onSubmit={submit} className="space-y-4">
        <ErrorBanner error={login.error} />
        <Input label="Email" type="email" autoComplete="email" required value={email} onChange={(e) => setEmail(e.target.value)} />
        <Input label="Password" type="password" autoComplete="current-password" required value={password}
          onChange={(e) => setPassword(e.target.value)} />
        <Button type="submit" className="w-full" loading={login.isPending}>Sign in</Button>
      </form>
      <p className="mt-6 text-center text-sm text-slate-500">
        New restaurant? <Link to="/signup" className="font-medium text-brand-700 hover:underline">Start a free trial</Link>
      </p>
    </AuthShell>
  )
}

export function SignupPage() {
  const { signIn, isAuthenticated } = useSession()
  const navigate = useNavigate()
  const [form, setForm] = useState({ restaurantName: '', outletName: '', ownerName: '', email: '', password: '', phone: '', sampleMenu: true })
  const set = (k: keyof typeof form) => (e: { target: { value: string } }) => setForm((f) => ({ ...f, [k]: e.target.value }))
  const signup = useMutation({
    mutationFn: () => api<{ tokens: Tokens }>('/api/auth/signup', { method: 'POST', body: form, auth: false }),
    onSuccess: (r) => {
      signIn(r.tokens)
      navigate('/')
    },
  })

  if (isAuthenticated) return <Navigate to="/" replace />

  return (
    <AuthShell title="Create your restaurant" subtitle="14-day free trial. No card required.">
      <form onSubmit={(e) => { e.preventDefault(); signup.mutate() }} className="space-y-4">
        <ErrorBanner error={signup.error} />
        <Input label="Restaurant / brand name" required value={form.restaurantName} onChange={set('restaurantName')} />
        <Input label="First outlet name" placeholder="e.g. Indiranagar" value={form.outletName} onChange={set('outletName')}
          hint="Defaults to the restaurant name" />
        <Input label="Your name" required autoComplete="name" value={form.ownerName} onChange={set('ownerName')} />
        <Input label="Work email" type="email" required autoComplete="email" value={form.email} onChange={set('email')} />
        <Input label="Password" type="password" required minLength={8} autoComplete="new-password" value={form.password}
          onChange={set('password')} hint="At least 8 characters" />
        <Checkbox label="Start with a sample menu I can edit" checked={form.sampleMenu}
          onChange={(v) => setForm((f) => ({ ...f, sampleMenu: v }))} />
        <Button type="submit" className="w-full" loading={signup.isPending}>Create account</Button>
      </form>
      <p className="mt-6 text-center text-sm text-slate-500">
        Already have an account? <Link to="/login" className="font-medium text-brand-700 hover:underline">Sign in</Link>
      </p>
    </AuthShell>
  )
}
