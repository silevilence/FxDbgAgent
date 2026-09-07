'use strict';
const { assert, fs, path, until } = require('./common');
const { Client } = require('./protocol');

async function main() {
  const [bundle, root, configuration, manifest] = process.argv.slice(2);
  const cases = [], resources = JSON.parse(fs.readFileSync(manifest, 'utf8')).resources;
  const source = path.join(root, 'tests/Debuggees/Environment.Shared/EnvironmentService.cs');
  const line = fs.readFileSync(source, 'utf8').split(/\r?\n/).findIndex(text => text.includes('// ENV_SERVICE_CALL')) + 1;
  let passed = false;
  try {
    for (const resource of resources) {
      const before = JSON.parse(fs.readFileSync(resource.heartbeat, 'utf8'));
      const client = new Client(bundle);
      try {
        await client.request('initialize');
        const attach = client.send('attach', { processId: before.pid });
        await client.event('initialized');
        await client.request('setBreakpoints', { source: { path: source }, breakpoints: [{ line }] });
        const cursor = client.events.length;
        await client.request('configurationDone'); await attach.completion;
        const stopped = await client.event('stopped', cursor);
        assert.equal(stopped.body.reason, 'breakpoint');
        const stack = await client.request('stackTrace', { threadId: stopped.body.threadId, levels: 8 });
        assert.equal(stack.stackFrames[0].line, line);
        assert.equal(stack.stackFrames[0].source.path.toLowerCase(), source.toLowerCase());
        await client.request('disconnect', { terminateDebuggee: false });
        const baseline = await until(() => {
          try { return JSON.parse(fs.readFileSync(resource.heartbeat, 'utf8')); } catch { return false; }
        }, 'post-detach heartbeat baseline');
        await until(() => {
          let after; try { after = JSON.parse(fs.readFileSync(resource.heartbeat, 'utf8')); } catch { return false; }
          assert.equal(after.pid, before.pid);
          return after.sequence > baseline.sequence;
        }, 'DAP detach resumes service heartbeat');
        cases.push({ architecture: resource.architecture, kind: 'service', pid: before.pid, line });
      } finally { await client.close(); }
    }
    passed = true;
  } finally {
    fs.writeFileSync(path.join(root, `artifacts/stage3-4-validation/dap-services-${configuration}.json`), JSON.stringify({ passed, cases }));
  }
}
main().catch(error => { console.error(error); process.exitCode = 1; });
