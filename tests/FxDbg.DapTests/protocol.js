'use strict';
const { assert, fs, path, spawn, delay, until, fixture, startTarget, cleanTarget, inspectAndStep } = require('./common');
class Client {
  constructor(bundle) {
    this.child = spawn(process.env.FXDBG_DOTNET || 'dotnet', [path.join(bundle, 'fxdbg-dap.dll')], { stdio: 'pipe', windowsHide: true });
    this.events = []; this.pending = new Map(); this.sequence = 0; this.serverSequence = 0; this.buffer = Buffer.alloc(0); this.stderr = '';
    this.child.stderr.on('data', bytes => { this.stderr += bytes; });
    this.child.stdout.on('data', bytes => { try { this.read(bytes); } catch (error) { this.failure = error; } });
    this.child.on('error', error => { this.failure = error; });
    this.child.on('exit', () => { for (const entry of this.pending.values()) entry.closed = true; });
  }
  read(bytes) {
    this.buffer = Buffer.concat([this.buffer, bytes]);
    while (true) {
      const end = this.buffer.indexOf('\r\n\r\n'); if (end < 0) { assert.ok(this.buffer.length <= 8192); return; }
      const match = /^Content-Length: (\d+)$/.exec(this.buffer.subarray(0, end).toString('ascii')); assert.ok(match, 'stdout contains only DAP');
      const length = Number(match[1]); assert.ok(length > 0 && length <= 4 * 1024 * 1024);
      if (this.buffer.length < end + 4 + length) return;
      const packet = JSON.parse(this.buffer.subarray(end + 4, end + 4 + length).toString('utf8')); this.buffer = this.buffer.subarray(end + 4 + length);
      assert.ok(packet.seq > this.serverSequence); this.serverSequence = packet.seq;
      if (packet.type === 'event') this.events.push(packet);
      else { assert.equal(packet.type, 'response'); const entry = this.pending.get(packet.request_seq); assert.ok(entry, 'exactly one response per request'); this.pending.delete(packet.request_seq); entry.packet = packet; }
    }
  }
  send(command, args = {}) {
    const seq = ++this.sequence; const entry = {}; this.pending.set(seq, entry);
    const bytes = Buffer.from(JSON.stringify({ seq, type: 'request', command, arguments: args }));
    this.child.stdin.write(`Content-Length: ${bytes.length}\r\n\r\n`); this.child.stdin.write(bytes);
    const completion = until(() => { if (this.failure) throw this.failure; if (entry.closed) throw new Error('DAP connection closed'); return entry.packet; }, command, 40000).then(packet => {
      if (!packet.success) throw new Error(packet.message + ': ' + packet.body?.error?.format);
      return packet.body;
    });
    // Callers may deliberately defer awaiting launch until after configurationDone.
    completion.catch(() => {});
    return { seq, completion };
  }
  request(command, args) { return this.send(command, args).completion; }
  event(name, cursor = 0) { return until(() => { if (this.failure) throw this.failure; return this.events.slice(cursor).find(packet => packet.event === name); }, name); }
  async close() {
    this.child.stdin.end();
    try { await until(() => this.child.exitCode !== null, 'DAP EOF cleanup', 12000); }
    catch (error) { this.child.kill(); throw error; }
    if (this.failure) throw this.failure;
    assert.equal(this.child.exitCode, 0, this.stderr);
  }
}
async function session(bundle, root, configuration, architecture, attach) {
  const fix = fixture(root, configuration, architecture, 'dap'); const client = new Client(bundle); let child;
  try {
    if (attach) { child = startTarget(fix); await until(() => fs.existsSync(path.join(fix.directory, 'ready')), 'attach readiness'); }
    const caps = await client.request('initialize', { adapterID: 'fxdbg', pathFormat: 'path', supportsVariablePaging: true });
    assert.equal(caps.supportsConfigurationDoneRequest, true); assert.ok(!caps.supportsSetVariable && caps.supportsEvaluateForHovers);
    const launch = client.send(attach ? 'attach' : 'launch', attach ? { processId: child.pid } : { program: fix.exe, args: ['--scenario', 'gated', fix.directory] });
    await client.event('initialized');
    await assert.rejects(client.request('setBreakpoints', { source: { path: fix.source }, breakpoints: [{ line: fix.line('E2E_BREAKPOINT'), condition: 'true' }] }));
    const breaks = await client.request('setBreakpoints', { source: { path: fix.source }, breakpoints: [{ line: fix.line('E2E_BREAKPOINT') }] });
    assert.equal(breaks.breakpoints.length, 1);
    await client.request('configurationDone'); await launch.completion;
    await until(() => fs.existsSync(path.join(fix.directory, 'ready')), 'launch readiness');
    let cursor = client.events.length;
    await client.request('pause'); await client.event('stopped', cursor);
    fs.writeFileSync(path.join(fix.directory, 'go'), 'go');
    cursor = client.events.length; await client.request('continue');
    const stopped = await client.event('stopped', cursor); assert.equal(stopped.body.reason, 'breakpoint');
    await inspectAndStep(client, fix, stopped);
    if (attach) await assert.rejects(client.request('terminate'));
    const removed = await client.request('setBreakpoints', { source: { path: fix.source }, breakpoints: [] }); assert.equal(removed.breakpoints.length, 0);
    cursor = client.events.length; await client.request('disconnect', { terminateDebuggee: false }); await client.event('terminated', cursor);
    await until(() => fs.existsSync(path.join(fix.directory, 'completed')), 'safe detach resumes target');
    if (child) { await until(() => child.exitCode !== null, 'attached target exit'); assert.equal(child.exitCode, 0); }
    console.log(`DAP ${configuration} ${architecture} ${attach ? 'attach' : 'launch'}: initialize, pending/configuration, pause, breakpoint, 14-frame stack, paging, next/stepIn/stepOut, stale handles and safe detach passed.`);
  } finally { await client.close(); await cleanTarget(child, fix); }
}
async function cancellation(bundle, root, configuration) {
  const fix = fixture(root, configuration, 'x64', 'dap-cancel'); const client = new Client(bundle);
  try {
    await client.request('initialize');
    const launch = client.send('launch', { program: fix.exe, args: ['--scenario', 'gated', fix.directory] });
    await client.event('initialized');
    await client.request('cancel', { requestId: launch.seq });
    await assert.rejects(launch.completion, /operation_cancelled/);
    await client.event('terminated');
    fs.writeFileSync(path.join(fix.directory, 'go'), 'go');
    await until(() => fs.existsSync(path.join(fix.directory, 'completed')), 'cancelled launch detaches');
  } finally { await client.close(); await cleanTarget(null, fix); }
  console.log('DAP cancellation during configuration: one failed launch response, terminated event, target resumed.');
}
async function eof(bundle, root, configuration) {
  const fix = fixture(root, configuration, 'x64', 'dap-eof'); const child = startTarget(fix); const client = new Client(bundle);
  try {
    await until(() => fs.existsSync(path.join(fix.directory, 'ready')), 'EOF attach readiness');
    await client.request('initialize');
    client.send('attach', { processId: child.pid }); await client.event('initialized');
    await client.close(); assert.equal(child.exitCode, null, 'EOF never kills attached target');
    fs.writeFileSync(path.join(fix.directory, 'go'), 'go');
    await until(() => child.exitCode !== null, 'EOF safe detach'); assert.equal(child.exitCode, 0);
  } finally { if (client.child.exitCode === null) await client.close(); await cleanTarget(child, fix); }
  console.log('DAP EOF during attached configuration: Engine cleanup and live target continuation passed.');
}
async function boundaries(bundle, root, configuration) {
  const fix = fixture(root, configuration, 'x64', 'dap-boundaries'); const client = new Client(bundle);
  try {
    await client.request('initialize');
    const launch = client.send('launch', { program: fix.exe, args: ['--scenario', 'paging', fix.directory] });
    await client.event('initialized');
    await client.request('setBreakpoints', { source: { path: fix.source }, breakpoints: [{ line: fix.line('PAGING_BREAKPOINT') }] });
    await client.request('configurationDone'); await launch.completion;
    const stopped = await client.event('stopped');
    const stack = await client.request('stackTrace', { threadId: stopped.body.threadId });
    const scopes = await client.request('scopes', { frameId: stack.stackFrames[0].id });
    const roots = await client.request('variables', { variablesReference: scopes.scopes[0].variablesReference });
    const array = roots.variables.find(value => value.name === 'values'); assert.equal(array.indexedVariables, 100001);
    await assert.rejects(client.request('variables', { variablesReference: array.variablesReference, count: 0 }), /paging/);
    await assert.rejects(client.request('variables', { variablesReference: array.variablesReference, start: -1, count: 10 }));
    await assert.rejects(client.request('variables', { variablesReference: array.variablesReference, count: 1025 }));
    for (const start of [0, 50000, 100000]) {
      const page = await client.request('variables', { variablesReference: array.variablesReference, start, count: 5 });
      assert.equal(page.variables.length, start === 100000 ? 1 : 5); assert.equal(page.variables[0].value, String(start));
    }
    await client.request('continue'); await client.event('terminated');
    await until(() => fs.existsSync(path.join(fix.directory, 'completed')), 'natural target exit');
    console.log('DAP 100001-element paging, invalid/unpaged limits and natural exit event passed.');
  } finally { await client.close(); }
  for (const payload of ['Content-Length: 4194305\r\n\r\n', 'Content-Length: 2\r\nContent-Length: 2\r\n\r\n{}']) {
    const malformed = new Client(bundle); malformed.child.stdin.end(payload);
    await until(() => malformed.child.exitCode !== null, 'malformed transport termination');
    assert.equal(malformed.child.exitCode, 1); assert.equal(malformed.buffer.length, 0);
  }
  console.log('DAP oversized/duplicate headers fail cleanly without stdout pollution.');
}
async function deepStack(bundle, root, configuration) {
  const fix = fixture(root, configuration, 'x64', 'dap-deep'); const client = new Client(bundle);
  try {
    await client.request('initialize');
    const launch = client.send('launch', { program: fix.exe, args: ['--scenario', 'deep', fix.directory] });
    await client.event('initialized');
    await client.request('setBreakpoints', { source: { path: fix.source }, breakpoints: [{ line: fix.line('E2E_BREAKPOINT') }] });
    await client.request('configurationDone'); await launch.completion;
    const stopped = await client.event('stopped'); const threadId = stopped.body.threadId;
    await assert.rejects(client.request('stackTrace', { threadId, levels: 0 }), /paging/);
    await assert.rejects(client.request('stackTrace', { threadId, levels: 200 }), /paging/);
    let start = 0, total = 0;
    while (true) {
      const page = await client.request('stackTrace', { threadId, startFrame: start, levels: 64 });
      assert.ok(page.totalFrames >= total); total = page.totalFrames;
      if (page.stackFrames.length === 64) assert.ok(total >= start + 64);
      start += page.stackFrames.length;
      if (page.stackFrames.length < 64) break;
    }
    assert.ok(start > 180); assert.equal(total, start);
    await client.request('continue'); await client.event('terminated');
    console.log(`DAP ${start}-frame stack: bounded pages and monotonic totalFrames, oversized/unpaged rejection passed.`);
  } finally { await client.close(); }
}
async function outputFailure(bundle, root, configuration, stalled) {
  const fix = fixture(root, configuration, 'x64', stalled ? 'dap-backpressure' : 'dap-output-closed');
  const target = startTarget(fix); const client = new Client(bundle);
  try {
    await until(() => fs.existsSync(path.join(fix.directory, 'ready')), 'output failure target ready');
    await client.request('initialize');
    const attach = client.send('attach', { processId: target.pid, stopAtEntry: true });
    await client.event('initialized'); await client.request('configurationDone'); await attach.completion;
    client.child.stdout.removeAllListeners('data');
    if (stalled) client.child.stdout.pause(); else client.child.stdout.destroy();
    client.send('x'.repeat(stalled ? 1024 * 1024 : 1));
    await until(() => client.child.exitCode !== null, 'bounded output failure cleanup with stdin open', 12000);
    assert.equal(client.child.exitCode, 1); assert.equal(target.exitCode, null, 'attached target remains alive');
    client.child.stdin.end(); client.child.stdout.destroy();
    fs.writeFileSync(path.join(fix.directory, 'go'), 'go');
    await until(() => target.exitCode !== null, 'output failure safe detach'); assert.equal(target.exitCode, 0);
    console.log(`DAP ${stalled ? 'stdout backpressure' : 'stdout closed'} with stdin open: bounded adapter exit and safe attached target continuation passed.`);
  } finally {
    if (client.child.exitCode === null) { client.child.stdin.end(); await until(() => client.child.exitCode !== null, 'output test EOF cleanup', 12000); }
    await cleanTarget(target, fix);
  }
}
async function main() {
  const [bundle, root, configuration] = process.argv.slice(2);
  for (const architecture of ['x86', 'x64']) for (const attach of [false, true]) await session(bundle, root, configuration, architecture, attach);
  await cancellation(bundle, root, configuration); await eof(bundle, root, configuration); await boundaries(bundle, root, configuration);
  await deepStack(bundle, root, configuration);
  await outputFailure(bundle, root, configuration, false); await outputFailure(bundle, root, configuration, true);
}
if (require.main === module) main().catch(error => { console.error(error); process.exitCode = 1; });
module.exports = { Client };
