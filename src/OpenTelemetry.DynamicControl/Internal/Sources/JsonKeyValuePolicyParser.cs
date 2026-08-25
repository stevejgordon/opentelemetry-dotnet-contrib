// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using OpenTelemetry.DynamicControl.Internal.Policies;

namespace OpenTelemetry.DynamicControl.Internal.Sources;

/// <summary>
/// Decodes a complete policy payload in which each entry is a key naming a policy type and
/// a value describing that policy.
/// </summary>
/// <remarks>
/// <para>
/// The payload root is either an object, whose properties are the entries, or an array,
/// whose elements are each an object carrying exactly one entry. Keys this package does not
/// recognize are ignored, so a payload shared with other SDKs is usable as it stands.
/// </para>
/// <para>
/// Decoding is deterministic: the same payload always yields the same policies, in the order
/// the payload declared them, and nothing outside the payload participates.
/// </para>
/// <para>
/// The payload is taken as UTF-8 because that is the form every transport delivers it in: a
/// policy file is read as bytes, an HTTP body arrives as bytes, and an OpAMP configuration
/// body is a protobuf byte string. Accepting text instead would oblige each source to decode
/// bytes that are then transcoded straight back to UTF-8 to be read.
/// </para>
/// </remarks>
internal static class JsonKeyValuePolicyParser
{
    // The set of policy types this package can decode. A reader is stateless and shared, so
    // the table is built once. It is ordered only for determinism of key matching; no reader
    // takes precedence over another, because each claims a distinct payload key.
    private static readonly PolicyReader[] Readers =
    [
        TraceSamplingRatePolicyReader.Instance,
        LogLevelPolicyReader.Instance,
    ];

    // The depth cap is stated rather than inherited so that raising it is a deliberate act.
    // It is set at the System.Text.Json default: the cap applies to the whole payload, so a
    // tighter one would let an unrecognized branch nested by another SDK discard the entries
    // this package does read. Bounding payload size, not depth, is what limits the work an
    // untrusted sender can demand, and that belongs to the transport that receives it.
    private static readonly JsonDocumentOptions PayloadOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64,
    };

    // The encoded form of U+FEFF. A byte order mark is meaningless in UTF-8 and is not a JSON
    // token, so System.Text.Json rejects a payload that begins with one. Editors and .NET's
    // own text writers emit it regardless, which a file source would otherwise surface as a
    // malformed payload.
    private static ReadOnlySpan<byte> Utf8ByteOrderMark => [0xEF, 0xBB, 0xBF];

    /// <summary>
    /// Decodes one complete policy payload.
    /// </summary>
    /// <param name="utf8Payload">
    /// The payload, encoded as UTF-8. A leading byte order mark is permitted. A payload that
    /// is not well-formed UTF-8 is malformed as a whole.
    /// </param>
    /// <returns>
    /// A result that is either malformed, meaning the payload could not be decoded and says
    /// nothing about the policies its source intends, or decoded, carrying the complete set
    /// of policies the payload declared. That set may be empty.
    /// </returns>
    /// <remarks>
    /// <para>
    /// No payload content causes this method to throw. Content that cannot be decoded is
    /// reported through the returned result.
    /// </para>
    /// <para>
    /// The payload is read in place rather than copied, so the caller must not modify it for
    /// the duration of the call.
    /// </para>
    /// </remarks>
    public static PolicyPayloadParseResult Parse(ReadOnlyMemory<byte> utf8Payload)
    {
        var payload = TrimByteOrderMark(utf8Payload);

        // JSON text is UTF-8 by definition (RFC 8259, section 8.1), so a payload that is not
        // well-formed UTF-8 is not a policy document and nothing about its author's intent
        // can be recovered from it. System.Text.Json does not establish this: it validates
        // structure, and surfaces an invalid sequence only when the text encoding it is
        // read. Left to that, a corrupted payload would cost only the entries that happen to
        // carry the bad bytes and would still decode, which a caller applies as a complete
        // replacement -- retracting policies the sender never withdrew. Checking up front is
        // what keeps corruption in the malformed outcome, where it changes nothing.
        if (!Utf8PayloadValidator.IsValid(payload.Span))
        {
            return PolicyPayloadParseResult.Malformed("The payload is not well-formed UTF-8.");
        }

        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(payload, PayloadOptions);
        }
        catch (JsonException ex)
        {
            return PolicyPayloadParseResult.Malformed("The payload could not be decoded as JSON. " + ex.Message);
        }

        // Every policy is built before the document is disposed, so nothing that outlives
        // this scope holds a JsonElement into the document's pooled buffers.
        using (document)
        {
            var root = document.RootElement;

            return root.ValueKind switch
            {
                JsonValueKind.Array => BuildResult(CollectArrayEntries(root)),
                JsonValueKind.Object => BuildResult(CollectObjectEntries(root)),
                _ => PolicyPayloadParseResult.Malformed("The payload root must be a JSON object or array."),
            };
        }
    }

    private static ReadOnlyMemory<byte> TrimByteOrderMark(ReadOnlyMemory<byte> payload)
        => payload.Span.StartsWith(Utf8ByteOrderMark)
            ? payload.Slice(Utf8ByteOrderMark.Length)
            : payload;

    private static List<PayloadEntry> CollectObjectEntries(in JsonElement root)
    {
        List<PayloadEntry> entries = [];

        foreach (var property in root.EnumerateObject())
        {
            entries.Add(CreateEntry(property, default));
        }

        return entries;
    }

    private static List<PayloadEntry> CollectArrayEntries(in JsonElement root)
    {
        List<PayloadEntry> entries = [];
        var index = 0;

        foreach (var element in root.EnumerateArray())
        {
            var location = PayloadEntryLocation.ForIndex(index);

            entries.Add(
                element.ValueKind is JsonValueKind.Object && TryGetSingleProperty(element, out var property)
                    ? CreateEntry(property, location)
                    : new PayloadEntry(new PolicyPayloadRejection(
                        location,
                        PolicyRejectionReason.InvalidPayloadShape,
                        "The payload array element is not an object with exactly one property.")));

            index++;
        }

        return entries;
    }

    private static PayloadEntry CreateEntry(in JsonProperty property, PayloadEntryLocation fallbackLocation)
        => TryGetKey(property, out var key)
            ? new(key, property.Value)
            : new(new PolicyPayloadRejection(
                fallbackLocation,
                PolicyRejectionReason.InvalidPayloadShape,
                "The payload declares a key that cannot be read as text."));

    private static bool TryGetKey(in JsonProperty property, [NotNullWhen(true)] out string? key)
    {
        // NameEquals compares against the encoded name, which both avoids an allocation for
        // the common case and tolerates a name this package cannot read: a name that has no
        // string equivalent equals nothing, so an unreadable name is simply not recognized.
        foreach (var reader in Readers)
        {
            if (property.NameEquals(reader.PayloadKey))
            {
                key = reader.PayloadKey;
                return true;
            }
        }

        try
        {
            key = property.Name;
            return true;
        }
        catch (InvalidOperationException)
        {
            // The name has no string equivalent. The payload is known to be well-formed
            // UTF-8 by this point, so the cause is an escaped unpaired surrogate, which JSON
            // permits and which no UTF-16 string can represent.
            key = null;
            return false;
        }
    }

    private static bool TryGetSingleProperty(in JsonElement element, out JsonProperty property)
    {
        property = default;
        var found = false;

        foreach (var candidate in element.EnumerateObject())
        {
            if (found)
            {
                return false;
            }

            property = candidate;
            found = true;
        }

        return found;
    }

    private static PolicyPayloadParseResult BuildResult(List<PayloadEntry> entries)
    {
        var duplicateKeys = FindDuplicateKeys(entries);

        List<TelemetryPolicy> policies = [];
        List<PolicyPayloadRejection> rejections = [];
        List<string> ignoredKeys = [];
        HashSet<string> seenIgnoredKeys = new(StringComparer.Ordinal);

        foreach (var entry in entries)
        {
            if (entry.Rejection is { } rejection)
            {
                rejections.Add(rejection);
                continue;
            }

            var key = entry.Key!;

            if (!TryGetReader(key, out var reader))
            {
                if (seenIgnoredKeys.Add(key))
                {
                    ignoredKeys.Add(key);
                }

                continue;
            }

            if (duplicateKeys?.Contains(key) == true)
            {
                rejections.Add(new(
                    PayloadEntryLocation.ForKey(key),
                    PolicyRejectionReason.DuplicateKey,
                    "The payload declares the key more than once. Every occurrence is excluded."));
                continue;
            }

            var result = reader.Read(entry.Value);

            if (result.TryGetPolicy(out var policy))
            {
                policies.Add(policy);
            }
            else
            {
                rejections.Add(result.ToRejection(PayloadEntryLocation.ForKey(key)));
            }
        }

        return PolicyPayloadParseResult.Decoded(policies, rejections, ignoredKeys);
    }

    // A recognized key maps to exactly one policy type, and identity is derived from that
    // type, so a key repeated anywhere in the payload would otherwise yield two policies
    // occupying one PolicyKey. Rejecting every occurrence is what prevents that; resolving
    // to the last would make the committed set depend on the order the payload was written.
    private static HashSet<string>? FindDuplicateKeys(List<PayloadEntry> entries)
    {
        HashSet<string>? seen = null;
        HashSet<string>? duplicates = null;

        foreach (var entry in entries)
        {
            if (entry.Key is not { } key || !TryGetReader(key, out _))
            {
                continue;
            }

            seen ??= new(StringComparer.Ordinal);

            if (!seen.Add(key))
            {
                duplicates ??= new(StringComparer.Ordinal);
                duplicates.Add(key);
            }
        }

        return duplicates;
    }

    private static bool TryGetReader(string key, [NotNullWhen(true)] out PolicyReader? reader)
    {
        foreach (var candidate in Readers)
        {
            if (string.Equals(key, candidate.PayloadKey, StringComparison.Ordinal))
            {
                reader = candidate;
                return true;
            }
        }

        reader = null;
        return false;
    }

    private readonly struct PayloadEntry
    {
        public PayloadEntry(string key, JsonElement value)
        {
            this.Key = key;
            this.Value = value;
            this.Rejection = null;
        }

        public PayloadEntry(PolicyPayloadRejection rejection)
        {
            this.Key = null;
            this.Value = default;
            this.Rejection = rejection;
        }

        public string? Key { get; }

        public JsonElement Value { get; }

        public PolicyPayloadRejection? Rejection { get; }
    }
}
