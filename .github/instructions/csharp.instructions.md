---
description: 'C# coding standards for the SmartPiXL tracking platform (.NET 10, high-throughput, zero-allocation hot paths)'
applyTo: '**/*.cs'
---

# C# Conventions — SmartPiXL

## Runtime & Language

- **.NET 10.0**, C# 14, nullable reference types enabled
- Minimal APIs (no controllers) — endpoints registered as lambdas in `*Endpoints.cs` files
- `sealed` on every class not designed for inheritance
- `sealed record` for reference-type DTOs (e.g., `TrackingData`)
- `readonly record struct` for stack-allocated value types (e.g., `GeoResult`, `IpClassification`)

## Philosophy: Stack Over Heap, Always

The lead developer is a former C++ game dev who thinks at the hardware level. This shapes every decision:

- **Prefer stack allocation over heap** — `ref struct`, `readonly record struct`, `stackalloc`, `Span<T>` are first-choice tools, not last resorts
- **Bit manipulation is welcome** — bit shifts, flags, masks, and bitwise tricks are preferred over branching when they're clearer or faster
- **Integer math over floating point** — when precision allows, use integer arithmetic. Fixed-point patterns, multiplication-by-reciprocal, and shift-based division are encouraged
- **Value types by default** — reach for `struct` and `readonly record struct` before `class`. Only use reference types when you need inheritance, nullability, or shared identity
- **Minimize indirection** — flat data, contiguous memory, cache-friendly layouts. Avoid deep object graphs and pointer-chasing patterns
- **Low-level is good** — `unsafe` and `fixed` are acceptable when the performance justifies it and the scope is contained. `Unsafe.As<T>`, `MemoryMarshal`, and `BitConverter` are tools, not smells
- **Know what the JIT does** — prefer patterns the JIT can optimize: small methods for inlining, `static` local functions to avoid closures, `[MethodImpl(MethodImplOptions.AggressiveInlining)]` when measured

## Hot Path Rules (request pipeline)

The pixel endpoint (`_SMART.GIF`) must return in <10ms. These rules apply to everything in the request path:

- **Zero heap allocation** — no LINQ, no string interpolation, no closures
- `ThreadStatic` StringBuilder reuse (see `TrackingCaptureService.cs`)
- `[GeneratedRegex]` source-generated regex — never `new Regex()` at runtime
- `SearchValues<char>` (SIMD) for character scanning (see header JSON escaping)
- `Span<T>` / `ReadOnlySpan<T>` over arrays and substrings
- `stackalloc` for small fixed buffers (see CIDR matching in `DatacenterIpService`)
- Lock-free patterns: `Volatile.Read`/`Interlocked.CompareExchange` for shared state
- Bit shifts over multiplication/division by powers of 2 where intent is clear
- `ref` returns and `ref readonly` parameters to avoid copying value types

## Background Services

- Inherit `BackgroundService` for long-running workers
- Use `Channel<T>` (bounded, `BoundedChannelFullMode.DropOldest`) for producer-consumer queues
- Custom `DbDataReader` for SqlBulkCopy — never intermediate `DataTable`/`DataRow`
- `SqlBulkCopy` with explicit column ordinal mappings for bulk writes

## Logging

- Use `ITrackingLogger` (our lightweight interface), **NOT** `Microsoft.Extensions.Logging.ILogger`
- Log levels: `Trace`, `Debug`, `Info`, `Warning`, `Error`
- Implementation: `FileTrackingLogger` — Channel-backed async file writer, daily rolling

## Naming Conventions

| Element | Convention | Example |
|---------|-----------|---------|
| Services | `{Name}Service.cs` | `GeoCacheService.cs` |
| Background workers | `{Name}Service.cs` (inherits BackgroundService) | `EtlBackgroundService.cs` |
| Enrichment services (Forge) | `Services/Enrichments/{Name}Service.cs` | `BotUaDetectionService.cs` |
| Endpoints | `{Domain}Endpoints.cs` | `DashboardEndpoints.cs` |
| Models | `{Name}.cs` | `TrackingData.cs` |
| SQL scripts | `{NN}_{Description}.sql` (numbered migrations) | `41_ScreenExtendedMousePath.sql` |
| Config | `TrackingSettings.cs` / `ForgeSettings.cs` with `IOptions<T>` | — |

## Project Boundaries

| Project | Purpose | Rules |
|---------|---------|-------|
| `SmartPiXL` | IIS Edge — pixel capture hot path | Zero-alloc request pipeline, named pipe client, GIF response <10ms |
| `SmartPiXL.Forge` | Windows Service — enrichment + ETL | Named pipe server, Tier 1-3 enrichments, SqlBulkCopy, Channel<T> pipeline |
| `SmartPiXL.Shared` | Shared library — models, config, interfaces | Zero NuGet dependencies, referenced by all projects |
| `SmartPiXL.Worker-Deprecated` | **DEPRECATED** — read-only reference | DO NOT MODIFY. Port functionality to the Forge. |
| `SmartPiXL.SqlClr` | CLR assembly for SQL Server 2025 | Minimal dependencies, target net10.0 (fallback net9.0/net8.0) |

## Patterns to Follow

- Fire-and-forget pixel response: enqueue to Channel, return GIF immediately
- Edge → Forge: named pipe (`NamedPipeClientStream` / `NamedPipeServerStream`)
- JSONL failover: when pipe unavailable, write JSON lines to disk for catch-up
- Server-side enrichment: append `_srv_*` query params before DB write
- Configuration: `IOptions<TrackingSettings>` bound from `appsettings.json` `Tracking` section
- IP classification: static `IpClassificationService.Classify()` — pure function, no instance
- Enrichment pipeline: `Channel<T>` → sequential enrichment per record → `Channel<T>` → SQL writer

## Anti-Patterns

- Never `Task.Run()` for fire-and-forget — use Channel<T> queue
- Never `DataTable` for bulk inserts — use custom DbDataReader
- Never `string.Split()` in hot paths — use Span-based parsing
- Never `Microsoft.Extensions.Logging` — use `ITrackingLogger`
- Never `new Regex()` — use `[GeneratedRegex]`
