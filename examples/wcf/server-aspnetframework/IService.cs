// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.ServiceModel;
using System.ServiceModel.Web;

namespace Examples.Wcf.Server.AspNetFramework;

[ServiceContract]
public interface IService
{
    [OperationContract]
    [WebGet(
        UriTemplate = "/Customer/{value}/Info",
        ResponseFormat = WebMessageFormat.Json,
        BodyStyle = WebMessageBodyStyle.Wrapped
    )]
    string GetCustomer(string value);

    [OperationContract]
    [WebInvoke(
        UriTemplate = "/Customer/Echo")]
    string EchoWithPost(string s);
}
