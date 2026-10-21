# Lexi search benchmark

- Date: 2026-10-20 21:51
- Machine: AMD Ryzen 9 7940H w/ Radeon 780M Graphics, 16 logical cores, 31 GB RAM
- OS: Microsoft Windows 10.0.26300 (X64); runtime .NET 10.0.13
- Database: 495 MB, 975,975 headwords, 1,322,126 entries, 1,092,295 forms (built 2026-10-20 19:26 UTC)
- Fuzzy index: 952,321 keys, 926,723 states, ~11.6 MB
- Method: `lexi bench --rounds 5 --sample 300`; each query runs 5× after warm-up, single thread, Release build, wall-clock via Stopwatch.

## Cold start

| Step | ms |
|---|---:|
| Open database (read-only) | 25.5 |
| First query (no fuzzy) | 10.7 |
| Load fuzzy index | 20.4 |
| First fuzzy query | 9.9 |

## Warm query latency (ms)

| Category | Queries | p50 | p95 | p99 | max |
|---|---:|---:|---:|---:|---:|
| acceptance | 15 | 1.93 | 6.95 | 7.98 | 7.98 |
| exact | 300 | 1.45 | 2.87 | 3.66 | 5.27 |
| typo | 274 | 2.53 | 3.93 | 4.80 | 6.29 |
| prefix | 245 | 1.50 | 2.73 | 3.44 | 4.37 |
| **all** | | 1.71 | 3.49 | 4.58 | 7.98 |

Details (aggregate + Markdown, 100 words): p50 0.55 ms, p95 1.22 ms, max 54.88 ms
Process memory after benchmark: working set 147 MB, private 68 MB, GC heap 27 MB
