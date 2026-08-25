// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry.DynamicControl.Internal.Policies;
using OpenTelemetry.DynamicControl.Internal.Sources;

namespace OpenTelemetry.DynamicControl.Tests;

public class PolicyPayloadParseResultTests
{
    [Fact]
    public void Malformed_SetsErrorAndLeavesCollectionsEmpty()
    {
        var result = PolicyPayloadParseResult.Malformed("Broken.");

        Assert.True(result.IsMalformed);
        Assert.Equal("Broken.", result.Error);
        Assert.Empty(result.Policies);
        Assert.Empty(result.Rejections);
        Assert.Empty(result.IgnoredKeys);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Malformed_WithBlankError_Throws(string? error)
    {
        Assert.ThrowsAny<ArgumentException>(() => PolicyPayloadParseResult.Malformed(error!));
    }

    [Fact]
    public void Decoded_CarriesSuppliedCollections()
    {
        Assert.True(TraceSamplingRatePolicy.TryCreate(new PolicyId("id"), "Name", 0.5, out var policy, out _));

        TelemetryPolicy[] policies = [policy];
        PolicyPayloadRejection[] rejections =
        [
            new(PayloadEntryLocation.ForKey("key"), PolicyRejectionReason.InvalidPolicyValue, "Nope."),
        ];
        string[] ignoredKeys = ["other"];

        var result = PolicyPayloadParseResult.Decoded(policies, rejections, ignoredKeys);

        Assert.False(result.IsMalformed);
        Assert.Null(result.Error);
        Assert.Same(policies, result.Policies);
        Assert.Same(rejections, result.Rejections);
        Assert.Same(ignoredKeys, result.IgnoredKeys);
    }

    [Fact]
    public void Decoded_WithNoEntries_IsNotMalformed()
    {
        var result = PolicyPayloadParseResult.Decoded([], [], []);

        Assert.False(result.IsMalformed);
        Assert.Null(result.Error);
        Assert.Empty(result.Policies);
    }

    [Fact]
    public void Decoded_WithNullPolicies_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PolicyPayloadParseResult.Decoded(null!, [], []));
    }

    [Fact]
    public void Decoded_WithNullRejections_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PolicyPayloadParseResult.Decoded([], null!, []));
    }

    [Fact]
    public void Decoded_WithNullIgnoredKeys_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => PolicyPayloadParseResult.Decoded([], [], null!));
    }
}
