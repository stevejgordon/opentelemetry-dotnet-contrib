// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;

namespace OpenTelemetry.Instrumentation;

internal static class SqlProcessor
{
    private const int CacheCapacity = 0;
    private static readonly Dictionary<string, SqlStatementInfo> Cache = [];

#if NET
    private static readonly SearchValues<char> WhitespaceSearchValues = SearchValues.Create([' ', '\t', '\r', '\n']);
#endif

    private static readonly string[] DmlStatements = ["SELECT", "INSERT", "UPDATE", "DELETE"];

    private static readonly string[] Clauses = ["FROM", "INTO", "JOIN"];

    private static readonly string[] DdlStatements = ["CREATE", "ALTER", "DROP"];

    // We can extend this in the future to include more keywords if needed.
    // The keywords should be ordered by frequency of use to optimize performance.
    private static readonly SqlKeywordInfo[] SqlKeywords =
    [
        new SqlKeywordInfo("SELECT", SqlKeyword.Select, captureInSummary: true),
        new SqlKeywordInfo("INSERT", SqlKeyword.Insert, captureInSummary: true),
        new SqlKeywordInfo("UPDATE", SqlKeyword.Update, captureInSummary: true),
        new SqlKeywordInfo("DELETE", SqlKeyword.Delete, captureInSummary: true),
        new SqlKeywordInfo("FROM", SqlKeyword.From, captureInSummary: false),
        new SqlKeywordInfo("INTO", SqlKeyword.Into, captureInSummary: false),
        new SqlKeywordInfo("JOIN", SqlKeyword.Join, captureInSummary: false),
        new SqlKeywordInfo("CREATE", SqlKeyword.Create, captureInSummary: true),
        new SqlKeywordInfo("ALTER", SqlKeyword.Alter, captureInSummary: true),
        new SqlKeywordInfo("DROP", SqlKeyword.Drop, captureInSummary: true),
        new SqlKeywordInfo("TABLE", SqlKeyword.Table, captureInSummary: false),
        new SqlKeywordInfo("INDEX", SqlKeyword.Index, captureInSummary: false),
        new SqlKeywordInfo("PROCEDURE", SqlKeyword.Procedure, captureInSummary: false),
        new SqlKeywordInfo("VIEW", SqlKeyword.View, captureInSummary: false),
        new SqlKeywordInfo("DATABASE", SqlKeyword.Database, captureInSummary: false),
        new SqlKeywordInfo("TRIGGER", SqlKeyword.Trigger, captureInSummary: false),
        new SqlKeywordInfo("UNIQUE", SqlKeyword.Unique, captureInSummary: false),
        new SqlKeywordInfo("NONCLUSTERED", SqlKeyword.NonClustered, captureInSummary: false),
        new SqlKeywordInfo("CLUSTERED", SqlKeyword.Clustered, captureInSummary: false),
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
    }

    public static SqlStatementInfo GetSanitizedSql(string? sql)
    {
        if (sql == null)
        {
            return default;
        }

        if (!Cache.TryGetValue(sql, out var sqlStatementInfo))
        {
            sqlStatementInfo = SanitizeSql(sql.AsSpan());

            if (Cache.Count == CacheCapacity)
            {
                return sqlStatementInfo;
            }

            lock (Cache)
            {
                if (!Cache.ContainsKey(sql))
                {
                    if (Cache.Count < CacheCapacity)
                    {
                        Cache[sql] = sqlStatementInfo;
                    }
                }
            }
        }

        return sqlStatementInfo;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.SpacingRules", "SA1005:Single line comments should begin with single space", Justification = "Temp")]
    private static SqlStatementInfo SanitizeSql(ReadOnlySpan<char> sql)
    {
        // We use a single buffer for both sanitized SQL and DB query summary
        // DB query summary starts from the index of the length of the input SQL
        // We rent a buffer twice the size of the input SQL to ensure we have enough space
        var rentedBuffer = ArrayPool<char>.Shared.Rent(sql.Length * 2);

        var buffer = rentedBuffer.AsSpan();

        var state = new ParseState
        {
            PreviousKeyword = SqlKeyword.Unknown,
            ParsePosition = 0,
            SanitizedPosition = 0,
            SummaryPosition = 0,
            CaptureNextTokenAsTarget = false,
            PreviousTokenWasKeyword = false,
            KeywordHistory = stackalloc SqlKeyword[4],
        };

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
            buffer.Slice(rentedBuffer.Length / 2, state.SummaryPosition).ToString());

        // We don't clear the buffer as we know the content has been sanitized
        ArrayPool<char>.Shared.Return(rentedBuffer);

        return sqlStatementInfo;
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
            if (nextChar == ' ' || nextChar == '\t' || nextChar == 'r' || nextChar == '\n')
#endif
            {
                foundWhitespace = true;
                buffer[state.SanitizedPosition++] = nextChar;
                state.ParsePosition++;
            }

            break;
        }

        return foundWhitespace;
    }

    private static bool SkipComment(ReadOnlySpan<char> sql, ref ParseState state)
    {
        var i = state.ParsePosition;
        var ch = sql[i];
        var length = sql.Length;

        // TODO - Use IndexOf and Slice

        // Scan past multi-line comment
        if (ch == '/' && i + 1 < length && sql[i + 1] == '*')
        {
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
        }

        // Scan past single-line comment
        if (ch == '-' && i + 1 < length && sql[i + 1] == '-')
        {
            for (i += 2; i < length; ++i)
            {
                ch = sql[i];
                if (ch is '\r' or '\n')
                {
                    i -= 1;
                    break;
                }
            }

            state.ParsePosition = ++i;
            return true;
        }

        return false;
    }

    private static bool SanitizeStringLiteral(ReadOnlySpan<char> sql, ref ParseState state)
    {
        var ch = sql[state.ParsePosition];
        if (ch == '\'')
        {
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

    private static void ParseToken(
        ReadOnlySpan<char> sql,
        Span<char> buffer,
        ref ParseState state)
    {
        var nextChar = sql[state.ParsePosition];
        var nextCharUpper = char.ToUpperInvariant(nextChar);

        var remainingSql = sql.Slice(state.ParsePosition);

        // Summary is truncated to max 255 characters.
        // We can fast pass through any remaining SQL for sanitization only.
        if (state.SummaryPosition < 255)
        {
            foreach (var keyword in DmlStatements)
            {
                if (nextCharUpper == keyword[0] && remainingSql.Length >= keyword.Length)
                {
                    if (TryWritePotentialKeyword(sql, keyword.AsSpan(), buffer, ref state))
                    {
                        return;
                    }
                }
            }

            foreach (var keyword in Clauses)
            {
                if (nextCharUpper == keyword[0] && remainingSql.Length >= keyword.Length)
                {
                    if (TryWritePotentialKeyword(sql, keyword.AsSpan(), buffer, ref state, copyToSummary: false))
                    {
                        return;
                    }
                }
            }

            foreach (var keyword in DdlStatements)
            {
                if (nextCharUpper == keyword[0] && remainingSql.Length >= keyword.Length)
                {
                }
            }
        }

        state.PreviousTokenWasKeyword = false;

        var summaryStartIndex = buffer.Length / 2;

        if (char.IsLetter(nextChar) || nextChar == '_')
        {
            if (state.CaptureNextTokenAsTarget && state.SummaryPosition < 255)
            {
                buffer.Slice(summaryStartIndex)[state.SummaryPosition++] = ' ';
            }

            while (state.ParsePosition < sql.Length)
            {
                nextChar = sql[state.ParsePosition];

                if (char.IsLetter(nextChar) || nextChar == '_' || nextChar == '.' || char.IsDigit(nextChar))
                {
                    buffer[state.SanitizedPosition++] = nextChar;

                    if (state.CaptureNextTokenAsTarget && state.SummaryPosition < 255)
                    {
                        buffer.Slice(summaryStartIndex)[state.SummaryPosition++] = nextChar;
                    }

                    state.ParsePosition++;
                    continue;
                }

                break;
            }

            state.CaptureNextTokenAsTarget = false;
        }
        else
        {
            if (state.PreviousKeyword == SqlKeyword.From && nextChar == ',')
            {
                state.CaptureNextTokenAsTarget = true;
            }

            buffer[state.SanitizedPosition++] = nextChar;
            state.ParsePosition++;
        }
    }

    private static bool TryWritePotentialKeyword(
        ReadOnlySpan<char> sql,
        ReadOnlySpan<char> statement,
        Span<char> destination,
        ref ParseState state,
        bool copyToSummary = true)
    {
        var sqlToCompare = sql.Slice(state.ParsePosition);

        // Check for whitespace after the potential token.
        // Early exit if no whitespace is found.
        if (sqlToCompare.Length > statement.Length)
        {
#if NET
            if (!WhitespaceSearchValues.Contains(sqlToCompare[statement.Length]))
#else
            var nextChar = sqlToCompare[statement.Length];
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
        var summaryStartIndex = destination.Length / 2;

        while (++compareIndex < statement.Length)
        {
            var nextChar = sqlToCompare[compareIndex];

            // Optimized, case insensitive comparison
            // If the next character is not a letter, we are done comparing.
            if (!char.IsLetter(nextChar) || (nextChar | 0x20) != (statement[compareIndex] | 0x20))
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
            if (matchedStatement && copyToSummary && state.SummaryPosition < 255)
            {
                var summaryDestination = destination.Slice(summaryStartIndex);

                // Add a space before the keyword if it's not the first token in the summary
                if (compareIndex == 0 && state.SummaryPosition > 0)
                {
                    summaryDestination[state.SummaryPosition++] = ' ';
                }

                // Copy the character to the summary SQL within the buffer
                summaryDestination[state.SummaryPosition++] = nextChar;
            }

            state.ParsePosition++;
        }

        if (matchedStatement)
        {
            var keyword = statement[0] switch
            {
                'S' => SqlKeyword.Select,
                'I' when statement.Length > 3 && statement[1] == 'N' && statement[2] == 'S' => SqlKeyword.Insert,
                'I' when statement.Length > 3 && statement[1] == 'N' && statement[2] == 'D' => SqlKeyword.Index,
                'I' => SqlKeyword.Into,
                'U' when statement.Length > 2 && statement[1] == 'P' => SqlKeyword.Update,
                'U' => SqlKeyword.Unique,
                'D' when statement.Length > 2 && statement[1] == 'E' => SqlKeyword.Delete,
                'D' => SqlKeyword.Drop,
                'F' => SqlKeyword.From,
                'J' => SqlKeyword.Join,
                'C' => SqlKeyword.Create,
                'A' => SqlKeyword.Alter,
                'P' => SqlKeyword.Procedure,
                'V' => SqlKeyword.View,
                'T' => SqlKeyword.Table, // TODO - Handle trigger
                _ => SqlKeyword.Unknown,
            };

            state.PreviousTokenWasKeyword = true;

            state.CaptureNextTokenAsTarget =
                keyword == SqlKeyword.From ||
                keyword == SqlKeyword.Into ||
                keyword == SqlKeyword.Join;

            // Maintain a history of the last 4 keywords to handle identifying cases like "CREATE UNIQUE CLUSTERED INDEX"
            // The keyword at the lowest index is the newest keyword
            for (var i = state.KeywordHistory.Length - 1; i > 0; i--)
            {
                state.KeywordHistory[i] = state.KeywordHistory[i - 1];
            }

            state.KeywordHistory[0] = keyword;
        }
        else
        {
            // Rewind the summary position if we didn't match a keyword while copying
            state.SummaryPosition = initialSummaryPosition;
            state.PreviousTokenWasKeyword = false;
        }

        return matchedStatement;
    }

    private ref struct ParseState
    {
        public SqlKeyword PreviousKeyword { get; set; }

        public int ParsePosition { get; set; }

        public int SanitizedPosition { get; set; }

        public int SummaryPosition { get; set; }

        public bool PreviousTokenWasKeyword { get; set; }

        public bool CaptureNextTokenAsTarget { get; set; }

        public Span<SqlKeyword> KeywordHistory { get; set; }
    }

    private readonly struct SqlKeywordInfo
    {
        public SqlKeywordInfo(string keyword, SqlKeyword sqlKeyword, bool captureInSummary)
        {
            this.Keyword = keyword;
            this.SqlKeyword = sqlKeyword;
            this.IsOperation = captureInSummary;
        }

        public readonly string Keyword { get; }

        public bool IsOperation { get; }

        public SqlKeyword SqlKeyword { get; }
    }
}
