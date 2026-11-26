import { mkdir, lstat, open, readFile, rename, unlink, chmod } from 'node:fs/promises'
import { dirname, resolve } from 'node:path'
import { randomUUID, createHash } from 'node:crypto'
import { createServer } from 'node:net'
import { execFile } from 'node:child_process'
import { promisify } from 'node:util'

const run = promisify(execFile)

export class PrivateState {
  constructor(path) { this.path = resolve(path); this.lockPath = this.path + '.lock' }

  async acquire() {
    const directory = dirname(this.path)
    await mkdir(directory, { recursive: true, mode: 0o700 })
    if ((await lstat(directory)).isSymbolicLink()) throw new Error('state_directory_symlink')
    if (process.platform === 'win32') {
      // Replace the dedicated directory ACL, including explicit grants, before storing secrets.
      await run('powershell.exe', ['-NoProfile', '-NonInteractive', '-Command',
        '$ErrorActionPreference="Stop"; $p=$env:RTM_PRIVATE_STATE_DIRECTORY; $acl=[System.Security.AccessControl.DirectorySecurity]::new(); ' +
        '$acl.SetAccessRuleProtection($true,$false); ' +
        '$sid=[System.Security.Principal.WindowsIdentity]::GetCurrent().User; ' +
        '$rule=[System.Security.AccessControl.FileSystemAccessRule]::new($sid,"FullControl","ContainerInherit,ObjectInherit","None","Allow"); ' +
        '$acl.AddAccessRule($rule); [System.IO.Directory]::SetAccessControl($p,$acl)'],
      { windowsHide: true, env: { ...process.env, RTM_PRIVATE_STATE_DIRECTORY: directory } })
    } else await chmod(directory, 0o700)
    if (process.platform === 'win32') {
      // Windows releases named-pipe ownership on a crash; no unsafe stale-file lock stealing.
      const name = '\\\\.\\pipe\\rtm-agent-state-' + createHash('sha256').update(this.path.toLowerCase()).digest('hex')
      const pipe = createServer(socket => socket.destroy())
      await new Promise((accept, reject) => {
        pipe.once('error', () => reject(new Error('state_locked')))
        pipe.listen(name, accept)
      })
      this.pipe = pipe
    } else {
      // Never unlink an existing lock: concurrent stale-PID checks can delete a new owner's lock.
      // After a crash, stop all clients for this state before manually removing the stale file.
      try { this.lock = await open(this.lockPath, 'wx', 0o600) }
      catch (error) {
        if (error.code === 'EEXIST') throw new Error('state_locked')
        throw error
      }
      await this.lock.writeFile(String(process.pid))
      await this.lock.sync()
    }
    try {
      if ((await lstat(this.path)).isSymbolicLink()) throw new Error('state_file_symlink')
      return JSON.parse(await readFile(this.path, 'utf8'))
    } catch (error) { if (error.code === 'ENOENT') return undefined; throw error }
  }

  async save(state) {
    const temporary = this.path + '.' + randomUUID() + '.tmp'
    const file = await open(temporary, 'wx', 0o600)
    try { await file.writeFile(JSON.stringify(state)); await file.sync() }
    finally { await file.close() }
    try {
      await rename(temporary, this.path)
      if (process.platform !== 'win32') {
        const directory = await open(dirname(this.path), 'r')
        try { await directory.sync() } finally { await directory.close() }
      }
    } finally { await unlink(temporary).catch(error => { if (error.code !== 'ENOENT') throw error }) }
  }

  async release() {
    if (this.pipe) { await new Promise(resolve => this.pipe.close(resolve)); this.pipe = undefined }
    if (!this.lock) return
    await this.lock.close()
    this.lock = undefined
    await unlink(this.lockPath)
  }
}
