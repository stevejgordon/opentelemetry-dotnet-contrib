// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using OpenTelemetry.DynamicControl.Internal.Sources;

namespace OpenTelemetry.DynamicControl.Tests;

public class PolicyPayloadRejectionTests
{
    [Fact]
    public void Constructor_CarriesSuppliedValues()
    {
        var location = PayloadEntryLocation.ForKey("sampling_rate");

        var rejection = new PolicyPayloadRejection(
            location,
            PolicyRejectionReason.InvalidPolicyValue,
            "Out of range.");

        Assert.Equal(location, rejection.Location);
        Assert.Equal(PolicyRejectionReason.InvalidPolicyValue, rejection.Reason);
        Assert.Equal("Out of range.", rejection.Message);
    }

    [Fact]
    public void Constructor_AllowsAPositionalLocation()
    {
        var rejection = new PolicyPayloadRejection(
            PayloadEntryLocation.ForIndex(2),
            PolicyRejectionReason.InvalidPayloadShape,
            "The element has no key.");

        Assert.True(rejection.Location.TryGetIndex(out var index));
        Assert.Equal(2, index);
    }

    [Fact]
    public void Constructor_AllowsAnUnknownLocation()
    {
        var rejection = new PolicyPayloadRejection(
            default,
            PolicyRejectionReason.InvalidPayloadShape,
            "The key cannot be read.");

        Assert.False(rejection.Location.TryGetKey(out _));
        Assert.False(rejection.Location.TryGetIndex(out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_WithBlankMessage_Throws(string? message)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => _ = new PolicyPayloadRejection(
                PayloadEntryLocation.ForKey("key"),
                PolicyRejectionReason.DuplicateKey,
                message!));
    }
}
