import clsx from 'clsx'
import { Minus, Plus } from 'lucide-react'
import { useState } from 'react'
import { newId } from '../../app/api'
import { Button, Modal, Textarea } from '../../components/ui'
import { money } from '../../lib/format'
import type { PosMenuItem } from '../../types'

export type DraftLine = {
  key: string
  menuItemId: string
  variantId: string | null
  modifierOptionIds: string[]
  name: string
  variantName: string | null
  modifierNames: string[]
  unitPrice: number
  quantity: number
  notes: string
}

export function needsConfiguration(item: PosMenuItem) {
  return item.variants.length > 0 || item.modifierGroups.length > 0
}

export function simpleLine(item: PosMenuItem): DraftLine {
  return {
    key: newId(), menuItemId: item.id, variantId: null, modifierOptionIds: [], name: item.name, variantName: null,
    modifierNames: [], unitPrice: item.price, quantity: 1, notes: '',
  }
}

export function ItemConfigurator({ item, onClose, onAdd }: { item: PosMenuItem; onClose: () => void; onAdd: (line: DraftLine) => void }) {
  const [variantId, setVariantId] = useState<string | null>(item.variants[0]?.id ?? null)
  const [selected, setSelected] = useState<Record<string, string[]>>(() =>
    Object.fromEntries(item.modifierGroups.map((g) => [g.id, []])))
  const [quantity, setQuantity] = useState(1)
  const [notes, setNotes] = useState('')

  const variant = item.variants.find((v) => v.id === variantId) ?? null
  const options = item.modifierGroups.flatMap((g) => g.options.filter((o) => selected[g.id]?.includes(o.id)))
  const unitPrice = (variant?.price ?? item.price) + options.reduce((s, o) => s + o.price, 0)
  const invalidGroups = item.modifierGroups.filter((g) => (selected[g.id]?.length ?? 0) < g.minSelect)

  const toggle = (groupId: string, optionId: string, max: number) =>
    setSelected((s) => {
      const current = s[groupId] ?? []
      if (current.includes(optionId)) return { ...s, [groupId]: current.filter((x) => x !== optionId) }
      if (max === 1) return { ...s, [groupId]: [optionId] }
      if (current.length >= max) return s
      return { ...s, [groupId]: [...current, optionId] }
    })

  return (
    <Modal open onClose={onClose} title={item.name} size="md"
      footer={<>
        <div className="mr-auto flex items-center gap-2">
          <Button variant="secondary" size="sm" aria-label="Decrease quantity" onClick={() => setQuantity((q) => Math.max(1, q - 1))}><Minus className="size-4" /></Button>
          <span className="w-8 text-center font-semibold" aria-live="polite">{quantity}</span>
          <Button variant="secondary" size="sm" aria-label="Increase quantity" onClick={() => setQuantity((q) => q + 1)}><Plus className="size-4" /></Button>
        </div>
        <Button disabled={invalidGroups.length > 0} onClick={() => onAdd({
          key: newId(), menuItemId: item.id, variantId, modifierOptionIds: options.map((o) => o.id), name: item.name,
          variantName: variant?.name ?? null, modifierNames: options.map((o) => o.name), unitPrice, quantity, notes: notes.trim(),
        })}>
          Add · {money(unitPrice * quantity)}
        </Button>
      </>}>
      <div className="space-y-5">
        {item.variants.length > 0 && (
          <fieldset>
            <legend className="mb-2 text-sm font-semibold">Size / variant</legend>
            <div className="grid grid-cols-2 gap-2">
              {item.variants.map((v) => (
                <label key={v.id} className={clsx('flex cursor-pointer items-center justify-between rounded-lg border px-3 py-2 text-sm',
                  variantId === v.id ? 'border-brand-600 bg-brand-50' : 'border-slate-200')}>
                  <span className="flex items-center gap-2">
                    <input type="radio" name="variant" checked={variantId === v.id} onChange={() => setVariantId(v.id)} />
                    {v.name}
                  </span>
                  <span className="text-slate-600">{money(v.price)}</span>
                </label>
              ))}
            </div>
          </fieldset>
        )}
        {item.modifierGroups.map((g) => (
          <fieldset key={g.id}>
            <legend className="mb-2 text-sm font-semibold">
              {g.name} <span className="font-normal text-slate-500">
                ({g.minSelect > 0 ? `choose ${g.minSelect === g.maxSelect ? g.minSelect : `${g.minSelect}–${g.maxSelect}`}` : `optional, up to ${g.maxSelect}`})
              </span>
            </legend>
            <div className="grid grid-cols-2 gap-2">
              {g.options.map((o) => {
                const on = selected[g.id]?.includes(o.id) ?? false
                return (
                  <label key={o.id} className={clsx('flex cursor-pointer items-center justify-between rounded-lg border px-3 py-2 text-sm',
                    on ? 'border-brand-600 bg-brand-50' : 'border-slate-200')}>
                    <span className="flex items-center gap-2">
                      <input type="checkbox" name={g.id} checked={on} onChange={() => toggle(g.id, o.id, g.maxSelect)} />
                      {o.name}
                    </span>
                    {o.price > 0 && <span className="text-slate-600">+{money(o.price)}</span>}
                  </label>
                )
              })}
            </div>
          </fieldset>
        ))}
        <Textarea label="Kitchen note (optional)" value={notes} maxLength={300} onChange={(e) => setNotes(e.target.value)} />
      </div>
    </Modal>
  )
}
