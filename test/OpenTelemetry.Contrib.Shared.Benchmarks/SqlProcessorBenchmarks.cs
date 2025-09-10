// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using BenchmarkDotNet.Attributes;

namespace OpenTelemetry.Instrumentation.Benchmarks;

[MemoryDiagnoser]
public class SqlProcessorBenchmarks
{
    [Params(
        "SELECT * FROM Orders o, OrderDetails od",
        "INSERT INTO Orders(Id, Name, Bin, Rate) VALUES(1, 'abc''def', 0xFF, 1.23e-5)",
        "UPDATE Orders SET Name = 'foo' WHERE Id = 42",
        "DELETE FROM Orders WHERE Id = 42",
        "CREATE UNIQUE CLUSTERED INDEX IX_Orders_Id ON Orders(Id)",
        "SELECT DISTINCT o.Id FROM Orders o JOIN Customers c ON o.CustomerId = c.Id",
        "SELECT column -- end of line comment\nFROM /* block \n comment */ table",
        "SELECT Col1, Col2, Col3, Col4, Col5 FROM VeryLongTableName_Sales2024_Q4, Another_Very_Long_Table_Name_Inventory")]
    public string Sql { get; set; } = string.Empty;

    [Params(false)]
    public bool CacheEnabled { get; set; }

    public void Setup() => SqlProcessor.CacheCapacity = this.CacheEnabled ? 1000 : 0;

    [Benchmark]
    public void Simple() => SqlProcessor.GetSanitizedSql(this.Sql);
}
