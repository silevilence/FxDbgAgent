# FxDbg Agent

FxDbg Agent controls one Desktop CLR target per debugging session and exposes debugger observations without executing target code.

## Language

**Debugging session**:
The lifetime of one FxDbg controller relationship with exactly one target process, identified by a session ID.
_Avoid_: Connection, debug instance

**Target process**:
The .NET Framework process controlled or observed by a debugging session.
_Avoid_: Debuggee, child process

**Engine**:
The architecture-specific process that owns all ICorDebug objects for a target process.
_Avoid_: Debugger backend, worker

**Host**:
The architecture-neutral session owner that routes requests to an Engine and never loads ICorDebug.
_Avoid_: Server, engine host

**Stop**:
A debugger-observable suspension in which inspection and execution-control commands are valid.
_Avoid_: Pause state, break state

**Resume**:
The single transition that consumes a Stop and lets the target process run again.
_Avoid_: Unpause

**Continue command**:
The externally visible execution command that requests one Resume transition for the current Stop.
_Avoid_: Run command

**Breakpoint binding**:
The relationship between a requested source location and executable IL in a loaded module.
_Avoid_: Breakpoint resolution
