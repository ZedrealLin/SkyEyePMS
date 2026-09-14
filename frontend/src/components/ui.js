import { reactive } from 'vue'

export const toast = reactive({ text: '', err: false })
let timer = null

export function showToast(text, err = false) {
  toast.text = text
  toast.err = err
  clearTimeout(timer)
  timer = setTimeout(() => (toast.text = ''), 2600)
}
