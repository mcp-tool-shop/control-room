# Control Room

## One-Line Pitch

Local-first desktop app that turns scripts into observable, repeatable operations with failure tracking and run profiles.

---

## What It Is

Control Room is a Windows desktop application for executing scripts with full observability. Instead of running scripts in a terminal and losing the output, Control Room:

1. **Logs everything** — stdout, stderr, exit codes, timing, artifacts
2. **Tracks failures** — Groups recurring errors by fingerprint
3. **Supports profiles** — Preset arg/env combinations per script
4. **Enables replay** — Export runs as ZIPs for sharing/debugging

---

## Who It's For

- **ML practitioners** — Track training runs locally without cloud dependencies
- **DevOps engineers** — Manage deployment scripts with full audit trails
- **Developers** — Run build/test scripts with organized configurations
- **Anyone** who runs scripts repeatedly and wants to stop losing output

---

## Key Features

### Profiles
Define preset configurations:
```
train-model
├── Default      (no args)
├── Smoke        --epochs=1 --subset=100
├── Full         --epochs=50 --wandb
└── Debug        --verbose  DEBUG=1
```

### Failure Fingerprinting
Errors are hashed by signature. See:
- How many times each error occurred
- First and last occurrence
- All runs with the same error

### Command Palette
Keyboard-driven (`Ctrl+K`):
- `run train` → shows all profiles
- `retry` → re-runs with same profile
- `tail` → jumps to latest failure

### Evidence-Grade Runs
Every run records:
- Profile used
- Resolved arguments
- Environment overrides
- Full output streams
- Artifacts collected

### ZIP Export
One-click export includes:
- `run-info.json` — Full metadata
- `stdout.txt` / `stderr.txt`
- `events.jsonl` — Machine-readable log
- `artifacts/` — Collected files

---

## Tech Stack

| Component | Technology |
|-----------|------------|
| UI | .NET MAUI (Windows) |
| Storage | SQLite (WAL mode) |
| MVVM | CommunityToolkit.Mvvm |

---

## License

MIT — Free for personal and commercial use.

---

## Links

- **Repository**: https://github.com/mcp-tool-shop/control-room
- **Issues**: https://github.com/mcp-tool-shop/control-room/issues
- **Releases**: https://github.com/mcp-tool-shop/control-room/releases
