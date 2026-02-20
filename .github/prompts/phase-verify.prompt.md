---
description: 'Verify a workplan phase is complete before moving to the next one. Sequential enforcement.'
agent: peer
tools: ['read', 'search', 'execute']
---

# Phase Verification Checklist

Before starting the next phase, verify the current phase is COMPLETE.

## Process

1. Read `docs/SmartPiXL Authoritative WorkPlan .md`
2. Identify the current phase and its deliverables
3. For each deliverable, verify:
   - **Code exists**: files created/modified as specified
   - **Builds clean**: `dotnet build` succeeds with zero warnings
   - **Tests pass**: `dotnet test SmartPiXL.Tests/` all green
   - **SQL applied**: migration scripts executed on `localhost\SQL2025`
   - **Config synced**: all config files listed in copilot-instructions.md are consistent
   - **Deployed** (if applicable): IIS or service updated and verified
   - **Data flowing**: PiXL.Raw receiving rows, ETL processing

## Output

Produce a checklist:

```
## Phase [N]: [Name] — Verification

- [x] Deliverable 1: description — VERIFIED (evidence)
- [ ] Deliverable 2: description — MISSING (what's needed)
- [x] Deliverable 3: description — VERIFIED (evidence)

### Verdict: PASS / FAIL

### Blockers (if FAIL):
1. What's missing
2. What needs to happen
```

Only mark PASS when ALL deliverables are verified. No partial credit.
