'use strict';
const vscode = require('vscode');
const { assert, fs, path, until } = require('./common');
const { target, readWeb, assertResumed } = require('./service-iis-common');
async function run() {
  const root = process.env.FXDBG_TEST_ROOT, configuration = process.env.FXDBG_TEST_CONFIGURATION;
  const resources = JSON.parse(fs.readFileSync(process.env.FXDBG_ENVIRONMENT_MANIFEST, 'utf8')).resources;
  const evidence = { client: 'VS Code', version: vscode.version, configuration, cases: [], passed: false };
  const observations = new Map(), terminated = new Set();
  const termination = vscode.debug.onDidTerminateDebugSession(session => terminated.add(session.id));
  const registration = vscode.debug.registerDebugAdapterTrackerFactory('fxdbg', {
    createDebugAdapterTracker(session) {
      const seen = { events: [], responses: [], requests: [] }; observations.set(session.id, seen);
      return {
        onWillReceiveMessage(packet) { if (packet.type === 'request') seen.requests.push(packet.command); },
        onDidSendMessage(packet) {
          if (packet.type === 'event') seen.events.push(packet);
          if (packet.type === 'response') seen.responses.push({ command: packet.command, success: packet.success });
        }
      };
    }
  });
  try {
    for (const resource of resources) for (const kind of ['service', 'iis']) {
      const fix = await target(root, resource, kind);
      const breakpoint = new vscode.SourceBreakpoint(new vscode.Location(vscode.Uri.file(fix.source), new vscode.Position(fix.line - 1, 0)));
      let session, request;
      vscode.debug.addBreakpoints([breakpoint]);
      try {
        assert.equal(await vscode.debug.startDebugging(undefined, { type: 'fxdbg', name: `FxDbg ${kind} ${resource.architecture}`,
          request: 'attach', processId: fix.pid, adapterPath: path.join(process.env.FXDBG_TEST_BUNDLE, 'fxdbg-dap.dll'), dotnetPath: process.env.FXDBG_DOTNET }), true);
        session = await until(() => vscode.debug.activeDebugSession?.type === 'fxdbg' && vscode.debug.activeDebugSession, 'VS Code attached session');
        const seen = observations.get(session.id);
        await until(() => seen.responses.some(response => response.command === 'configurationDone' && response.success), 'VS Code configurationDone');
        if (kind === 'iis') { request = readWeb(resource.url); request.catch(() => {}); }
        const stopped = await until(() => seen.events.find(event => event.event === 'stopped'), 'VS Code fixture breakpoint');
        assert.equal(stopped.body.reason, 'breakpoint');
        const stack = await session.customRequest('stackTrace', { threadId: stopped.body.threadId, levels: 8 });
        assert.equal(stack.stackFrames[0].line, fix.line);
        assert.equal(stack.stackFrames[0].source.path.toLowerCase(), fix.source.toLowerCase());
        await session.customRequest('disconnect', { terminateDebuggee: false });
        await until(() => terminated.has(session.id), 'VS Code session disposal');
        await assertResumed(fix, request);
        evidence.cases.push({ kind, architecture: resource.architecture, pid: fix.pid, line: fix.line,
          requests: seen.requests, responses: seen.responses, events: seen.events.map(event => ({ event: event.event, reason: event.body?.reason })) });
      } finally {
        vscode.debug.removeBreakpoints([breakpoint]);
        if (session && !terminated.has(session.id)) await vscode.debug.stopDebugging(session);
        if (request) await request.catch(() => {});
      }
    }
    assert.equal(evidence.cases.length, 4); evidence.passed = true;
  } catch (error) { evidence.error = error.stack; throw error; }
  finally {
    registration.dispose(); termination.dispose(); evidence.finishedAtUtc = new Date().toISOString();
    fs.writeFileSync(path.join(root, `artifacts/stage3-4-validation/vscode-service-iis-${configuration}.json`), JSON.stringify(evidence, null, 2));
  }
}
module.exports = { run };
