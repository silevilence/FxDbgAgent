'use strict';
const vscode = require('vscode');
const path = require('path');
const fs = require('fs');

function activate(context) {
  context.subscriptions.push(vscode.debug.registerDebugAdapterDescriptorFactory('fxdbg', {
    createDebugAdapterDescriptor(session) {
      if (process.platform !== 'win32') throw new Error('FxDbg requires Windows.');
      const settings = vscode.workspace.getConfiguration('fxdbg');
      const adapter = session.configuration.adapterPath || settings.get('adapterPath');
      const dotnet = session.configuration.dotnetPath || settings.get('dotnetPath') || 'dotnet';
      if (!adapter || !path.isAbsolute(adapter) || !fs.existsSync(adapter))
        throw new Error('Publish eng/publish-dap.ps1 and set fxdbg.adapterPath to the absolute fxdbg-dap.dll path.');
      return new vscode.DebugAdapterExecutable(dotnet, [adapter], { cwd: path.dirname(adapter) });
    }
  }));
}
module.exports = { activate };
