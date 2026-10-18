import { execFileSync } from 'node:child_process'
import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { create } from 'tar'
execFileSync(process.execPath, [fileURLToPath(new URL('./patch-atem.mjs', import.meta.url)), '--package'], {stdio: 'inherit'})
const {version} = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'))
await create({gzip: true, cwd: 'pkg', file: `ndi-machine-${version}.tgz`}, ['ndi-machine'])
