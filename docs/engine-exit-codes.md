# Engine bootstrap exit codes

The architecture-specific Engine bootstrap returns one JSON object on stdout for success and one JSON error object on stderr for failure. Product Host launches it with `--hold-session`; the Engine then owns the live debugging relationship until Host closes Engine stdin. Direct one-shot invocations detach after emitting the bootstrap result.

| Exit code | Error codes | Operator action |
|---:|---|---|
| 0 | success | Read `processId`, `architecture`, `runtimeVersion`, and `sessionState`. |
| 2 | `invalid_request`, `invalid_executable`, `target_not_found` | Correct the path, PID, or arguments. |
| 10 | `architecture_mismatch`, `unsupported_architecture` | Route to the Engine matching the target architecture. |
| 11 | `not_managed_process` | Select a process that has loaded, or whose managed image will load, the Desktop CLR. |
| 12 | `core_clr_not_supported` | Use a CoreCLR-compatible debugger instead. |
| 13 | `unsupported_clr_version` | Select a CLR v4.0.30319 target. |
| 14 | `access_denied` | Run with sufficient rights to inspect and attach to the target. |
| 15 | `operation_timed_out` | Increase the timeout or inspect a hung target. |
| 20 | `internal_error` and other failures | Inspect Engine diagnostics; the target is detached during cleanup. |

`arch=auto` is resolved by `FxDbg.Host` before an Engine process is started. An Engine accepts only its own explicit architecture and never attempts a cross-bitness attach.
