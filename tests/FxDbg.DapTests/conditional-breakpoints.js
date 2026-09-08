'use strict';
const { Client } = require('./protocol');
const { assert, fs, path, until } = require('./common');
const { spawnSync } = require('child_process');

async function main() {
  const [bundle,root,configuration]=process.argv.slice(2);
  const cases=JSON.parse(fs.readFileSync(path.join(root,'tests/Debuggees/conditional-breakpoint-cases.json'),'utf8'));
  const source=path.join(root,'tests/Debuggees/Fx40.ModuleLifecycle/ConditionalBreakpointScenarios.cs');
  const lines=fs.readFileSync(source,'utf8').split(/\r?\n/);
  const line=lines.findIndex(text=>text.includes('// CONDITIONAL_HIT'))+1;
  const extraLine=lines.findIndex(text=>text.includes('File.WriteAllText'))+1;
  for(const architecture of ['x86','x64']) for(const test of cases) {
    const name=architecture==='x86'?'Fx40.ModuleLifecycle.x86':'Fx40.ModuleLifecycle';
    const exe=path.join(root,`tests/Debuggees/${name}/bin/${configuration}/net40/${name}.exe`);
    const directory=fs.mkdtempSync(path.join(root,'artifacts/stage4-validation/conditional-entries-'));
    const oracle=path.join(directory,'dap-ok');
    const client=new Client(bundle);
    const request={line}; for(const field of ['condition','hitCondition']) if(test[field]!==undefined) request[field]=test[field];
    const set=breakpoints=>client.request('setBreakpoints',{source:{path:source},breakpoints});
    let cursor=0;
    async function event() { return until(()=>client.events.slice(cursor).find(e=>['stopped','terminated'].includes(e.event)),'conditional stop/exit',30000); }
    try {
      const caps=await client.request('initialize'); assert.equal(caps.supportsConditionalBreakpoints,true); assert.equal(caps.supportsHitConditionalBreakpoints,true);
      const launch=client.send('launch',{program:exe,args:['--conditional-breakpoints',oracle]}); await client.event('initialized');
      const point=(await set([request])).breakpoints[0];
      await assert.rejects(set([{line,hitCondition:'=0'}]));
      await client.request('configurationDone'); await launch.completion;
      for(const iteration of test.stops) {
        const stopped=await event(); assert.equal(stopped.event,'stopped'); assert.equal(stopped.body.reason,'breakpoint');
        const frames=await client.request('stackTrace',{threadId:stopped.body.threadId});
        assert.equal((await client.request('evaluate',{frameId:frames.stackFrames[0].id,expression:'iteration'})).result,String(iteration));
        // Repeated lists and changes elsewhere must retain the original logical breakpoint/counter.
        const repeated=await set([request,{line:extraLine,condition:'false'}]); assert.equal(repeated.breakpoints[0].id,point.id);
        if(test.diagnostic) { assert.match(repeated.breakpoints[0].message,/stopped conservatively/); await set([]); }
        cursor=client.events.length; await client.request('continue');
      }
      assert.equal((await event()).event,'terminated','No hidden condition stops, including after resubmitting a list.');
      assert.equal(client.events.filter(e=>e.event==='stopped').length,test.stops.length);
      assert.equal(fs.readFileSync(oracle,'utf8'),'ok');
      console.log(`PASS: DAP conditional ${architecture} ${configuration} ${test.name}, exact stops, stable IDs/counters, diagnostics and state oracle.`);
    } finally { await client.close(); }

    const cli=path.join(root,`src/FxDbg.Cli/bin/${configuration}/net10.0-windows/fxdbg.dll`);
    const cliOracle=path.join(directory,'cli-ok'); let session;
    function call(command,args=[]) {
      const run=spawnSync('dotnet',[cli,command,...(session?['--session',session]:[]),...args],{encoding:'utf8',windowsHide:true,timeout:30000});
      assert.equal(run.status,0,run.stderr||run.error?.message); return JSON.parse(run.stdout);
    }
    try {
      session=call('launch',['--exe',exe,'--arg','--conditional-breakpoints','--arg',cliOracle,'--stop-at-entry','--engine-dir',path.join(bundle,'engines')]).sessionId;
      const args=['--file',source,'--line',String(line)];
      if(test.condition!==undefined) args.push('--condition',test.condition);
      if(test.hitCondition!==undefined) args.push('--hit-condition',test.hitCondition);
      const point=call('break',args).result; assert.equal(point.hitCount,0);
      for(const iteration of test.stops) {
        call('continue'); const stopped=call('wait').result; assert.equal(stopped.reason,'breakpoint'); assert.equal(stopped.breakpointId,point.breakpointId);
        const current=call('breakpoints').result[0]; assert.equal(current.hitCount,iteration); assert.equal(!!current.conditionDiagnostic,!!test.diagnostic);
        const frame=call('stack',['--thread',String(stopped.threadId),'--count','1']).result[0].frameId;
        assert.equal(call('evaluate',['--frame',frame,'--expression','iteration']).result.displayValue,String(iteration));
        if(test.diagnostic) call('enable-break',['--breakpoint',point.breakpointId,'--enabled','false']);
      }
      call('continue'); assert.equal(call('wait').result.reason,'processExit');
      assert.equal(call('breakpoints').result[0].hitCount,test.diagnostic?1:6);
      assert.equal(fs.readFileSync(cliOracle,'utf8'),'ok');
      console.log(`PASS: CLI conditional ${architecture} ${configuration} ${test.name}, shared conditions, counters, diagnostics and state oracle.`);
    } finally { if(session) spawnSync('dotnet',[cli,'detach','--session',session],{windowsHide:true,timeout:15000}); }
  }
}
main().catch(error=>{console.error(error);process.exitCode=1;});
