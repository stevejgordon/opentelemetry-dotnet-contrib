// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;

namespace OpenTelemetry.Instrumentation.Benchmarks;

[MemoryDiagnoser]
public class SqlProcessorBenchmarks
{
    [Params("SELECT * FROM Orders o, OrderDetails od")]
    public string Sql { get; set; } = string.Empty;

    [Benchmark]
    public void Simple() => SqlProcessor.GetSanitizedSql(this.Sql);
}
