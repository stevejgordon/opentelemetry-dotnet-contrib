// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using OpenTelemetry.Instrumentation;

if (Debugger.IsAttached || (args.Length > 0 && args[0] == "execute"))
{
    SqlProcessor.GetSanitizedSql("SELECT * FROM Orders o, OrderDetails od");

    SqlProcessor.GetSanitizedSql("SELECT * FROM Orders o, OrderDetails od");

    SqlProcessor.GetSanitizedSql("SELECT * FROM Orders o, OrderDetails od");
}
else
{
    var config = ManualConfig.Create(DefaultConfig.Instance)
        .WithArtifactsPath(@"..\..\..\BenchmarkResults");

    BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, config);
}
