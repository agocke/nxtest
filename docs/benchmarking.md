# Benchmarking

NXTest provides basic in-process benchmarking through the `[Bench]` attribute.
Benchmarks are discovered by the source generator but run separately from tests.

## Defining a Benchmark

Mark a method with `[Bench]`:

```csharp
public class ParserBenchmarks
{
    private readonly byte[] _payload = LoadPayload();

    [Bench]
    public void ParsePayload()
    {
        Benchmark.Consume(Parser.Parse(_payload));
    }
}
```

Benchmark methods must return `void` or non-generic `Task`. `ValueTask`, `Task<T>`,
and other return types produce a compile-time diagnostic. An exception fails the
benchmark.

Parameterized benchmarks reuse theory-style `[InlineData]`:

```csharp
[Bench]
[InlineData(16)]
[InlineData(256)]
public void ParsePayload(int payloadSize)
{
    Benchmark.Consume(Parser.Parse(_payload.AsSpan(0, payloadSize)));
}
```

Each data row is a separate benchmark result, such as
`ParsePayload(payloadSize: 16)`. It receives its own calibration, warmup,
measurements, and class instance. A benchmark with parameters must provide at least
one `[InlineData]` row, and every row must match the method's parameter count;
violations produce compile-time diagnostics.

## Running Benchmarks

Normal test runs exclude benchmarks:

```bash
dotnet test
```

Use `dotnet test` in Release mode for the human benchmark summary:

```bash
dotnet test perf/bench/bench.csproj -c Release -- --bench
```

Replace `perf/bench/bench.csproj` with the path to your benchmark project.
`--bench` runs benchmarks exclusively; facts and theories are not run. The run itself
stays quiet — the platform's normal per-test output is used — and NXTest prints a
single summary table once every benchmark has completed (see
[Measurement and Results](#measurement-and-results)).

Run the benchmark application directly when collecting machine-readable results:

```bash
dotnet run --project perf/bench/bench.csproj -c Release -- --bench > old.txt
```

Programmatic callers can select the same mode with
`TestExecutionOptions.RunBenchmarks = true`.

## Instance Lifecycle

Each instance benchmark case gets its own class instance. NXTest:

1. Constructs the instance once.
2. Reuses it for every preparation, calibration, warmup, and measured invocation.
3. Disposes it once after the benchmark completes when it implements `IDisposable`.

Construction and disposal are outside the measurement window. Constructors can
therefore perform per-benchmark setup without adding allocation time to every
sample.

State mutations persist between invocations. Benchmarks that require fresh state
must reset that state themselves; per-iteration setup is not currently supported.
Static benchmark methods do not create a class instance.

## Optimization Resistance

The JIT can remove pure computations when their results are unused. Pass benchmark
outputs to `Benchmark.Consume` to make them observable without boxing:

```csharp
[Bench]
public void ParsePayload()
{
    Benchmark.Consume(Parser.Parse(_payload));
}
```

`Benchmark.Consume` is a non-inlined generic call, following the same strategy as
BenchmarkDotNet's dead-code-elimination helper. The call is part of the measured
operation. Methods that already produce observable side effects do not need it.

## Measurement and Results

### Why batching is required: clock resolution

Timing a single fast operation directly is not possible, because the monotonic clock
is far coarser than the operations being measured. `System.Diagnostics.Stopwatch`
does not read the CPU's `rdtsc` counter; it reads the operating system's monotonic
timer — `mach_absolute_time` (backed by the ARM generic timer `CNTVCT_EL0`) on Apple
silicon, `QueryPerformanceCounter` on Windows, `clock_gettime(CLOCK_MONOTONIC)` on
Linux. Although `Stopwatch.Frequency` is often reported as 1 GHz — implying a
one-nanosecond tick — that figure is a scaling artifact, not the true granularity.
On Apple silicon the underlying counter runs at 24 MHz, so the smallest observable
increment is about 42 ns; a typical Windows `QueryPerformanceCounter` resolves to
about 100 ns. Reading the clock also has non-trivial latency, and even a raw `rdtsc`
would not help: its read cost and required fencing exceed the cost of a few-nanosecond
operation, and the counter's frequency is unrelated to instruction throughput.

The runner therefore measures a **batch** of many operations per sample and divides by
the operation count. A batch spanning roughly one hundred milliseconds amortizes both the coarse
clock quantum and the per-read overhead to a negligible fraction, which is why the
duration targets below are expressed in milliseconds even though individual operations
may take only a few nanoseconds.

### Pilot, warmup, and measurement

The runner explicitly prepares the generated dispatch delegate and executes one untimed
preparation operation so cold JIT compilation cannot make the first calibration batch
appear long enough and incorrectly select one operation per sample. Calibration starts
with one operation per iteration. Very short batches increase geometrically; once a
batch is long enough for the clock quantum to be
negligible, the runner projects the operation count needed for an iteration of at
least 20 milliseconds. The count is bounded by an internal safety limit. This reaches
a useful signal with fewer state-mutating pilot invocations than simple doubling. If
the limit is reached first, the result reports a calibration warning.

After calibration, the runner warms up with an *adaptive* stability condition rather
than a fixed duration: it runs between six and fifty batches and requires the most
recent window of per-operation timings to be both narrow and to show at least four
timing-direction changes (or be completely flat). This catches level shifts and
monotonic drift as a benchmark climbs the JIT's compilation tiers. Warmup is bounded
by an iteration and time cap.

Because calibration measured cold, un-tiered code, the runner recalibrates the warmed
per-operation timing to a 100 millisecond batch and executes two discarded final-size
settling batches. Each observed timing corrects the next operation count in case
Dynamic PGO accelerates the workload at the larger batch size. The runner then performs
a full garbage collection and records consecutive batches until it has both at least
ten samples and one second of measured work. Normally these are ten 100 millisecond
samples; if late optimization makes the batches shorter, measurement simply collects
more of them. It does not collect between samples, so workload-triggered collections
and sustained heap behavior remain part of the timed epoch.

If the epoch shows a material level shift, its samples are discarded, the benchmark
performs another adaptive warmup at the final batch size, and measurement retries once.
The second epoch is reported even if it remains unstable. No duration or sample-count
planning is performed from observed measurement data.

Each sample times one calibrated batch and is divided by its operation count. The
generated synchronous dispatch uses a named loop marked
`MethodImplOptions.AggressiveOptimization` around a direct method call, so clock and
delegate dispatch overhead occur once per sample rather than once per operation;
benchmark dispatch performs no string lookup. Cancellation checks and statistical
analysis occur outside timing. The fixed policy is internal and intentionally not
configurable. Benchmarks run sequentially in deterministic name order to reduce
interference and keep each case at a consistent position across repeated runs.

### Stability and descriptive statistics

In-process timing is prone to *non-stationary execution* — the timing can shift
partway through a run due to late tiering, cache effects, or scheduling. The runner
guards against reporting deceptively precise results over such a run by comparing the
median of the first half of the samples with the median of the second half. When
those regimes differ materially, the result is flagged as **unstable**. Comparing
medians of sample groups also blunts the effect of autocorrelation between adjacent
samples.

The reported statistics reflect a deliberate choice about *what a benchmark is
estimating*. Measurement noise is almost entirely one-sided — context switches,
interrupts, GC pauses, and thermal effects can only make a sample slower, never
faster than the true cost. That informs which summaries are useful:

- **Median (primary)** — a robust estimate of the *typical* per-operation cost,
  insensitive to the occasional slow sample. It includes the median amount of any
  genuine, representative variance (data-dependent branches, rehashing, allocation),
  which makes it a sound general-purpose headline.
- **Floor, reported as the 10th percentile (primary)** — an estimate of the
  *intrinsic* cost with the least measurement interference. Because noise is
  one-sided, the low end of the distribution is the least corrupted and the most
  reproducible number for comparing implementations or detecting regressions. A low
  percentile is used rather than the raw minimum because the minimum is an extreme
  order statistic biased by sample count, whereas the 10th percentile stays close to
  the floor while remaining stable across runs. (Note that each sample is already a
  mean over a batch of operations, so the floor is the best *sustained* per-operation
  throughput, not a single lucky reading.)
- **MAD** — robust dispersion around the median.
- **Minimum and maximum** — the observed range.

The **gap between the floor and the median is itself diagnostic**: when they are
close, the operation is deterministic and CPU-bound; when the floor is much lower than
the median, the operation has real or noise-induced variance and should be read with
that in mind.

The mean is intentionally *not* part of the headline: it is dominated by the
right-hand tail and is the worst estimator of intrinsic cost. It and the sample
standard deviation remain available as descriptive values because the mean is the only
additive summary — `mean × operations` gives total time.

A completed benchmark produces `BenchmarkResult.Completed` with:

- Total measured time
- Raw per-operation samples
- Median and median absolute deviation (primary)
- Floor / 10th-percentile per-operation time (primary)
- Minimum and maximum
- Mean and sample standard deviation (secondary; not shown on the console)
- Stability flag (whether distinct timing regimes were detected)
- Gen0/Gen1/Gen2 collection counts and bytes allocated across the measured epoch
- Measured iteration count, operations per iteration (after post-warmup
  recalibration), warmup iteration count, and calibration status

The console summary presents allocation and GC totals normalized per operation — bytes
allocated per operation and collections per 1,000 operations — matching the convention
used by BenchmarkDotNet.

### The summary table

When launched through `dotnet test`, NXTest keeps the run quiet and prints a single
aligned summary table once all benchmarks have completed:

```
Benchmark summary

| Benchmark                               |  Median | Floor (P10) |     MAD |  Alloc/op |             GC/1k op |
|-----------------------------------------|---------|-------------|---------|-----------|----------------------|
| BasicBenchmarks.ConsumeValue(value: 64) | 2.11 ns |     2.11 ns | 0.00 ns | 0.00 B/op | 0.0000/0.0000/0.0000 |
```

The `GC/1k op` column reports Gen0/Gen1/Gen2 collections per 1,000 operations. A
benchmark flagged as unstable is marked with `*`, and one whose calibration hit the
operation limit with `~`; these markers are explained in a `Notes` section below the
table. Failed and skipped benchmarks are listed after the table. All descriptive
statistics use every raw sample; no outliers are removed.

### Benchmark text output from `dotnet run`

Direct `dotnet run` benchmark execution writes a Go-style benchmark stream. Run
metadata is retained as `#` comments, followed by one aggregate line for the full
benchmark execution. The commented human summary appears at the bottom, where it
remains easy to find in terminal output:

```bash
dotnet run --project perf/bench/bench.csproj -c Release -- --bench > old.txt
```

```text
# nxbench: 1
# run: 96dd6a9d-b5c6-4131-a100-c61cc9368db8
# timestamp: 2026-07-22T21:00:00.0000000+00:00
# benchmark: BenchmarkProbeBenchmarks.Measure stable=true calibrated=true warmup=8
BenchmarkProbeBenchmarks.Measure 167772160 1.43 ns/op 0 B/op 0 gen0/1k-op 0 gen1/1k-op 0 gen2/1k-op
#
# Benchmark summary
# | Benchmark               | Median  | Floor (P10) | ... |
# | ProbeBenchmarks.Measure | 1.43 ns |     1.42 ns | ... |
```

Names containing whitespace or `%` use percent escaping. Direct benchmark mode bypasses
Microsoft Testing Platform, so NXTest writes only the configuration keys, `#` comments,
and one `Benchmark...` result per execution. Benchmark code can still write its own
output; analysis tools should ignore unrecognized lines. Redirecting another invocation
with `>>` naturally adds an independent result, matching Go's repeated-run model.

Failures and skipped benchmarks produce `BenchmarkResult.Failed` and
`BenchmarkResult.Skipped`, respectively. `dotnet test` continues to represent a
completed benchmark as a passed MTP node because the platform requires test-oriented
states; direct `dotnet run -- --bench` does not construct MTP.

## Current Scope

NXTest intentionally performs best-effort measurement in the test process. Benchmark
mode excludes regular tests, and calibration, adaptive warmup, recalibration, and
fixed measurement reduce several important sources of noise without requiring
process isolation.

- Timing still includes loop and method-call overhead, although pilot batching
  amortizes clock and generated-dispatch overhead.
- There is no overhead subtraction, inferential confidence interval, or regression
  comparison. A separate comparison tool can consume the raw sample lines without
  adding statistical policy to the test runtime.
- Instability is detected and reported, but the runner does not model change points
  or autocorrelation explicitly.
- Parameterless benchmark signatures are documented but not yet diagnosed by the
  source generator.

Use these results for coarse comparisons and tracking relatively substantial
operations. Very small operations can be dominated by framework and clock overhead.
