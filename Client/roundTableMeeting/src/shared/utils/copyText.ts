/** Keep the legacy path synchronous so HTTP clicks retain browser user activation. */
function copyWithSelection(text: string): void {
  const active = document.activeElement instanceof HTMLElement ? document.activeElement : null
  const selection = window.getSelection()
  const ranges = selection ? Array.from({ length: selection.rangeCount }, (_, index) => selection.getRangeAt(index).cloneRange()) : []
  const inputSelection = active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement
    ? { start: active.selectionStart, end: active.selectionEnd, direction: active.selectionDirection } : undefined
  const field = document.createElement('textarea')
  field.value = text
  field.readOnly = true
  field.tabIndex = -1
  field.style.cssText = 'position:fixed;top:0;left:0;width:1px;height:1px;padding:0;border:0;opacity:0;pointer-events:none'
  // Stay inside the focus trap when copying from an invitation modal.
  const host = active?.closest('[role="dialog"]') ?? document.body
  host.appendChild(field)
  try {
    field.focus({ preventScroll: true })
    field.select()
    field.setSelectionRange(0, text.length)
    if (!document.execCommand('copy')) throw new Error('Copy failed')
  } finally {
    field.remove()
    if (active?.isConnected) active.focus({ preventScroll: true })
    selection?.removeAllRanges()
    ranges.forEach(range => selection?.addRange(range))
    if (inputSelection?.start != null && inputSelection.end != null && (active instanceof HTMLInputElement || active instanceof HTMLTextAreaElement)) {
      active.setSelectionRange(inputSelection.start, inputSelection.end, inputSelection.direction ?? undefined)
    }
  }
}

export async function copyText(text: string): Promise<void> {
  if (navigator.clipboard?.writeText) {
    try { await navigator.clipboard.writeText(text); return } catch { /* Try the HTTP-compatible path. */ }
  }
  copyWithSelection(text)
}
