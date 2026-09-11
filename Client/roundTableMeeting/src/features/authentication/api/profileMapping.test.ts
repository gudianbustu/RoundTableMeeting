import { expect, it } from 'vitest'
import { toCurrentUserViewModel } from './login'
it('accepts false and rejects missing or nonboolean administrator values', () => {
 const user = { userId: 'u', username: 'user', nickname: 'User' }
 expect(toCurrentUserViewModel({ ...user, isAdmin: false }).isAdmin).toBe(false)
 expect(() => toCurrentUserViewModel(user)).toThrow()
 expect(() => toCurrentUserViewModel({ ...user, isAdmin: 'false' as unknown as boolean })).toThrow()
})
