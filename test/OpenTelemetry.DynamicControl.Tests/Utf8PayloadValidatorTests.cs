// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Text;
using OpenTelemetry.DynamicControl.Internal.Sources;

namespace OpenTelemetry.DynamicControl.Tests;

public class Utf8PayloadValidatorTests
{
    // Ill-formed sequences from the Unicode standard's table 3-7. Every case is rejected on
    // every target: modern targets delegate to the runtime, and the older ones use the scan
    // this package carries, so the two must agree on all of them.
    public static TheoryData<string, byte[]> IllFormedSequences =>
        new()
        {
            { "continuation byte with nothing to continue", [0x80] },
            { "highest lone continuation byte", [0xBF] },
            { "overlong two-byte lead", [0xC0, 0x80] },
            { "second overlong two-byte lead", [0xC1, 0xBF] },
            { "two-byte sequence missing its continuation", [0xC3] },
            { "two-byte sequence with a non-continuation", [0xC3, 0x28] },
            { "overlong three-byte encoding of U+007F", [0xE0, 0x81, 0xBF] },
            { "overlong three-byte encoding of U+0800", [0xE0, 0x9F, 0xBF] },
            { "high surrogate U+D800", [0xED, 0xA0, 0x80] },
            { "low surrogate U+DFFF", [0xED, 0xBF, 0xBF] },
            { "three-byte sequence truncated", [0xE2, 0x82] },
            { "three-byte sequence with a non-continuation", [0xE2, 0x28, 0xA1] },
            { "overlong four-byte encoding of U+FFFF", [0xF0, 0x8F, 0xBF, 0xBF] },
            { "scalar above U+10FFFF", [0xF4, 0x90, 0x80, 0x80] },
            { "four-byte lead no encoding defines", [0xF5, 0x80, 0x80, 0x80] },
            { "highest undefined lead byte", [0xFF] },
            { "four-byte sequence truncated", [0xF0, 0x9F, 0x98] },
            { "four-byte sequence with a non-continuation", [0xF0, 0x9F, 0x28, 0x80] },
        };

    public static TheoryData<string, byte[]> WellFormedSequences =>
        new()
        {
            { "empty", [] },
            { "ascii", [0x7B, 0x7D] },
            { "null byte", [0x00] },
            { "highest single byte", [0x7F] },
            { "lowest two-byte sequence, U+0080", [0xC2, 0x80] },
            { "two-byte sequence, U+00E9", [0xC3, 0xA9] },
            { "highest two-byte sequence, U+07FF", [0xDF, 0xBF] },
            { "lowest three-byte sequence, U+0800", [0xE0, 0xA0, 0x80] },
            { "just below the surrogate range, U+D7FF", [0xED, 0x9F, 0xBF] },
            { "just above the surrogate range, U+E000", [0xEE, 0x80, 0x80] },
            { "byte order mark, U+FEFF", [0xEF, 0xBB, 0xBF] },
            { "highest three-byte sequence, U+FFFF", [0xEF, 0xBF, 0xBF] },
            { "lowest four-byte sequence, U+10000", [0xF0, 0x90, 0x80, 0x80] },
            { "four-byte sequence, U+1F600", [0xF0, 0x9F, 0x98, 0x80] },
            { "highest scalar, U+10FFFF", [0xF4, 0x8F, 0xBF, 0xBF] },
        };

    [Theory]
    [MemberData(nameof(IllFormedSequences))]
    public void IsValid_WithIllFormedSequence_ReturnsFalse(string description, byte[] payload)
    {
        Assert.False(Utf8PayloadValidator.IsValid(payload), description);
    }

    [Theory]
    [MemberData(nameof(WellFormedSequences))]
    public void IsValid_WithWellFormedSequence_ReturnsTrue(string description, byte[] payload)
    {
        Assert.True(Utf8PayloadValidator.IsValid(payload), description);
    }

    [Theory]
    [MemberData(nameof(IllFormedSequences))]
    public void IsValid_WithIllFormedSequenceAmongValidText_ReturnsFalse(string description, byte[] sequence)
    {
        var prefix = Encoding.UTF8.GetBytes("{\"sampling_rate\": \"");
        var suffix = Encoding.UTF8.GetBytes("\"}");

        byte[] payload = [.. prefix, .. sequence, .. suffix];

        Assert.False(Utf8PayloadValidator.IsValid(payload), description);
    }

    [Fact]
    public void IsValid_AgreesWithTheFrameworkEncoderOverEveryLeadByte()
    {
        // A strict decoder is the independent authority. Comparing against it across the
        // whole lead-byte range, with the first continuation byte swept through the values
        // the bounds turn on, catches a bound stated one value out -- which a hand-picked
        // table of cases can miss.
        var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
        byte[] continuations = [0x7F, 0x80, 0x8F, 0x90, 0x9F, 0xA0, 0xBF, 0xC0];

        for (var lead = 0; lead <= 0xFF; lead++)
        {
            foreach (var continuation in continuations)
            {
                var payload = CreateSequence((byte)lead, continuation);

                bool expected;

                try
                {
                    strict.GetCharCount(payload);
                    expected = true;
                }
                catch (DecoderFallbackException)
                {
                    expected = false;
                }

                Assert.Equal(expected, Utf8PayloadValidator.IsValid(payload));
            }
        }
    }

    // Builds a sequence of the length the lead byte claims, so that well-formed combinations
    // actually occur rather than every case failing on a truncated tail.
    private static byte[] CreateSequence(byte lead, byte firstContinuation)
    {
        var length = lead switch
        {
            < 0x80 => 1,
            < 0xE0 => 2,
            < 0xF0 => 3,
            _ => 4,
        };

        var payload = new byte[length];
        payload[0] = lead;

        for (var index = 1; index < length; index++)
        {
            payload[index] = 0x80;
        }

        if (length > 1)
        {
            payload[1] = firstContinuation;
        }

        return payload;
    }
}
