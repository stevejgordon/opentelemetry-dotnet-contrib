// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

#if NET8_0_OR_GREATER
using System.Text.Unicode;
#endif

namespace OpenTelemetry.DynamicControl.Internal.Sources;

/// <summary>
/// Determines whether a sequence of bytes is well-formed UTF-8.
/// </summary>
/// <remarks>
/// <para>
/// JSON text is UTF-8 by definition (RFC 8259, section 8.1), so a payload that is not
/// well-formed UTF-8 is not a JSON document at all. <see cref="System.Text.Json"/> does not
/// establish that: it validates structure, and discovers an invalid sequence only when the
/// text encoding it is read. A payload is therefore checked here, before it is decoded.
/// </para>
/// <para>
/// Validation is a single pass over the bytes and allocates nothing. It runs once per
/// payload, on the cold path that receives one, so the pass costs nothing that matters.
/// </para>
/// </remarks>
internal static class Utf8PayloadValidator
{
    /// <summary>
    /// Determines whether the given bytes are well-formed UTF-8.
    /// </summary>
    /// <param name="payload">The bytes to check. An empty sequence is well-formed.</param>
    /// <returns>
    /// <see langword="true"/> if every byte participates in a well-formed UTF-8 sequence;
    /// otherwise <see langword="false"/>.
    /// </returns>
    /// <remarks>
    /// Overlong encodings, the UTF-16 surrogate range, scalar values above U+10FFFF, and
    /// truncated sequences are all ill-formed, matching the Unicode standard's table 3-7.
    /// </remarks>
    public static bool IsValid(ReadOnlySpan<byte> payload)
    {
#if NET8_0_OR_GREATER
        return Utf8.IsValid(payload);
#else
        var index = 0;

        while (index < payload.Length)
        {
            var first = payload[index];

            if (first < 0x80)
            {
                index++;
                continue;
            }

            // The bounds on the first continuation byte are what exclude the sequences that
            // are structurally well-formed but encode nothing: an overlong form of a scalar
            // that fits in fewer bytes, a UTF-16 surrogate, or a value above U+10FFFF. The
            // remaining continuation bytes are unconstrained beyond being continuations.
            int continuationCount;
            byte firstContinuationMinimum = 0x80;
            byte firstContinuationMaximum = 0xBF;

            if (first is >= 0xC2 and <= 0xDF)
            {
                continuationCount = 1;
            }
            else if (first is >= 0xE0 and <= 0xEF)
            {
                continuationCount = 2;

                if (first == 0xE0)
                {
                    firstContinuationMinimum = 0xA0;
                }
                else if (first == 0xED)
                {
                    firstContinuationMaximum = 0x9F;
                }
            }
            else if (first is >= 0xF0 and <= 0xF4)
            {
                continuationCount = 3;

                if (first == 0xF0)
                {
                    firstContinuationMinimum = 0x90;
                }
                else if (first == 0xF4)
                {
                    firstContinuationMaximum = 0x8F;
                }
            }
            else
            {
                // A continuation byte with nothing to continue, an overlong two-byte lead
                // (0xC0 or 0xC1), or a lead byte no encoding defines (0xF5 through 0xFF).
                return false;
            }

            if (index + continuationCount >= payload.Length)
            {
                return false;
            }

            var firstContinuation = payload[index + 1];

            if (firstContinuation < firstContinuationMinimum || firstContinuation > firstContinuationMaximum)
            {
                return false;
            }

            for (var offset = 2; offset <= continuationCount; offset++)
            {
                if (payload[index + offset] is < 0x80 or > 0xBF)
                {
                    return false;
                }
            }

            index += continuationCount + 1;
        }

        return true;
#endif
    }
}
