'use strict';
const { Client } = require('./protocol');
const { assert, fs, path, until } = require('./common');
const { spawnSync } = require('child_process');

async function main() {
  const [bundle, root, configuration] = process.argv.slice(2);
  const source = path.join(root,'tests/Debuggees/Fx40.ModuleLifecycle/Program.cs');
  const line = fs.readFileSync(source,'utf8').split(/\r?\n/).findIndex(line=>line.includes('// EVALUATION_BREAKPOINT'))+1;
  for (const architecture of ['x86','x64']) {
    const name=architecture==='x86'?'Fx40.ModuleLifecycle.x86':'Fx40.ModuleLifecycle';
    const exe=path.join(root,`tests/Debuggees/${name}/bin/${configuration}/net40/${name}.exe`);
    const directory=fs.mkdtempSync(path.join(root,'artifacts/stage4-validation/entry-evaluation-'));
    const dapOracle=path.join(directory,'dap-ok'), cliOracle=path.join(directory,'cli-ok');
    const client=new Client(bundle);
    try {
      const caps=await client.request('initialize'); assert.equal(caps.supportsEvaluateForHovers,true);
      const launch=client.send('launch',{program:exe,args:['--evaluation',dapOracle]});
      await client.event('initialized'); await client.request('setBreakpoints',{source:{path:source},breakpoints:[{line}]});
      await client.request('configurationDone'); await launch.completion;
      const stop=await client.event('stopped'); assert.equal(stop.body.reason,'breakpoint');
      const stack=await client.request('stackTrace',{threadId:stop.body.threadId}); const frameId=stack.stackFrames[0].id;
      for(const [expression,expected] of [['number + 2','44'],['matrix[1,1]','40'],['shifted[-1,7]','99'],['userCodeCalls','0']]) {
        const result=await client.request('evaluate',{frameId,expression,context:'watch'}); assert.equal(result.result,expected);
      }
      const object=await client.request('evaluate',{frameId,expression:'node'}); assert.ok(object.variablesReference>0);
      const children=await client.request('variables',{variablesReference:object.variablesReference}); assert.ok(children.variables.some(x=>x.name==='Label'&&x.value==='node-label'));
      await assert.rejects(client.request('evaluate',{frameId,expression:'node.ToString()'}),/expression_forbidden/);
      let cursor=client.events.length; await client.request('next',{threadId:stop.body.threadId}); await client.event('stopped',cursor);
      await assert.rejects(client.request('evaluate',{frameId,expression:'number'}));
      cursor=client.events.length; await client.request('continue'); await client.event('terminated',cursor);
      await until(()=>fs.existsSync(dapOracle),'DAP target state oracle'); assert.equal(fs.readFileSync(dapOracle,'utf8'),'ok');
      console.log(`PASS: DAP evaluation ${configuration} ${architecture}, watch, object paging, rejection, stale frame, state oracle.`);
    } finally { await client.close(); }

    // CLI is a separate persistent Host using the same Engine command.
    const cli=path.join(root,`src/FxDbg.Cli/bin/${configuration}/net10.0-windows/fxdbg.dll`);
    let session;
    function call(command,args=[]) {
      const run=spawnSync('dotnet',[cli,command,...(session?['--session',session]:[]),...args],{encoding:'utf8',windowsHide:true,timeout:30000});
      assert.equal(run.status,0,run.stderr||run.error?.message); return JSON.parse(run.stdout);
    }
    try {
      session=call('launch',['--exe',exe,'--arg','--evaluation','--arg',cliOracle,'--stop-at-entry','--engine-dir',path.join(bundle,'engines')]).sessionId;
      call('break',['--file',source,'--line',String(line)]); call('continue');
      const stop=call('wait').result; const frame=call('stack',['--thread',String(stop.threadId)]).result[0].frameId;
      assert.equal(call('evaluate',['--frame',frame,'--expression','number + matrix[1,1]']).result.displayValue,'82');
      assert.equal(call('evaluate',['--frame',frame,'--expression','userCodeCalls']).result.displayValue,'0');
      call('continue'); const exited=call('wait').result; assert.equal(exited.reason,'processExit');
      assert.equal(fs.readFileSync(cliOracle,'utf8'),'ok');
      console.log(`PASS: CLI evaluation ${configuration} ${architecture}, shared Engine path and target state oracle.`);
    } finally { if(session) { spawnSync('dotnet',[cli,'detach','--session',session],{windowsHide:true,timeout:15000}); } }
  }
}
main().catch(error=>{console.error(error);process.exitCode=1;});
