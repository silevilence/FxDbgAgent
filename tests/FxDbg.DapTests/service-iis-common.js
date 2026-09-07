'use strict';
const { assert, fs, path, until } = require('./common');
async function readWeb(url) {
  assert.equal(new URL(url).hostname, '127.0.0.1');
  const response = await fetch(url, { signal: AbortSignal.timeout(45000) });
  assert.equal(response.status, 200); return response.json();
}
async function target(root, resource, kind) {
  assert.ok(resource.service.startsWith('FxDbgStage34-services-'));
  const before = kind === 'service'
    ? await until(() => { try { return JSON.parse(fs.readFileSync(resource.heartbeat, 'utf8')); } catch { return false; } }, 'service heartbeat')
    : await readWeb(resource.url);
  assert.equal(before.bits, resource.architecture === 'x86' ? 32 : 64);
  const source = path.join(root, kind === 'service' ? 'tests/Debuggees/Environment.Shared/EnvironmentService.cs' : 'tests/Debuggees/Fx40.Environment.Web/Health.cs');
  const marker = kind === 'service' ? '// ENV_SERVICE_CALL' : '// ENV_WEB_CALL';
  const line = fs.readFileSync(source, 'utf8').split(/\r?\n/).findIndex(text => text.includes(marker)) + 1;
  assert.ok(line > 0); return { pid: before.pid, source, line, kind, resource };
}
async function assertResumed(fix, request) {
  if (fix.kind === 'iis') {
    const resumed = await request;
    assert.equal(resumed.pid, fix.pid); assert.equal(resumed.result, 85);
    const next = await readWeb(fix.resource.url);
    assert.equal(next.pid, fix.pid); assert.equal(next.result, 85);
  } else {
    const baseline = await until(() => { try { return JSON.parse(fs.readFileSync(fix.resource.heartbeat, 'utf8')); } catch { return false; } }, 'post-detach heartbeat');
    await until(() => {
      let next; try { next = JSON.parse(fs.readFileSync(fix.resource.heartbeat, 'utf8')); } catch { return false; }
      assert.equal(next.pid, fix.pid); assert.equal(next.result, next.sequence * 2 + 1);
      return next.sequence > baseline.sequence;
    }, 'next service business heartbeat');
  }
}
module.exports = { target, readWeb, assertResumed };
