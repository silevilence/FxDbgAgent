'use strict';
const { assert, fs, path } = require('./common');
const { Client } = require('./protocol');
const { target, readWeb, assertResumed } = require('./service-iis-common');
async function main() {
  const [bundle, root, configuration, manifest] = process.argv.slice(2);
  const cases = [], resources = JSON.parse(fs.readFileSync(manifest, 'utf8')).resources;
  let passed = false;
  try {
    for (const resource of resources) for (const kind of ['service', 'iis']) {
      const fix = await target(root, resource, kind), client = new Client(bundle);
      let request;
      try {
        await client.request('initialize');
        const attach = client.send('attach', { processId: fix.pid });
        await client.event('initialized');
        await client.request('setBreakpoints', { source: { path: fix.source }, breakpoints: [{ line: fix.line }] });
        const cursor = client.events.length;
        await client.request('configurationDone'); await attach.completion;
        if (kind === 'iis') { request = readWeb(resource.url); request.catch(() => {}); }
        const stopped = await client.event('stopped', cursor);
        assert.equal(stopped.body.reason, 'breakpoint');
        const stack = await client.request('stackTrace', { threadId: stopped.body.threadId, levels: 8 });
        assert.equal(stack.stackFrames[0].line, fix.line);
        assert.equal(stack.stackFrames[0].source.path.toLowerCase(), fix.source.toLowerCase());
        await client.request('disconnect', { terminateDebuggee: false });
        await assertResumed(fix, request);
        cases.push({ architecture: resource.architecture, kind, pid: fix.pid, line: fix.line });
      } finally { await client.close(); if (request) await request.catch(() => {}); }
    }
    assert.equal(cases.length, 4); passed = true;
  } finally {
    fs.writeFileSync(path.join(root, `artifacts/stage3-4-validation/dap-service-iis-${configuration}.json`), JSON.stringify({ passed, cases }));
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
