import { lazy, Suspense, useEffect, useState } from 'react'
import { Route, Routes, useParams } from 'react-router'
import { useQuery, useQueryClient } from '@tanstack/react-query'
import { api, onUnauthorized } from './api/client'
import { Layout } from './components/Layout'
import { Empty, Loading } from './components/ui'
import { JobsProvider } from './hooks/useJobs'
import { ScopeProvider } from './hooks/useScope'
import { integrationUi } from './integrations/registry'
import { Login } from './pages/Login'

const Overview = lazy(() => import('./pages/Overview'))
const Browse = lazy(() => import('./pages/Browse'))
const Search = lazy(() => import('./pages/Search'))
const Duplicates = lazy(() => import('./pages/Duplicates'))
const Insights = lazy(() => import('./pages/Insights'))
const Rules = lazy(() => import('./pages/Rules'))
const Ai = lazy(() => import('./pages/Ai'))
const Actions = lazy(() => import('./pages/Actions'))
const PlanPage = lazy(() => import('./pages/PlanPage'))
const Integrations = lazy(() => import('./pages/Integrations'))
const Settings = lazy(() => import('./pages/Settings'))

function IntegrationPage() {
  const { id } = useParams()
  const ui = id ? integrationUi(id) : undefined
  if (!ui) return <Empty>Unknown integration.</Empty>
  const Page = ui.page
  return <Page />
}

export default function App() {
  const qc = useQueryClient()
  const me = useQuery({ queryKey: ['me'], queryFn: () => api<{ required: boolean; authenticated: boolean }>('/auth/me'), staleTime: Infinity })
  const [locked, setLocked] = useState(false)
  useEffect(() => onUnauthorized(() => setLocked(true)), [])

  if (me.isLoading) return <Loading />
  if (locked || (me.data && !me.data.authenticated)) {
    return (
      <Login
        onSuccess={() => {
          setLocked(false)
          qc.invalidateQueries()
        }}
      />
    )
  }

  const logout = me.data?.required
    ? async () => {
        await api('/auth/logout', { method: 'POST' })
        qc.clear()
        setLocked(true)
      }
    : undefined

  return (
    <ScopeProvider>
      <JobsProvider>
        <Suspense fallback={<Loading />}>
          <Routes>
            <Route element={<Layout onLogout={logout} />}>
              <Route index element={<Overview />} />
              <Route path="browse" element={<Browse />} />
              <Route path="browse/:id" element={<Browse />} />
              <Route path="search" element={<Search />} />
              <Route path="duplicates" element={<Duplicates />} />
              <Route path="insights" element={<Insights />} />
              <Route path="rules" element={<Rules />} />
              <Route path="ai" element={<Ai />} />
              <Route path="actions" element={<Actions />} />
              <Route path="actions/:id" element={<PlanPage />} />
              <Route path="integrations" element={<Integrations />} />
              <Route path="integrations/:id" element={<IntegrationPage />} />
              <Route path="settings" element={<Settings />} />
              <Route path="*" element={<Empty>Page not found.</Empty>} />
            </Route>
          </Routes>
        </Suspense>
      </JobsProvider>
    </ScopeProvider>
  )
}
