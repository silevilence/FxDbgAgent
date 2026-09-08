'use strict';
const { Client } = require('./protocol');
const { assert, fs, path, until } = require('./common');
const { spawnSync } = require('child_process');
const prefix='FxDbg.Debuggees.Filtering.';

async function main() {
  const [bundle,root,configuration]=process.argv.slice(2);
  for(const architecture of ['x86','x64']) {
    const name=architecture==='x86'?'Fx40.ModuleLifecycle.x86':'Fx40.ModuleLifecycle';
    const exe=path.join(root,`tests/Debuggees/${name}/bin/${configuration}/net40/${name}.exe`);
    const directory=fs.mkdtempSync(path.join(root,'artifacts/stage4-validation/filters-dap-'));
    const client=new Client(bundle);
    const configure=condition=>client.request('setExceptionBreakpoints',{filters:[],filterOptions:[{filterId:'firstChance',condition}]});
    async function check(cursor,message) {
      const stop=await until(()=>client.events.slice(cursor).find(e=>e.event==='stopped'),'filtered DAP stop',30000);
      assert.equal(stop.body.reason,'exception');
      if(message!=='unhandled') {
        const stack=await client.request('stackTrace',{threadId:stop.body.threadId});
        const result=await client.request('evaluate',{frameId:stack.stackFrames[0].id,expression:'error._message'});
        assert.equal(result.result,message);
      }
    }
    async function next(message) { const cursor=client.events.length; await client.request('continue'); await check(cursor,message); }
    try {
      const caps=await client.request('initialize'); assert.equal(caps.supportsExceptionFilterOptions,true);
      assert.equal(caps.exceptionBreakpointFilters[0].supportsCondition,true);
      const launch=client.send('launch',{program:exe,args:['--exception-filters',directory]});
      await client.event('initialized'); await configure('exact:'+prefix+'DerivedFailure');
      await client.request('configurationDone'); await launch.completion; await check(0,'exact');
      await configure('namespace:'+prefix+'Group'); await next('namespace');
      await configure('derived:'+prefix+'BaseFailure'); await next('base'); await next('derived');
      let cursor=client.events.length; await client.request('continue');
      await until(()=>fs.existsSync(path.join(directory,'running-ready')),'running gate');
      await configure('exact:'+prefix+'Group.Failure');
      await assert.rejects(configure('exact:X*'));
      assert.equal(client.events.slice(cursor).filter(e=>e.event==='stopped').length,0);
      fs.writeFileSync(path.join(directory,'running-go'),'go'); await check(cursor,'dynamic');
      const empty=await client.request('setExceptionBreakpoints',{filters:[]}); assert.equal(empty.breakpoints.length,0);
      cursor=client.events.length; await client.request('continue');
      await until(()=>fs.existsSync(path.join(directory,'cleared-ready')),'cleared gate');
      assert.equal(client.events.slice(cursor).filter(e=>e.event==='stopped').length,0);
      await client.request('setExceptionBreakpoints',{filters:['firstChance']});
      fs.writeFileSync(path.join(directory,'cleared-go'),'go'); await check(cursor,'legacy');
      const additive=await client.request('setExceptionBreakpoints',{filters:['firstChance'],filterOptions:[{filterId:'firstChance',condition:'exact:Never'}]});
      assert.equal(additive.breakpoints.length,2); await next('disabled');
      await client.request('setExceptionBreakpoints',{filters:[]}); await next('unhandled');
      assert.equal(fs.readFileSync(path.join(directory,'oracle'),'utf8'),'ok');
      await client.request('disconnect',{terminateDebuggee:true});
      console.log(`PASS: DAP exception filters ${architecture} ${configuration}, exact/namespace/derived, running updates, additive OR, empty/default and zero formatting.`);
    } finally { await client.close(); }

    const cliDirectory=fs.mkdtempSync(path.join(root,'artifacts/stage4-validation/filters-cli-'));
    const cli=path.join(root,`src/FxDbg.Cli/bin/${configuration}/net10.0-windows/fxdbg.dll`);
    let session;
    function call(command,args=[]) {
      const run=spawnSync('dotnet',[cli,command,...(session?['--session',session]:[]),...args],{encoding:'utf8',windowsHide:true,timeout:40000});
      assert.equal(run.status,0,run.stderr||run.error?.message); return JSON.parse(run.stdout);
    }
    function config(rule) { return call('exceptions',['--first-chance','true','--exception-rules',rule]); }
    function nextCli(message) { call('continue'); const stop=call('wait',['--timeout-ms','30000']).result; assert.equal(stop.exception.message,message); return stop; }
    try {
      session=call('launch',['--exe',exe,'--arg','--exception-filters','--arg',cliDirectory,'--stop-at-entry','--engine-dir',path.join(bundle,'engines')]).sessionId;
      config('exact:'+prefix+'DerivedFailure'); nextCli('exact');
      config('namespace:'+prefix+'Group'); nextCli('namespace');
      config('derived:'+prefix+'BaseFailure'); nextCli('base'); nextCli('derived');
      call('continue'); await until(()=>fs.existsSync(path.join(cliDirectory,'running-ready')),'CLI running gate');
      config('exact:'+prefix+'Group.Failure'); fs.writeFileSync(path.join(cliDirectory,'running-go'),'go');
      assert.equal(call('wait',['--timeout-ms','30000']).result.exception.message,'dynamic');
      config(''); call('continue'); await until(()=>fs.existsSync(path.join(cliDirectory,'cleared-ready')),'CLI clear gate');
      call('exceptions',['--first-chance','true']); fs.writeFileSync(path.join(cliDirectory,'cleared-go'),'go');
      assert.equal(call('wait').result.exception.message,'legacy');
      call('exceptions',['--first-chance','false']); assert.equal(nextCli('unhandled').exception.isUnhandled,true);
      assert.equal(fs.readFileSync(path.join(cliDirectory,'oracle'),'utf8'),'ok'); call('terminate');
      console.log(`PASS: CLI exception filters ${architecture} ${configuration}, shared parser, running update, clear/default/legacy and zero formatting.`);
    } finally { if(session) spawnSync('dotnet',[cli,'detach','--session',session],{windowsHide:true,timeout:15000}); }
  }
}
main().catch(error=>{console.error(error);process.exitCode=1;});
