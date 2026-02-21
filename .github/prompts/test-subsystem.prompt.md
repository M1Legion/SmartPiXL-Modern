---
description: 'Test a specific subsystem thoroughly — coverage analysis, write missing tests, run and verify.'
agent: testing-specialist
tools: ['read', 'edit', 'execute', 'search']
---

# Test Subsystem

Thoroughly test a specific SmartPiXL subsystem.

## Process

1. Identify the target subsystem from user input (or ask).
2. Read all source files for that subsystem.
3. Check existing test coverage — does a test file exist? How many tests? What do they cover?
4. Using the test matrix from the testing-specialist agent, identify gaps:
   - Missing happy path tests
   - Missing error/exception handling tests
   - Missing edge case tests (null, empty, boundary values, adversarial input)
   - Missing concurrency tests (if applicable)
5. Write the missing tests following project conventions:
   - Name: `{Method}_should_{expected}_when_{condition}`
   - Framework: xUnit + FluentAssertions
   - Pattern: AAA (Arrange, Act, Assert)
6. Run tests: `dotnet test SmartPiXL.Tests/`
7. Fix any failures introduced.
8. Report coverage summary.

## Subsystem Shortcuts

| Input | Subsystem | Key Files |
|-------|-----------|-----------|
| `edge` | Full Edge request pipeline | All services in `SmartPiXL/Services/` |
| `forge` | Forge pipeline services | `SmartPiXL.Forge/Services/` |
| `enrichments` | Tier 1-3 enrichment services | `SmartPiXL.Forge/Services/Enrichments/` |
| `etl` | ETL stored procedures | SQL procs + `EtlBackgroundService` |
| `script` | PiXL Script | `PiXLScript.cs` |
| `shared` | Shared library | `SmartPiXL.Shared/` |
| `ip` | IP classification + behavior | `IpClassificationService`, `IpBehaviorService`, `DatacenterIpService` |
| `pipe` | Named pipe IPC | `PipeClientService`, `PipeListenerService` |
| `failover` | JSONL failover | `JsonlFailoverService`, `FailoverCatchupService` |
| `all` | Everything — full coverage report | All projects |
