'use strict';
const vscode = require('vscode');
const { assert, fs, path, until, fixture, startTarget, cleanTarget, inspectAndStep } = require('./common');

async function run() {
  const root = process.env.FXDBG_TEST_ROOT, configuration = process.env.FXDBG_TEST_CONFIGURATION;
  const evidence = { client: 'VS Code', version: vscode.version, configuration, startedAtUtc: new Date().toISOString(), cases: [], passed: false };
  const observations = new Map();
  const terminated = new Set();
  const termination = vscode.debug.onDidTerminateDebugSession(session => terminated.add(session.id));
  const registration = vscode.debug.registerDebugAdapterTrackerFactory('fxdbg', {
    createDebugAdapterTracker(session) {
      const observed = { events: [], requests: [], responses: [], breakpointRequests: [] }; observations.set(session.id, observed);
      return {
        onWillReceiveMessage(packet) { if (packet.type === 'request') observed.requests.push(packet.command); if (packet.command === 'setBreakpoints') observed.breakpointRequests.push(packet.arguments); },
        onDidSendMessage(packet) {
          if (packet.type === 'event') observed.events.push(packet);
          if (packet.type === 'response') observed.responses.push({ command: packet.command, success: packet.success });
        }
      };
    }
  });
  try {
    for (const architecture of ['x86', 'x64']) for (const attach of [false, true]) {
      const fix = fixture(root, configuration, architecture, 'vscode'); let child, session;
      const breakpoint = new vscode.SourceBreakpoint(new vscode.Location(vscode.Uri.file(fix.source), new vscode.Position(fix.line('E2E_BREAKPOINT') - 1, 0)));
      vscode.debug.addBreakpoints([breakpoint]);
      try {
        if (attach) { child = startTarget(fix); await until(() => fs.existsSync(path.join(fix.directory, 'ready')), 'VS Code attach target ready'); }
        const configurationObject = { type: 'fxdbg', name: `FxDbg validation ${architecture}`, request: attach ? 'attach' : 'launch',
          adapterPath: path.join(process.env.FXDBG_TEST_BUNDLE, 'fxdbg-dap.dll'), dotnetPath: process.env.FXDBG_DOTNET,
          ...(attach ? { processId: child.pid } : { program: fix.exe, args: ['--scenario', 'gated', fix.directory] }) };
        assert.equal(await vscode.debug.startDebugging(undefined, configurationObject), true);
        session = await until(() => vscode.debug.activeDebugSession?.type === 'fxdbg' && vscode.debug.activeDebugSession, 'VS Code session');
        const observed = observations.get(session.id);
        const client = { events: observed.events, request: (command, args = {}) => session.customRequest(command, args),
          event: (name, cursor = 0) => until(() => observed.events.slice(cursor).find(event => event.event === name), 'VS Code ' + name) };
        await until(() => observed.responses.some(response => response.command === 'configurationDone' && response.success), 'VS Code configurationDone');
        await until(() => fs.existsSync(path.join(fix.directory, 'ready')), 'VS Code target ready');
        let cursor = client.events.length; await client.request('pause'); await client.event('stopped', cursor);
        fs.writeFileSync(path.join(fix.directory, 'go'), 'go');
        cursor = client.events.length; await client.request('continue');
        const stopped = await client.event('stopped', cursor); assert.equal(stopped.body.reason, 'breakpoint');
        await inspectAndStep(client, fix, stopped);
        if (attach) await assert.rejects(client.request('terminate'));
        cursor = client.events.length; await client.request('disconnect', { terminateDebuggee: false }); await client.event('terminated', cursor);
        await until(() => terminated.has(session.id), 'VS Code session disposal');
        await until(() => fs.existsSync(path.join(fix.directory, 'completed')), 'VS Code detach resumes target');
        if (child) { await until(() => child.exitCode !== null, 'VS Code target exit'); assert.equal(child.exitCode, 0); }
        evidence.cases.push({ architecture, mode: attach ? 'attach' : 'launch', passed: true, requests: observed.requests,
          responses: observed.responses, events: observed.events.map(event => ({ event: event.event, reason: event.body?.reason })) });
      } finally {
        vscode.debug.removeBreakpoints([breakpoint]);
        if (session && !terminated.has(session.id)) await vscode.debug.stopDebugging(session);
        await cleanTarget(child, fix);
      }
    }
    evidence.passed = true;
  } catch (error) { evidence.error = error.stack; throw error; }
  finally {
    registration.dispose(); termination.dispose(); evidence.finishedAtUtc = new Date().toISOString();
    if (!evidence.passed) evidence.failedObservations = [...observations.values()];
    fs.writeFileSync(path.join(root, `artifacts/stage3-validation/vscode-${configuration}.json`), JSON.stringify(evidence, null, 2));
  }
}
module.exports = { run };
