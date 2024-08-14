// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

namespace Examples.Wcf.Server.AspNetFramework;

public class Service : IService
{
    public string GetCustomer(string value) => string.Format("You entered: {0}", int.Parse(value));

    public string EchoWithPost(string s) => "You said " + s;
}
