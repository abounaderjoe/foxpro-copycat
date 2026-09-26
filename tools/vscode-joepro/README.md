# Joe Pro for VS Code

Language support and debugging for FoxPro code, powered by the `joepro` command-line tool.

- Diagnostics, completion, hover and go to definition through `joepro lsp`
- Breakpoints (including conditions), stepping, call stack, variables and a debug console through `joepro dap`

**Status: untested scaffold.** The `joepro lsp` and `joepro dap` servers are covered by Joe Pro's
test suite, but this extension has not yet been run inside VS Code.

## Try it

1. Build Joe Pro and put `joepro` on your PATH (or set `joepro.executable`).
2. `npm install` in this folder, then press F5 in VS Code to start an Extension Development Host.
3. Open a folder with `.prg` files; use the "Run current program" launch configuration to debug.
