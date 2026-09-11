import test from 'node:test'
import assert from 'node:assert/strict'
import { mkdtemp, readFile, writeFile, rm, unlink } from 'node:fs/promises'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { PrivateState } from './reliable-state.mjs'

test('Unix contenders never remove an abandoned lock; failed cleanup cannot release another owner', async () => {
  // Exercise the portable file-lock branch on Windows too; this does not emulate Unix fsync/ACLs.
  const platform = Object.getOwnPropertyDescriptor(process, 'platform')
  Object.defineProperty(process, 'platform', { ...platform, value: 'linux' })
  const directory = await mkdtemp(join(tmpdir(), 'rtm-stale-lock-test-'))
  const owners = [new PrivateState(join(directory, 'state.json')), new PrivateState(join(directory, 'state.json'))]
  try {
    // A PID that cannot exist on the host makes the old stale-PID reclamation path deterministic.
    const stale = '2147483647'
    await writeFile(owners[0].lockPath, stale)
    const results = await Promise.allSettled(owners.map(owner => owner.acquire()))
    assert.ok(results.every(result => result.status === 'rejected' && result.reason.message === 'state_locked'))
    await Promise.all(owners.map(owner => owner.release()))
    assert.equal(await readFile(owners[0].lockPath, 'utf8'), stale)

    // Explicit maintenance with both contenders stopped permits exactly one subsequent owner.
    await unlink(owners[0].lockPath)
    const claims = await Promise.allSettled(owners.map(owner => owner.acquire()))
    assert.equal(claims.filter(result => result.status === 'fulfilled').length, 1)
    const winner = claims.findIndex(result => result.status === 'fulfilled')
    await owners[1 - winner].release()
    await assert.rejects(owners[1 - winner].acquire(), /state_locked/)
    assert.equal(await readFile(owners[0].lockPath, 'utf8'), String(process.pid))
    await owners[winner].release()
    assert.equal(await owners[1 - winner].acquire(), undefined)
  } finally {
    await Promise.all(owners.map(owner => owner.release()))
    await rm(directory, { recursive: true, force: true })
    Object.defineProperty(process, 'platform', platform)
  }
})
