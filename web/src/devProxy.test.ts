import { describe, expect, it } from 'vitest'
import { isDevServerOrigin } from '../vite.config'

describe('isDevServerOrigin', () => {
  it("is true for the dev server's own page, in any case", () => {
    expect(isDevServerOrigin('http://127.0.0.1:5173', '127.0.0.1:5173')).toBe(true)
    expect(isDevServerOrigin('http://LOCALHOST:5173', 'localhost:5173')).toBe(true)
  })

  it('is false for any other origin, so the API refuses it', () => {
    expect(isDevServerOrigin('https://attacker.example', '127.0.0.1:5173')).toBe(false)
    expect(isDevServerOrigin('http://127.0.0.1:3000', '127.0.0.1:5173')).toBe(false)
    expect(isDevServerOrigin('https://127.0.0.1:5173', '127.0.0.1:5173')).toBe(false)
    expect(isDevServerOrigin('null', '127.0.0.1:5173')).toBe(false)
    expect(isDevServerOrigin('http://attacker.example:5173', 'attacker.example:5173')).toBe(false)
    expect(isDevServerOrigin('http://127.0.0.1:5173', undefined)).toBe(false)
  })
})
