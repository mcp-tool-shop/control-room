# Changelog

All notable changes to Control Room will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.0] - 2025-01-29

### Added

#### Profiles Feature
- **ThingConfig schema** with versioning (v1 → v2 migration)
- **ThingProfile** model: Id, Name, Args, Env (dictionary), WorkingDir override
- **ExecuteWithProfileAsync** in RunLocalScript for profile-aware execution
- **Profile resolution**: explicit profileId → default profile → fallback
- **Args resolution**: override → profile.Args → empty string
- **Environment merge**: Control Room vars + profile env overrides
- **Command palette** shows profile variants (e.g., "Run: hello (Smoke)")
- **Profile subtitles** show args preview, env count, custom cwd indicator
- **Retry command** preserves the profile from the failed run
- **NewThingPage** with collapsible profiles editor
- **BoolToExpandIconConverter** for UI expand/collapse state

#### Evidence-Grade Runs
- RunSummary now includes ProfileId, ProfileName, ArgsResolved, EnvOverrides
- Full reproducibility metadata stored with each run

#### Failure Groups
- Fingerprint-based grouping of recurring failures
- "All Runs" button filters Timeline by fingerprint
- Recurrence count displayed in palette and Timeline

#### ZIP Export
- Safe filename sanitization
- Fallback directory chain (Desktop → Documents → Temp)
- `run-info.json` with comprehensive metadata
- `events.jsonl` machine-readable event stream

#### Timeline Filtering
- IQueryAttributable for fingerprint query parameter
- Filter chip UI with clear button
- Navigation from Failures page to filtered Timeline

### Infrastructure
- SQLite with WAL mode for concurrent reads
- .NET MAUI for Windows desktop UI
- CommunityToolkit.Mvvm with source generators

---

## [Unreleased]

### Planned
- Profile import/export
- Artifact rules per profile
- Timeout configuration
- macOS support
