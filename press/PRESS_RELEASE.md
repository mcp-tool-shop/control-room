# Control Room Introduces Profiles: Turn Scripts into Repeatable Operations

**FOR IMMEDIATE RELEASE**

## Local-first desktop app now supports preset run configurations for scripts and tasks

Control Room, the open-source desktop application for managing and executing scripts with full observability, today announced the release of **Profiles** — a feature that transforms scripts from one-off commands into organized, repeatable operations.

### The Problem

Developers and ML practitioners often run the same scripts with different configurations:
- `python train.py --epochs=1` for quick smoke tests
- `python train.py --epochs=50 --wandb` for full training runs
- `python train.py --verbose DEBUG=1` for debugging

These variations live in shell history, scattered notes, or team memory. When something fails, reproducing the exact conditions becomes detective work.

### The Solution: Profiles

Profiles let you define preset argument and environment combinations directly in Control Room:

| Profile | Args | Environment |
|---------|------|-------------|
| Default | (none) | — |
| Smoke | `--epochs=1 --subset=100` | — |
| Full | `--epochs=50 --wandb` | `WANDB_PROJECT=prod` |
| Debug | `--verbose --no-cache` | `DEBUG=1` |

Each profile appears as a separate command in the palette. Type "run train" and see:
- Run: train-model
- Run: train-model (Smoke)
- Run: train-model (Full)

### Evidence-Grade Tracking

Every run records which profile was used, along with:
- Resolved arguments
- Environment variable overrides
- Working directory
- Full stdout/stderr
- Exit code and timing

When a run fails, the "Retry" command automatically uses the same profile. Failure fingerprinting groups recurring errors across profile variants.

### Key Features

- **Schema-versioned config** — Automatic migration as the format evolves
- **Profile inheritance** — Each profile can override working directory
- **Command palette integration** — Keyboard-driven execution with fuzzy search
- **ZIP export** — Share runs with full metadata for reproduction

### Availability

Control Room with Profiles is available now under the MIT license at:
https://github.com/mcp-tool-shop/control-room

### About Control Room

Control Room is a local-first desktop application that brings CI/CD-style observability to local script execution. Built with .NET MAUI for Windows, it stores all data locally in SQLite, giving users full control over their execution history.

### Contact

GitHub: https://github.com/mcp-tool-shop
Issues: https://github.com/mcp-tool-shop/control-room/issues

---

*Control Room is an open-source project maintained by mcp-tool-shop.*
