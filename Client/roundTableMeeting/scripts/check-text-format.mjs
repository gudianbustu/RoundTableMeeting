import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'

const index = readFileSync('index.html', 'utf8')
const favicon = readFileSync('public/favicon.svg', 'utf8')
if (!/<link\s+rel="icon"[^>]+href="\/favicon\.svg"/i.test(index)) throw new Error('index.html must reference /favicon.svg.')
if (!favicon.includes('<svg') || !favicon.includes('RoundTableMeeting')) throw new Error('Project favicon.svg is missing its expected identity.')

const root = 'src/api/generated'
const files = []
/** 收集生成的契约文件并排除 Kiota 诊断文件 */
const visit = (directory) => {
  for (const entry of readdirSync(directory, { withFileTypes: true })) {
    const file = join(directory, entry.name)
      if (entry.isDirectory()) visit(file)
      else if (!entry.name.endsWith('.kiota.log')) files.push(file)
  }
}
visit(root)
if (readdirSync(root).some((entry) => entry.endsWith('.kiota.log'))) throw new Error('Kiota diagnostic log must not be in generated output.')
for (const file of files) {
  const bytes = readFileSync(file)
  if (bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) throw new Error('UTF-8 BOM: ' + file)
  const content = bytes.toString('utf8')
  if (/\r(?!\n)|(?<!\r)\n/.test(content)) throw new Error('Non-CRLF line ending: ' + file)
  if (/[ \t]+\r?\n/.test(content)) throw new Error('Trailing whitespace: ' + file)
}
