import { act, cleanup, render, waitFor } from '@testing-library/react'
import { BrowserRouter } from 'react-router'
import { afterEach, beforeEach, expect, test } from 'vitest'
import { useEntryPreview, useUrlEnum, useUrlOverlay, useUrlParam } from './useUrlState'

type Api = {
  tab: ReturnType<typeof useUrlEnum<'a' | 'b'>>
  q: ReturnType<typeof useUrlParam>
  dialog: ReturnType<typeof useUrlOverlay>
  file: ReturnType<typeof useEntryPreview>
}

let api: Api

function Probe() {
  api = {
    tab: useUrlEnum('tab', ['a', 'b'] as const, 'a'),
    q: useUrlParam('q'),
    dialog: useUrlOverlay('dialog'),
    file: useEntryPreview(),
  }
  return null
}

const search = () => window.location.search
const back = async (expected: string) => {
  act(() => window.history.back())
  await waitFor(() => expect(search()).toBe(expected))
}

beforeEach(() => {
  window.history.replaceState(null, '', '/page')
  render(
    <BrowserRouter>
      <Probe />
    </BrowserRouter>,
  )
})
afterEach(cleanup)

test('tabs add history entries, defaults are left out of the URL', async () => {
  act(() => api.tab[1]('b'))
  expect(search()).toBe('?tab=b')
  expect(api.tab[0]).toBe('b')
  act(() => api.tab[1]('a'))
  expect(search()).toBe('')
  await back('?tab=b')
  expect(api.tab[0]).toBe('b')
})

test('typing replaces the entry instead of adding one', async () => {
  act(() => api.tab[1]('b'))
  act(() => api.q[1]('x', { replace: true }))
  act(() => api.q[1]('xy', { replace: true }))
  expect(search()).toBe('?tab=b&q=xy')
  await back('')
})

test('closing an opened overlay goes back instead of adding an entry', async () => {
  act(() => api.tab[1]('b'))
  act(() => api.dialog.open('new'))
  expect(search()).toBe('?tab=b&dialog=new')
  act(() => {
    api.dialog.close()
    api.dialog.close() // a second close before the browser moved must not go back twice
  })
  await waitFor(() => expect(search()).toBe('?tab=b'))
  await back('')
})

test('an overlay loaded from a link closes in place', () => {
  act(() => api.tab[1]('b', { also: { dialog: '5' } }))
  act(() => api.dialog.close())
  expect(search()).toBe('?tab=b')
})

test('stepping through files keeps Back closing the file panel, with its tab', async () => {
  act(() => api.file.open(1))
  act(() => api.file.setTab('hex'))
  act(() => api.file.step(2))
  expect(search()).toBe('?file=2&fileTab=hex')
  expect(api.file.id).toBe(2)
  expect(api.file.tab).toBe('hex')
  act(() => api.file.close())
  await waitFor(() => expect(search()).toBe(''))
  expect(api.file.id).toBeNull()
})
