import { spawn } from 'node:child_process';
import { resolve, join } from 'node:path';
import assert from 'node:assert/strict';

const bundle = resolve(process.argv[2]);
const children = new Set();
function connect(extra = [], environment = {}) {
  const child = spawn('dotnet', [join(bundle, 'fxdbg-mcp.dll'), ...extra], { windowsHide: true, cwd: process.env.TEMP, env: { ...process.env, ...environment } });
  children.add(child);
  const messages = [], pending = new Map();
  let buffer = '', stderr = '', failure;
  child.stderr.setEncoding('utf8');
  child.stderr.on('data', data => stderr += data);
  child.stdin.on('error', error => { if (error.code !== 'EPIPE' && error.code !== 'EOF') failure = error; });
  child.stdout.setEncoding('utf8');
  child.stdout.on('data', data => {
    buffer += data;
    let index;
    while ((index = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, index).trim(); buffer = buffer.slice(index + 1);
      try {
        assert.ok(line, 'stdout must have no blank/log lines');
        const item = JSON.parse(line); assert.equal(item.jsonrpc, '2.0'); messages.push(item);
        pending.get(item.id)?.resolve(item); pending.delete(item.id);
      } catch (error) { failure = error; child.kill(); }
    }
  });
  const exited = new Promise(resolveExit => child.on('exit', code => {
    children.delete(child); resolveExit(code);
    for (const entry of pending.values()) entry.reject(new Error('MCP exited before response: ' + stderr));
  }));
  let id = 0;
  const api = {
    child, messages, get stderr() { return stderr; },
    send: item => child.stdin.write(JSON.stringify(item) + '\n'),
    async call(method, params) {
      const requestId = ++id;
      const response = new Promise((resolveResponse, reject) => pending.set(requestId, { resolve: resolveResponse, reject }));
      api.send({ jsonrpc: '2.0', id: requestId, method, params });
      return await deadline(response, 8000);
    },
    async close() {
      child.stdin.end();
      const code = await deadline(exited, 12000);
      if (failure) throw failure;
      assert.equal(buffer.trim(), '', 'Incomplete stdout frame');
      return code;
    }
  };
  return api;
}
async function deadline(promise, ms) {
  let timer;
  try { return await Promise.race([promise, new Promise((_, reject) => timer = setTimeout(() => reject(new Error('Protocol test timed out')), ms))]); }
  finally { clearTimeout(timer); }
}
try {
  const client = connect();
  const early = await client.call('tools/list', {});
  assert.ok(early.error, 'Uninitialized requests must be rejected');
  const initialized = await client.call('initialize', { protocolVersion: '2099-01-01', capabilities: {}, clientInfo: { name: 'wire-tests', version: '1' } });
  assert.equal(initialized.result.protocolVersion, '2025-11-25', 'Unsupported client version negotiates server version');
  client.send({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} });
  assert.ok((await client.call('ping', {})).result);
  const listed = await client.call('tools/list', {});
  assert.equal(listed.result?.tools.length, 15, JSON.stringify(listed));
  assert.ok(listed.result.tools.some(tool => tool.name === 'debug_evaluate'));
  assert.equal((await client.call('method/absent', {})).error.code, -32601);
  const missing = await client.call('tools/call', { name: 'debug_status', arguments: {} });
  assert.equal(missing.result.isError, true);
  client.send({ jsonrpc: '2.0', method: 'notifications/cancelled', params: { requestId: 9999 } });
  await client.close();
  const malformed = connect();
  malformed.child.stdin.write('{ broken JSON }\n');
  await malformed.close(); // A malformed transport is allowed to close; no log text may enter stdout.
  const absent = connect(['--engine-dir', join(bundle, 'missing engines')]);
  assert.notEqual(await absent.close(), 0, 'Missing dependency must fail startup');
  for (const option of [['--max-sessions', '9'], ['--max-calls', '0'], ['--max-control-calls', 'bad'], ['--log-level', 'trace'], ['--value-logs', 'on']]) {
    const invalidConfig = connect(option);
    assert.notEqual(await invalidConfig.close(), 0, 'Invalid limits must fail startup');
  }
  const oversized = connect();
  oversized.child.stdin.write(' '.repeat(4 * 1024 * 1024 + 1)); // No newline: limit applies while accumulating.
  assert.notEqual(await oversized.close(), 0, 'Oversized input closes without unbounded buffering');
  const duplicate = connect();
  duplicate.child.stdin.write('{"jsonrpc":"2.0","id":1,"id":2,"method":"ping"}\n');
  assert.notEqual(await duplicate.close(), 0, 'Duplicate properties must not select an ambiguous request');
  const quiet = connect(['--log-level', 'off', '--value-logs', 'off']);
  await quiet.call('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'quiet-test', version: '1' } });
  quiet.send({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} });
  await quiet.call('tools/call', { name: 'debug_status', arguments: { sessionId: 'secret-invalid-session' } });
  await quiet.close();
  assert.equal(quiet.stderr, '', 'Off level emits no tool diagnostics');
  // One logical processor makes the SDK's yielded handlers overtake each other reliably.
  // Wire-order admission must reject the early request even if its handler runs last.
  for (let iteration = 0; iteration < 20; iteration++) {
    const race = connect([], { DOTNET_PROCESSOR_COUNT: '1' });
    race.send({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} });
    await race.call('initialize', { protocolVersion: '2025-11-25', capabilities: {}, clientInfo: { name: 'ordered-handshake', version: '1' } });
    race.child.stdin.cork();
    const before = race.call('tools/list', {});
    race.send({ jsonrpc: '2.0', method: 'notifications/initialized', params: {} });
    const after = race.call('tools/list', {});
    race.child.stdin.uncork();
    assert.equal((await before).error?.code, -32600, 'Premature notification cannot admit an early request');
    assert.equal((await after).result?.tools.length, 15, 'Immediate legal tools/list must survive reordered handlers');
    assert.equal(await race.close(), 0);
  }
  console.log('Raw stdio: initialization, version negotiation, ping, errors, cancellation, malformed input and EOF passed.');
} finally { for (const child of children) child.kill(); }
