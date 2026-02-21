---
description: 'Generate or update Atlas documentation for a subsystem across all 4 audience tiers.'
agent: doc-specialist
tools: ['read', 'edit', 'search']
---

# Document Subsystem

Generate structured Atlas documentation for a SmartPiXL subsystem.

## Process

1. Identify the target subsystem from user input.
2. Read all relevant source code for that subsystem.
3. Read relevant sections of the authoritative documents.
4. Create (or update) the Atlas doc in `docs/atlas/` following the 4-tier template.
5. Write **Private tier first** (raw detail), then **Technical**, then **Internal**, then **Public**.
6. Update `docs/atlas/_index.md` to reflect the new document's status.

## Tier Reminders

| Tier | Never Include |
|------|--------------|
| **Public** | Code, SQL, file paths, architecture internals, detection techniques |
| **Internal** | Code, specific SQL, internal file paths |
| **Technical** | Production credentials, owner-only decisions |
| **Private** | Nothing restricted — include everything |

## Output Location

`docs/atlas/{category}/{subsystem}.md` where category is one of:
- `architecture/` — system-level docs
- `subsystems/` — feature/component docs
- `database/` — schema and procedure docs
- `operations/` — deployment and monitoring docs
