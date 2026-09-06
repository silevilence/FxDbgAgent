'use strict';
const assert = require('assert/strict');
const fs = require('fs');
const path = require('path');
const { spawn } = require('child_process');
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
async function until(predicate, label, milliseconds = 20000) {
  const end = Date.now() + milliseconds;
  while (Date.now() < end) { const value = predicate(); if (value) return value; await delay(20); }
  throw new Error('Timed out: ' + label);
}
function fixture(root, configuration, architecture, tag) {
  const directory = path.join(root, 'artifacts/stage3-validation', `${tag}-${architecture}-${configuration}-${Date.now()}`);
  fs.mkdirSync(directory, { recursive: true });
  const source = path.join(root, 'tests/Debuggees/Shared/EndToEndScenarios.cs');
  const lines = fs.readFileSync(source, 'utf8').split(/\r?\n/);
  return { directory, source, exe: path.join(root, `tests/Debuggees/Fx40.Console.${architecture}/bin/${configuration}/net40/Fx40.Console.${architecture}.exe`),
    line: marker => lines.findIndex(line => line.includes('// ' + marker)) + 1 };
}
function startTarget(fix) {
  const child = spawn(fix.exe, ['--scenario', 'gated', fix.directory], { stdio: 'ignore', windowsHide: true });
  child.on('error', error => { child.failure = error; });
  return child;
}
async function cleanTarget(child, fix) {
  fs.writeFileSync(path.join(fix.directory, 'go'), 'go');
  if (!child) return;
  try { await until(() => child.exitCode !== null || child.signalCode !== null, 'owned target exit', 5000); }
  catch { child.kill(); await until(() => child.exitCode !== null || child.signalCode !== null, 'owned target cleanup', 5000); }
}
async function inspectAndStep(client, fix, stopped) {
  const threadId = stopped.body.threadId;
  assert.ok(threadId > 0);
  const threads = await client.request('threads');
  assert.ok(threads.threads.some(thread => thread.id === threadId));
  const stack = await client.request('stackTrace', { threadId, startFrame: 0, levels: 32 });
  assert.ok(stack.stackFrames.length >= 14);
  assert.equal(stack.stackFrames[0].line, fix.line('E2E_BREAKPOINT'));
  assert.equal(stack.stackFrames[0].source.path.toLowerCase(), fix.source.toLowerCase());
  const frameId = stack.stackFrames[0].id;
  const scopes = await client.request('scopes', { frameId });
  const rootReference = scopes.scopes[0].variablesReference;
  const roots = (await client.request('variables', { variablesReference: rootReference })).variables;
  assert.equal(roots.find(value => value.name === 'number').value, '42');
  const node = roots.find(value => value.name === 'node');
  const members = (await client.request('variables', { variablesReference: node.variablesReference })).variables;
  assert.ok(!members.some(value => value.name === 'Dangerous'));
  const array = members.find(value => value.name === 'LargeTexts');
  assert.equal(array.indexedVariables, 129);
  const page = (await client.request('variables', { variablesReference: array.variablesReference, filter: 'indexed', start: 128, count: 8 })).variables;
  assert.equal(page.length, 1); assert.equal(page[0].name, '[128]'); assert.ok(page[0].value.length < 300);
  assert.equal((await client.request('variables', { variablesReference: array.variablesReference, filter: 'named' })).variables.length, 0);
  let cursor = client.events.length;
  await client.request('next', { threadId });
  stopped = await client.event('stopped', cursor);
  await assert.rejects(client.request('scopes', { frameId }));
  await assert.rejects(client.request('variables', { variablesReference: rootReference }));
  let next = await client.request('stackTrace', { threadId, levels: 1 });
  assert.equal(next.stackFrames[0].line, fix.line('E2E_STEP_CALL'));
  cursor = client.events.length;
  await client.request('stepIn', { threadId });
  await client.event('stopped', cursor);
  next = await client.request('stackTrace', { threadId, levels: 1 });
  assert.ok(next.stackFrames[0].name.includes('AddOne'));
  cursor = client.events.length;
  await client.request('stepOut', { threadId });
  await client.event('stopped', cursor);
  next = await client.request('stackTrace', { threadId, levels: 1 });
  assert.ok(next.stackFrames[0].name.includes('Observe'));
  await assert.rejects(client.request('evaluate', { expression: 'node.Dangerous', frameId: next.stackFrames[0].id }));
}
module.exports = { assert, fs, path, spawn, delay, until, fixture, startTarget, cleanTarget, inspectAndStep };
