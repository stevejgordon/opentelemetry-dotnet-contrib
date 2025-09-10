// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;
using System.Collections.Concurrent;

namespace OpenTelemetry.Instrumentation;

internal static class SqlProcessor
{
    // Internal for benchmarking
    internal static int CacheCapacity;

    private static readonly ConcurrentDictionary<string, SqlStatementInfo> Cache = new();

#if NET
    private static readonly SearchValues<char> WhitespaceSearchValues = SearchValues.Create([' ', '\t', '\r', '\n']);
#endif

    // We can extend this in the future to include more keywords if needed.
    // The keywords should be ordered by frequency of use to optimize performance.
    // This only includes keywords that are standalone or which are the first keyword in a chain.
    private static readonly SqlKeywordInfo[] SqlKeywords =
    [
        SqlKeywordInfo.SelectKeyword,
        SqlKeywordInfo.InsertKeyword,
        SqlKeywordInfo.UpdateKeyword,
        SqlKeywordInfo.DeleteKeyword,
        SqlKeywordInfo.CreateKeyword,
        SqlKeywordInfo.AlterKeyword,
        SqlKeywordInfo.DropKeyword,
    ];

    private enum SqlKeyword
    {
        Unknown,
        Select,
        Insert,
        Update,
        Delete,
        From,
        Into,
        Join,
        Create,
        Alter,
        Drop,
        Table,
        Index,
        Procedure,
        View,
        Database,
        Trigger,
        Unique,
        NonClustered,
        Clustered,
        Distinct,
        On,
    }

    public static SqlStatementInfo GetSanitizedSql(string? sql)
    {
        if (sql == null)
        {
            return default;
        }

        if (Cache.TryGetValue(sql, out var sqlStatementInfo))
        {
            return sqlStatementInfo;
        }

        sqlStatementInfo = SanitizeSql(sql.AsSpan());

        // Capacity <= 0 disables caching
        if (CacheCapacity <= 0)
        {
            return sqlStatementInfo;
        }

        // Best-effort capacity guard; may slightly exceed under concurrency which is acceptable for this cache
        return Cache.Count >= CacheCapacity ? sqlStatementInfo : Cache.GetOrAdd(sql, sqlStatementInfo);
    }

    private static SqlStatementInfo SanitizeSql(ReadOnlySpan<char> sql)
    {
        // We use a single buffer for both sanitized SQL and DB query summary
        // DB query summary starts from the index of the length of the input SQL
        // We rent a buffer twice the size of the input SQL to ensure we have enough space
        var rentedBuffer = ArrayPool<char>.Shared.Rent(sql.Length * 2);

        var buffer = rentedBuffer.AsSpan();

        ParseState state = new(stackalloc SqlKeyword[4]);

        // Precompute the summary buffer slice once and carry it via state to avoid repeated Span.Slice calls
        state.SummaryBuffer = buffer.Slice(rentedBuffer.Length / 2);

        while (state.ParsePosition < sql.Length)
        {
            if (SkipComment(sql, ref state))
            {
                continue;
            }

            if (SanitizeStringLiteral(sql, ref state) ||
                SanitizeHexLiteral(sql, ref state) ||
                SanitizeNumericLiteral(sql, ref state))
            {
                buffer[state.SanitizedPosition++] = '?';
                continue;
            }

            if (ParseWhitespace(sql, buffer, ref state))
            {
                continue;
            }

            ParseToken(sql, buffer, ref state);
        }

        var sqlStatementInfo = new SqlStatementInfo(
            buffer.Slice(0, state.SanitizedPosition).ToString(),
            state.SummaryBuffer.Slice(0, state.SummaryPosition).ToString());

        // We don't clear the buffer as we know the content has been sanitized
        ArrayPool<char>.Shared.Return(rentedBuffer);

        return sqlStatementInfo;
    }

    private static void ParseToken(
        ReadOnlySpan<char> sql,
        Span<char> buffer,
        ref ParseState state)
    {
        var previousKeyword = state.KeywordHistory[0];
        ref readonly var keywordInfo = ref SqlKeywordInfo.GetInfo(previousKeyword);

        // As an optimization, we only compare for keywords if we haven't already captured 255 characters for the summary.
        // Avoid comparing for keywords if the previous token was a keyword that is expected to be followed by an identifier.
        if (state.SummaryPosition < 255 && !(state.PreviousTokenWasKeyword && keywordInfo.RequiresIdentifier))
        {
            // Quick first-character filter: only attempt keyword matching if the current char is an ASCII letter
            var c = sql[state.ParsePosition];
            var lower = (char)(c | 0x20);
            var canBeKeyword = lower >= 'a' && lower <= 'z';

            if (canBeKeyword)
            {
                // First check if the previous keyword may be the start of a keyword chain so we can reduce the
                // number of comparisons we need to do.
                if (keywordInfo.FollowedByKeywords.Length > 0)
                {
                    foreach (var followedByKeyword in keywordInfo.FollowedByKeywords)
                    {
                        if (TryWritePotentialKeyword(sql, followedByKeyword, buffer, ref state))
                        {
                            return;
                        }
                    }
                }

                // We didn't match any keywords in the chain, so we check all keywords that are standalone or
                // which are the first keyword in a chain.
                foreach (var sqlKeyword in SqlKeywords)
                {
                    if (TryWritePotentialKeyword(sql, in sqlKeyword, buffer, ref state))
                    {
                        return;
                    }
                }
            }
        }

        // If we get this far, we have not matched a keyword, so we copy the token as-is
        state.PreviousTokenWasKeyword = false;

        var nextChar = sql[state.ParsePosition];

        if (char.IsLetter(nextChar) || nextChar == '_')
        {
            if (state.CaptureNextTokenAsTarget && state.SummaryPosition < 255)
            {
                state.SummaryBuffer[state.SummaryPosition++] = ' ';
            }

            // Scan the identifier token once, then bulk-copy to minimize per-char branching
            var start = state.ParsePosition;
            var i = start;
            while (i < sql.Length)
            {
                var ch = sql[i];
                if (char.IsLetter(ch) || ch == '_' || ch == '.' || char.IsDigit(ch))
                {
                    i++;
                    continue;
                }

                break;
            }

            var length = i - start;
            if (length > 0)
            {
                // Copy to sanitized buffer
                sql.Slice(start, length).CopyTo(buffer.Slice(state.SanitizedPosition));
                state.SanitizedPosition += length;

                // Optionally copy to summary buffer
                if (state.CaptureNextTokenAsTarget && state.SummaryPosition < 255)
                {
                    var remaining = 255 - state.SummaryPosition;
                    var toCopy = length > remaining ? remaining : length;
                    sql.Slice(start, toCopy).CopyTo(state.SummaryBuffer.Slice(state.SummaryPosition));
                    state.SummaryPosition += toCopy;
                }
            }

            state.ParsePosition = i;
            state.CaptureNextTokenAsTarget = false;
        }
        else
        {
            if (state.KeywordHistory[0] == SqlKeyword.From && nextChar == ',')
            {
                state.CaptureNextTokenAsTarget = true;
            }

            buffer[state.SanitizedPosition++] = nextChar;
            state.ParsePosition++;
        }
    }

    private static bool TryWritePotentialKeyword(
        ReadOnlySpan<char> sql,
        in SqlKeywordInfo sqlKeywordInfo,
        Span<char> destination,
        ref ParseState state)
    {
        var sqlToCompare = sql.Slice(state.ParsePosition);
        var keywordSpan = sqlKeywordInfo.KeywordText.AsSpan();

        // if (sqlToCompare.Length < keywordSpan.Length)
        // {
        //    state.PreviousTokenWasKeyword = false;
        //    return false;
        // }

        // Check for whitespace after the potential token.
        // Early exit if no whitespace is found.
        if (sqlToCompare.Length > keywordSpan.Length)
        {
#if NET
            if (!WhitespaceSearchValues.Contains(sqlToCompare[keywordSpan.Length]))
#else
            var nextChar = sqlToCompare[keywordSpan.Length];
            if (nextChar != ' ' && nextChar != '\t' && nextChar != '\r' && nextChar != '\n')
#endif
            {
                state.PreviousTokenWasKeyword = false;
                return false;
            }
        }

        var compareIndex = -1;
        var matchedStatement = true;
        var initialSummaryPosition = state.SummaryPosition;

        while (++compareIndex < keywordSpan.Length)
        {
            var nextChar = sqlToCompare[compareIndex];

            // Optimized, case insensitive comparison
            if ((nextChar | 0x20) != (keywordSpan[compareIndex] | 0x20))
            {
                if (compareIndex == 0)
                {
                    // We didn't match the first character, so we return false immediately
                    state.PreviousTokenWasKeyword = false;
                    return false;
                }

                // We didn't match the statement, so we stop comparing
                // We don't return here as we may need to rewind any summary we may have written
                matchedStatement = false;
                break;
            }

            // Copy the character to the sanitized SQL within the buffer
            destination[state.SanitizedPosition++] = nextChar;

            // Truncate the summary at 255 characters
            if (matchedStatement && sqlKeywordInfo.CaptureInSummary && state.SummaryPosition < 255)
            {
                // Add a space before the keyword if it's not the first token in the summary
                if (compareIndex == 0 && state.SummaryPosition > 0)
                {
                    state.SummaryBuffer[state.SummaryPosition++] = ' ';
                }

                // Copy the character to the summary SQL within the buffer
                state.SummaryBuffer[state.SummaryPosition++] = nextChar;
            }

            state.ParsePosition++;
        }

        if (matchedStatement)
        {
            state.PreviousTokenWasKeyword = true;
            state.CaptureNextTokenAsTarget = sqlKeywordInfo.RequiresIdentifier;

            // Maintain a history of the last 4 keywords to handle identifying cases like "CREATE UNIQUE CLUSTERED INDEX"
            // The keyword at the lowest index is the newest keyword
            for (var i = state.KeywordHistory.Length - 1; i > 0; i--)
            {
                state.KeywordHistory[i] = state.KeywordHistory[i - 1];
            }

            state.KeywordHistory[0] = sqlKeywordInfo.SqlKeyword;
        }
        else
        {
            // Rewind the summary position if we didn't match a keyword while copying
            state.SummaryPosition = initialSummaryPosition;
            state.PreviousTokenWasKeyword = false;
        }

        return matchedStatement;
    }

    private static bool ParseWhitespace(ReadOnlySpan<char> sql, Span<char> buffer, ref ParseState state)
    {
        var foundWhitespace = false;

        while (state.ParsePosition < sql.Length)
        {
            var nextChar = sql[state.ParsePosition];

#if NET
            if (WhitespaceSearchValues.Contains(nextChar))
#else
            if (nextChar == ' ' || nextChar == '\t' || nextChar == '\r' || nextChar == '\n')
#endif
            {
                foundWhitespace = true;
                buffer[state.SanitizedPosition++] = nextChar;
                state.ParsePosition++;
                continue; // keep consuming contiguous whitespace
            }

            break; // stop when nextChar is not whitespace
        }

        return foundWhitespace;
    }

    private static bool SkipComment(ReadOnlySpan<char> sql, ref ParseState state)
    {
        var i = state.ParsePosition;
        var ch = sql[i];
        var length = sql.Length;

        // Scan past multi-line comment
        if (ch == '/' && i + 1 < length && sql[i + 1] == '*')
        {
#if NET
            var rest = sql.Slice(i + 2);
            while (!rest.IsEmpty)
            {
                var starIdx = rest.IndexOf('*');
                if (starIdx < 0)
                {
                    // Unterminated comment, consume to end
                    state.ParsePosition = length;
                    return true;
                }

                // Check for closing */
                if (starIdx + 1 < rest.Length && rest[starIdx + 1] == '/')
                {
                    state.ParsePosition = i + 2 + starIdx + 2; // position after */
                    return true;
                }

                // Continue searching after this '*'
                rest = rest.Slice(starIdx + 1);
            }

            state.ParsePosition = length;
            return true;
#else
            for (i += 2; i < length; ++i)
            {
                ch = sql[i];
                if (ch == '*' && i + 1 < length && sql[i + 1] == '/')
                {
                    i += 1;
                    break;
                }
            }

            state.ParsePosition = ++i;
            return true;
#endif
        }

        // Scan past single-line comment
        if (ch == '-' && i + 1 < length && sql[i + 1] == '-')
        {
#if NET
            // Find next line break efficiently and preserve the newline for whitespace handling
            var rest = sql.Slice(i + 2);
            var idx = rest.IndexOfAny('\r', '\n');
            if (idx >= 0)
            {
                // Position at the newline so ParseWhitespace can copy it
                state.ParsePosition = i + 2 + idx;
            }
            else
            {
                state.ParsePosition = sql.Length;
            }

            return true;
#else
            for (i += 2; i < length; ++i)
            {
                ch = sql[i];
                if (ch == '\r' || ch == '\n')
                {
                    i -= 1;
                    break;
                }
            }

            state.ParsePosition = ++i;
            return true;
#endif
        }

        return false;
    }

    private static bool SanitizeStringLiteral(ReadOnlySpan<char> sql, ref ParseState state)
    {
        var ch = sql[state.ParsePosition];
        if (ch == '\'')
        {
#if NET
            var rest = sql.Slice(state.ParsePosition + 1);
            while (!rest.IsEmpty)
            {
                var idx = rest.IndexOf('\'');
                if (idx < 0)
                {
                    state.ParsePosition = sql.Length;
                    return true;
                }

                if (idx + 1 < rest.Length && rest[idx + 1] == '\'')
                {
                    // Skip escaped quote ('')
                    rest = rest.Slice(idx + 2);
                    continue;
                }

                // Found terminating quote
                state.ParsePosition = (sql.Length - rest.Length) + idx + 1;
                return true;
            }

            return true;
#else
            var i = state.ParsePosition + 1;
            var length = sql.Length;
            for (; i < length; ++i)
            {
                ch = sql[i];
                if (ch == '\'' && i + 1 < length && sql[i + 1] == '\'')
                {
                    ++i;
                    continue;
                }

                if (ch == '\'')
                {
                    break;
                }
            }

            state.ParsePosition = ++i;
            return true;
#endif
        }

        return false;
    }

    private static bool SanitizeHexLiteral(ReadOnlySpan<char> sql, ref ParseState state)
    {
        var i = state.ParsePosition;
        var ch = sql[i];
        var length = sql.Length;

        if (ch == '0' && i + 1 < length && (sql[i + 1] == 'x' || sql[i + 1] == 'X'))
        {
            for (i += 2; i < length; ++i)
            {
                ch = sql[i];
                if (char.IsDigit(ch) ||
                    ch == 'A' || ch == 'a' ||
                    ch == 'B' || ch == 'b' ||
                    ch == 'C' || ch == 'c' ||
                    ch == 'D' || ch == 'd' ||
                    ch == 'E' || ch == 'e' ||
                    ch == 'F' || ch == 'f')
                {
                    continue;
                }

                i -= 1;
                break;
            }

            state.ParsePosition = ++i;
            return true;
        }

        return false;
    }

    private static bool SanitizeNumericLiteral(ReadOnlySpan<char> sql, ref ParseState state)
    {
        var i = state.ParsePosition;
        var ch = sql[i];
        var length = sql.Length;

        // Scan past leading sign
        if ((ch == '-' || ch == '+') && i + 1 < length && (char.IsDigit(sql[i + 1]) || sql[i + 1] == '.'))
        {
            i += 1;
            ch = sql[i];
        }

        // Scan past leading decimal point
        var periodMatched = false;
        if (ch == '.' && i + 1 < length && char.IsDigit(sql[i + 1]))
        {
            periodMatched = true;
            i += 1;
            ch = sql[i];
        }

        if (char.IsDigit(ch))
        {
            var exponentMatched = false;
            for (i += 1; i < length; ++i)
            {
                ch = sql[i];
                if (char.IsDigit(ch))
                {
                    continue;
                }

                if (!periodMatched && ch == '.')
                {
                    periodMatched = true;
                    continue;
                }

                if (!exponentMatched && (ch == 'e' || ch == 'E'))
                {
                    // Scan past sign in exponent
                    if (i + 1 < length && (sql[i + 1] == '-' || sql[i + 1] == '+'))
                    {
                        i += 1;
                    }

                    exponentMatched = true;
                    continue;
                }

                i -= 1;
                break;
            }

            state.ParsePosition = ++i;
            return true;
        }

        return false;
    }

    private ref struct ParseState
    {
        // NOTE: ParseState intentionally uses public fields (not properties).
        // - This is a ref struct that lives on the stack and is passed by ref through hot paths.
        // - Fields avoid property accessor calls in tight loops and yield smaller/faster code after inlining.
        // - Grouping Span<> fields first helps layout and may reduce padding on x64.
        // - Keeping the struct simple and flat minimizes stack pressure and lets the JIT keep values in registers.
        // If you add members, keep spans first, then ints, then bools, to preserve a compact layout.

        public Span<char> SummaryBuffer;
        public Span<SqlKeyword> KeywordHistory;

        public int ParsePosition;
        public int SanitizedPosition;
        public int SummaryPosition;

        public bool PreviousTokenWasKeyword;
        public bool CaptureNextTokenAsTarget;

        public ParseState(Span<SqlKeyword> historyBuffer)
        {
            this = default;
            this.KeywordHistory = historyBuffer;
        }
    }

    private readonly struct SqlKeywordInfo
    {
        // Order matters here. If a keyword can be followed by another keyword, the field(s)
        // it can be followed by should be declared first so that static initialization works.

        // Use static readonly fields (not properties) so we can return by-ref without copying.
        public static readonly SqlKeywordInfo UnknownKeyword =
            new(string.Empty, SqlKeyword.Unknown);

        public static readonly SqlKeywordInfo JoinKeyword =
            new("JOIN", SqlKeyword.Join, captureInSummary: false, requiresIdentifier: true);

        public static readonly SqlKeywordInfo FromKeyword =
            new("FROM", SqlKeyword.From, captureInSummary: false, requiresIdentifier: true, followedByKeywords: [JoinKeyword]);

        public static readonly SqlKeywordInfo DistinctKeyword =
            new("DISTINCT", SqlKeyword.Distinct, captureInSummary: true, followedByKeywords: [FromKeyword]);

        public static readonly SqlKeywordInfo SelectKeyword =
            new("SELECT", SqlKeyword.Select, captureInSummary: true, followedByKeywords: [FromKeyword, DistinctKeyword]);

        public static readonly SqlKeywordInfo IntoKeyword =
            new("INTO", SqlKeyword.Into, captureInSummary: false, requiresIdentifier: true);

        public static readonly SqlKeywordInfo InsertKeyword =
           new("INSERT", SqlKeyword.Insert, captureInSummary: true, followedByKeywords: [IntoKeyword]);

        public static readonly SqlKeywordInfo UpdateKeyword =
           new("UPDATE", SqlKeyword.Update, captureInSummary: true);

        public static readonly SqlKeywordInfo DeleteKeyword =
           new("DELETE", SqlKeyword.Delete, captureInSummary: true);

        public static readonly SqlKeywordInfo TableKeyword =
            new("TABLE", SqlKeyword.Table, captureInSummary: true, requiresIdentifier: true);

        public static readonly SqlKeywordInfo OnKeyword =
            new("ON", SqlKeyword.On, captureInSummary: false, requiresIdentifier: true);

        public static readonly SqlKeywordInfo IndexKeyword =
            new("INDEX", SqlKeyword.Index, captureInSummary: true, followedByKeywords: [OnKeyword]);

        public static readonly SqlKeywordInfo ClusteredKeyword =
            new("CLUSTERED", SqlKeyword.Clustered, captureInSummary: true, followedByKeywords: [IndexKeyword]);

        public static readonly SqlKeywordInfo NonClusteredKeyword =
            new("NONCLUSTERED", SqlKeyword.NonClustered, captureInSummary: true, followedByKeywords: [IndexKeyword]);

        public static readonly SqlKeywordInfo UniqueKeyword =
            new(
                "UNIQUE",
                SqlKeyword.Unique,
                captureInSummary: true,
                followedByKeywords: [IndexKeyword, ClusteredKeyword, NonClusteredKeyword]);

        public static readonly SqlKeywordInfo TriggerKeyword =
            new("TRIGGER", SqlKeyword.Trigger, captureInSummary: true, requiresIdentifier: true);

        public static readonly SqlKeywordInfo ViewKeyword =
            new("VIEW", SqlKeyword.View, captureInSummary: true, requiresIdentifier: true);

        public static readonly SqlKeywordInfo ProcedureKeyword =
            new("PROCEDURE", SqlKeyword.Procedure, captureInSummary: true, requiresIdentifier: true);

        public static readonly SqlKeywordInfo CreateKeyword =
            new(
                "CREATE",
                SqlKeyword.Create,
                captureInSummary: true,
                followedByKeywords: [TableKeyword, IndexKeyword, ViewKeyword, ProcedureKeyword, TriggerKeyword, UniqueKeyword]);

        public static readonly SqlKeywordInfo DropKeyword =
            new("DROP", SqlKeyword.Drop, captureInSummary: true, followedByKeywords: [TableKeyword, IndexKeyword]);

        public static readonly SqlKeywordInfo AlterKeyword =
            new("ALTER", SqlKeyword.Alter, captureInSummary: true, followedByKeywords: [TableKeyword]);

        public SqlKeywordInfo(
            string keyword,
            SqlKeyword sqlKeyword,
            bool captureInSummary = false,
            bool requiresIdentifier = false,
            SqlKeywordInfo[]? followedByKeywords = null)
        {
            this.KeywordText = keyword;
            this.SqlKeyword = sqlKeyword;
            this.CaptureInSummary = captureInSummary;
            this.RequiresIdentifier = requiresIdentifier;
            this.FollowedByKeywords = followedByKeywords ?? [];
        }

        public string KeywordText { get; }

        public bool CaptureInSummary { get; }

        public bool RequiresIdentifier { get; }

        public SqlKeyword SqlKeyword { get; }

        public SqlKeywordInfo[] FollowedByKeywords { get; }

        // Return by ref readonly to avoid copying the struct.
        public static ref readonly SqlKeywordInfo GetInfo(SqlKeyword sqlKeyword)
        {
            switch (sqlKeyword)
            {
                case SqlKeyword.Select:
                    return ref SelectKeyword;
                case SqlKeyword.From:
                    return ref FromKeyword;
                case SqlKeyword.Table:
                    return ref TableKeyword;
                case SqlKeyword.Unique:
                    return ref UniqueKeyword;
                case SqlKeyword.Clustered:
                    return ref ClusteredKeyword;
                case SqlKeyword.NonClustered:
                    return ref NonClusteredKeyword;
                case SqlKeyword.Index:
                    return ref IndexKeyword;
                case SqlKeyword.Create:
                    return ref CreateKeyword;
                case SqlKeyword.Drop:
                    return ref DropKeyword;
                case SqlKeyword.Procedure:
                    return ref ProcedureKeyword;
                case SqlKeyword.View:
                    return ref ViewKeyword;
                case SqlKeyword.Trigger:
                    return ref TriggerKeyword;
                case SqlKeyword.On:
                    return ref OnKeyword;
                case SqlKeyword.Distinct:
                    return ref DistinctKeyword;
                case SqlKeyword.Alter:
                    return ref AlterKeyword;
                case SqlKeyword.Insert:
                    return ref InsertKeyword;
                case SqlKeyword.Into:
                    return ref IntoKeyword;
                case SqlKeyword.Update:
                    return ref UpdateKeyword;
                case SqlKeyword.Delete:
                    return ref DeleteKeyword;
                case SqlKeyword.Join:
                    return ref JoinKeyword;
                default:
                    return ref UnknownKeyword;
            }
        }
    }
}
