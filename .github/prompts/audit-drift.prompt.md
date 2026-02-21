---
description: 'Run adversarial audit against docs. Quick drift check for a subsystem or full codebase.'
agent: adversarial-reviewer
tools: ['read', 'search', 'execute', 'ms-mssql.mssql/*']
---

# Audit Drift

Run an adversarial review comparing what the code and database actually do against what the authoritative documents say they should do.

## Process

1. Read ALL authoritative documents:
   - `docs/BRILLIANT-PIXL-DESIGN.md`
   - `docs/SmartPiXL Authoritative WorkPlan .md`
   - `docs/IMPLEMENTATION-LOG.md`
   - `.github/copilot-instructions.md`
   - `.github/instructions/csharp.instructions.md`
   - `.github/instructions/sql.instructions.md`

2. If user specified a subsystem, focus there. Otherwise, full audit.

3. For each doc claim, verify against code:
   - Architecture claims → actual project structure
   - Field counts → actual `data.*` assignments in PiXLScript.cs
   - Service inventories → actual DI registrations in Program.cs
   - Schema maps → actual database objects
   - Config values → actual appsettings.json files

4. Run `dotnet build` and `dotnet test` to verify build/test health.

5. Produce the structured report per the adversarial-reviewer agent format.
