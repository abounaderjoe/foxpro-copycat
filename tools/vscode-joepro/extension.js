// Joe Pro VS Code extension: starts `joepro lsp` for language features and points the
// debugger contribution at `joepro dap` (using the configured executable).
const vscode = require('vscode');
const { LanguageClient } = require('vscode-languageclient/node');

let client;

function activate(context) {
  const exe = vscode.workspace.getConfiguration('joepro').get('executable') || 'joepro';
  client = new LanguageClient(
    'joepro',
    'Joe Pro',
    { command: exe, args: ['lsp'] },
    { documentSelector: [{ language: 'foxpro' }] }
  );
  client.start();
  context.subscriptions.push(
    vscode.debug.registerDebugAdapterDescriptorFactory('joepro', {
      createDebugAdapterDescriptor: () => new vscode.DebugAdapterExecutable(exe, ['dap'])
    })
  );
}

function deactivate() {
  return client ? client.stop() : undefined;
}

module.exports = { activate, deactivate };
