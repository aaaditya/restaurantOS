type Cell = string | number | null | undefined

function escape(cell: Cell) {
  const text = cell == null ? '' : String(cell)
  // Prefix formula-like values so spreadsheets do not execute them.
  const safe = /^[=+\-@\t\r]/.test(text) && !/^-?\d/.test(text) ? `'${text}` : text
  return /[",\n\r]/.test(safe) ? `"${safe.replace(/"/g, '""')}"` : safe
}

export function toCsv(head: string[], rows: Cell[][]) {
  return [head, ...rows].map((r) => r.map(escape).join(',')).join('\r\n')
}

export function downloadCsv(filename: string, head: string[], rows: Cell[][]) {
  const blob = new Blob(['\ufeff', toCsv(head, rows)], { type: 'text/csv;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = filename
  a.click()
  URL.revokeObjectURL(url)
}
