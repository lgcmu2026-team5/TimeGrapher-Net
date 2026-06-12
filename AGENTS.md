# Agent Guide

## Context

- This project is not just software for distribution — it is a **team-project deliverable evaluated in a software architecture course**.
- It is a port of the original Qt/C++ version (TimeGrapher) to **Avalonia + C# (.NET 8)**, supporting Windows and Raspberry Pi 5 (linux-arm64) from a single codebase.
- For evaluation and interviews, a human must be able to read and trace the accumulated change history, so **every change must make its rationale visible in the history.**

## Scope

- For any code change or implementation request, always keep the change **minimal** within the required scope.
- Do not add **exception handling or fallback logic** that was not explicitly requested.
- Do not perform **refactoring for structural or performance improvement** that was not explicitly requested.
- Even if you spot what looks like an obvious bug, error, or mistake outside the requested scope, **do not fix it on your own — notify the user** and let them decide. However, if the requested change cannot be completed without fixing that bug, fix it as part of the work (and state so in the commit).

## Commits

- Always split commits into the **smallest logically separable units**.
- Write the commit **subject in English**, following the **Conventional Commits** spec.
  - Format: `<type>(<scope>): <description>` — scope is optional (e.g. `feat(splash):`, `fix(install.sh):`, `docs:`, `chore:`, `test:`, `ci:`, `build:`).
  - `<type>` is lowercase.
- Write the commit body in **both Korean and English**, in the following format (English first, then Korean):

  ```
  [en] English description of the change
  continued in English...

  [ko] 변경 내용에 대한 한글 설명
  한글 설명 계속...
  ```

- For changes that affect the software architecture or design pattern, state in the body **which software architecture theory or tactic the change is based on**, and update the corresponding architecture view document under `docs/` when needed.

## Principles

- Base every change on **software architecture principles and the existing structure**.
- The architecture and its decisions are documented under `docs/` — check the relevant views before making changes:
  - `docs/MODULE_DECOMPOSITION_VIEW.md`, `docs/MODULE_USES_VIEW.md`, `docs/LAYERED_VIEW.md`, `docs/MVC_VIEW.md`, `docs/DATA_MODEL_VIEW.md`
  - `docs/SAP_TACTICS_ANALYSIS.md` (quality-attribute tactics), `docs/QT_CPP_TO_AVALONIA_PORTING.md` (porting rationale)
- Respect the project dependency graph: `TimeGrapher.App` → `TimeGrapher.Core` / `TimeGrapher.Platform.*`, `TimeGrapher.Platform.*` → `TimeGrapher.Core`, `TimeGrapher.Verify` → `TimeGrapher.Core`. **Core must not depend on anything** (no UI or platform references).

## Build & Test

```powershell
dotnet build TimeGrapherNet.sln -c Release        # build everything
dotnet test TimeGrapherNet.sln -c Release         # run all tests (4 projects under tests/)
dotnet run --project src/TimeGrapher.App          # launch the GUI
dotnet run --project src/TimeGrapher.Verify -c Release -- --generated --byte-fixtures   # headless detection-accuracy verification
dotnet run --project src/TimeGrapher.Verify -c Release -- --ab=baseline,robust --fidelity-check  # adverse-condition A/B (baseline weakness pins vs robust profile) + all-off fidelity gate
```

- After changing code, confirm the relevant tests pass before committing.
- When adding new behavior or changing existing behavior, **add or update the tests** that cover it.
