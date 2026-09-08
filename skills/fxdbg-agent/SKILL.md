---
name: fxdbg-agent
description: Debug local Windows .NET Framework 4.x applications through FxDbg MCP. Use for managed source breakpoints, threads, stacks, raw fields and safe exception inspection in Desktop CLR Console, WinForms, SCM Windows Service or full IIS ASP.NET processes, including x86/x64 routing and safe detach. 不适用于 CoreCLR、远程调试或函数求值。
---

# FxDbg Agent

Read [workflow](references/workflow.md) for setup and examples and [tool contract](references/mcp-tools.md) for limits and recovery. Both references ship with this skill; the installed skill does not require access to a developer's checkout.

Use the configured FxDbg MCP server and discover its actual tools. It exposes the original 13 tools plus `debug_evaluate` for bounded read-only interpretation. The debugger host must run on the same Windows machine as the target. Request the executable/source paths only if they are unavailable from the user's task or workspace; do not guess IDs or paths. Build a local checkout only when the task authorizes it.

Start with `debug_launch(exe, stopAtEntry=true)` or attach to the user-authorized PID. Save the returned sessionId and architecture. Create a source breakpoint; pending is not failure, so inspect status/symbols and resume to let modules load. Report any moved line. Continue once and inspect the newly returned stop. Get threadId from that stop/threads, frameId from stack, then read variables. Use referenceId only with its originating session and current stop. After any resume, fetch fresh frames and references.

Follow actual results, not a fixed script. `waitForStop=false` returns an operationId; query `debug_status` without issuing another continue. A fast stop may already be terminal in the first response. If a tool fails, use error.code and the contract to choose recovery; pending/unresolved symbols, invalid state and expired references require different responses. Never blindly retry an uncertain launch/attach/resume.

Default tools time out after 10 seconds. Keep variable pages small (100 members, depth 1, 256 characters initially), reducing them on an oversized-result error. A timed out execution normally pauses; inspect actual status before deciding to resume. Already returned asynchronous runs are stopped with pause or ended with detach. Cancellation requires a real MCP notification, not merely stopping a local await.

Read only: never evaluate functions, call getters or ToString, change values, set next statement, or invent COM access. First-chance exception stops are off; unhandled exception text comes from safe raw fields. Distinguish null, unavailable and optimized-away values.

For arithmetic or comparisons use debug_evaluate with a current frameId. It interprets only the documented subset inside the debugger; no target function executes. Default evaluation budget is 250 ms (maximum 1000 ms). On rejected syntax, forbidden calls, stale frames or budget errors, follow the contract; never fall back to arbitrary code, getters or writes. Object results expand with debug_variables and expire on resume.

Finish with detach so the target can continue, or terminate only when authorized and launched by this session. Attached targets cannot be terminated. Close stdin for normal transport shutdown; do not kill the process tree. Respond to standard server ping messages (normal MCP clients handle this). Report actual observations and limitations, including Engine hard-crash target-survival limits, with the tool results that support them.
