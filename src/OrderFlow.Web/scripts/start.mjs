// `npm start` — what Aspire's AddJavaScriptApp("web", "../OrderFlow.Web", "start") runs.
//
// Two jobs, in order:
//   1. Regenerate public/config.js from the API URLs Aspire injected (see write-config.mjs).
//   2. Serve on the port Aspire assigned via PORT, defaulting to 4200.
//
// The port default is load-bearing beyond convenience: every API's CORS policy whitelists
// http://localhost:4200 and nothing else (B11 [R]1 — never a wildcard). Serve the app anywhere else and
// every request it makes is blocked by the browser, which presents as a dead UI against healthy APIs.

import { spawn } from 'node:child_process';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)), '..');
const port = process.env['PORT'] ?? '4200';

await import('./write-config.mjs');

// shell: true so the platform resolves `ng` from node_modules/.bin — npm has already put it on PATH.
// Without it this works on Linux and fails on Windows, where the shim is ng.cmd.
const ng = spawn('ng', ['serve', '--port', port, '--host', 'localhost'], {
  cwd: root,
  stdio: 'inherit',
  shell: true,
});

ng.on('exit', (code) => process.exit(code ?? 0));
