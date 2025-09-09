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

        var captureNextTokenAsTarget = false;
        var inFromClause = false;
        var sanitizedPosition = 0;
        var summaryPosition = 0;
        var buffer = rentedBuffer.AsSpan();

        var parsePosition = 0;

        while (parsePosition < sql.Length)
        {
            if (SkipComment(sql, ref parsePosition))
            {
                continue;
            }

            if (SanitizeStringLiteral(sql, ref parsePosition) ||
                SanitizeHexLiteral(sql, ref parsePosition) ||
                SanitizeNumericLiteral(sql, ref parsePosition))
            {
                buffer[sanitizedPosition++] = '?';
                continue;
            }

            WriteToken(sql, ref parsePosition, buffer, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause);
        }

        var sqlStatementInfo = new SqlStatementInfo(
            buffer.Slice(0, sanitizedPosition).ToString(),
            buffer.Slice(rentedBuffer.Length / 2, summaryPosition).ToString());

        // We don't clear the buffer as we know the content has been sanitized
        ArrayPool<char>.Shared.Return(rentedBuffer);

        return sqlStatementInfo;
    }

    private static bool SkipComment(ReadOnlySpan<char> sql, ref int index)
    {
        var i = index;
        var ch = sql[i];
        var length = sql.Length;

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

            index = ++i;
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

            index = ++i;
            return true;
        }

        return false;
    }

    private static bool SanitizeStringLiteral(ReadOnlySpan<char> sql, ref int index)
    {
        var ch = sql[index];
        if (ch == '\'')
        {
            var i = index + 1;
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

            index = ++i;
            return true;
        }

        return false;
    }

    private static bool SanitizeHexLiteral(ReadOnlySpan<char> sql, ref int index)
    {
        var i = index;
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

            index = ++i;
            return true;
        }

        return false;
    }

    private static bool SanitizeNumericLiteral(ReadOnlySpan<char> sql, ref int index)
    {
        var i = index;
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

            index = ++i;
            return true;
        }

        return false;
    }

    private static bool TryWritePotentialKeyword(
        ReadOnlySpan<char> sql,
        ReadOnlySpan<char> statement,
        Span<char> destination,
        ref int parsePosition,
        ref int sanitizedPosition,
        ref int summaryPosition,
        bool isOperation = true)
    {
        var compareIndex = 0;
        var matchedStatement = true;
        var initialSummaryPosition = summaryPosition;
        var summaryStartIndex = destination.Length / 2;

        var sqlToCompare = sql.Slice(parsePosition);

        // Check for whitespace after the potential token.
        // Early exit if no whitespace is found.
        if (sqlToCompare.Length > statement.Length)
        {
#if NET
            if (!WhitespaceSearchValues.Contains(sqlToCompare[statement.Length]))
#else
            var nextChar = sqlToCompare[statement.Length];
            if (nextChar != ' ' && nextChar != '\t' && nextChar != 'r' && nextChar != '\n')
#endif
            {
                return false;
            }
        }

        while (compareIndex < statement.Length)
        {
            var nextChar = sqlToCompare[compareIndex];
            var nextCharUpper = char.ToUpperInvariant(nextChar);

            if (matchedStatement && nextCharUpper != statement[compareIndex])
            {
                matchedStatement = false;
            }

            destination[sanitizedPosition++] = nextChar;

            if (matchedStatement && isOperation && summaryPosition < 255)
            {
                var summaryDestination = destination.Slice(summaryStartIndex);

                if (compareIndex == 0 && summaryPosition > 0)
                {
                    summaryDestination[summaryPosition++] = ' ';
                }

                summaryDestination[summaryPosition++] = nextCharUpper;
            }

            compareIndex++;
            parsePosition++;
        }

        if (!matchedStatement)
        {
            summaryPosition = initialSummaryPosition;
        }

        return matchedStatement;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("StyleCop.CSharp.SpacingRules", "SA1005:Single line comments should begin with single space", Justification = "Temp")]
    private static void WriteToken(
        ReadOnlySpan<char> sql,
        ref int parsePosition,
        Span<char> buffer,
        ref int sanitizedPosition,
        ref int summaryPosition,
        ref bool captureNextTokenAsTarget,
        ref bool inFromClause)
    {
        var nextChar = sql[parsePosition];
        var nextCharUpper = char.ToUpperInvariant(nextChar);

        var remainingSql = sql.Slice(parsePosition);

        // Summary is truncated to max 255 characters.
        // We can fast pass through any remaining SQL for sanitization only.
        if (summaryPosition < 255)
        {
            foreach (var operation in DmlStatements)
            {
                if (nextCharUpper == operation[0] && remainingSql.Length >= operation.Length)
                {
                    if (TryWritePotentialKeyword(sql, operation.AsSpan(), buffer, ref parsePosition, ref sanitizedPosition, ref summaryPosition))
                    {
                        captureNextTokenAsTarget = false;
                        inFromClause = false;
                        return;
                    }
                }
            }

            foreach (var clause in Clauses)
            {
                if (nextCharUpper == clause[0] && remainingSql.Length >= clause.Length)
                {
                    if (TryWritePotentialKeyword(sql, clause.AsSpan(), buffer, ref parsePosition, ref sanitizedPosition, ref summaryPosition, isOperation: false))
                    {
                        captureNextTokenAsTarget = true;
                        inFromClause = clause[0] == 'F';
                        return;
                    }
                }
            }

            foreach (var ddl in DdlStatements)
            {
                if (nextCharUpper == ddl[0] && remainingSql.Length >= ddl.Length)
                {
                }
            }
        }

        var summaryStartIndex = buffer.Length / 2;

        if (char.IsLetter(nextChar) || nextChar == '_')
        {
            if (captureNextTokenAsTarget && summaryPosition < 255)
            {
                buffer.Slice(summaryStartIndex)[summaryPosition++] = ' ';
            }

            while (parsePosition < sql.Length)
            {
                nextChar = sql[parsePosition];

                if (char.IsLetter(nextChar) || nextChar == '_' || nextChar == '.' || char.IsDigit(nextChar))
                {
                    buffer[sanitizedPosition++] = nextChar;

                    if (captureNextTokenAsTarget && summaryPosition < 255)
                    {
                        buffer.Slice(summaryStartIndex)[summaryPosition++] = nextChar;
                    }

                    parsePosition++;
                    continue;
                }

                break;
            }

            captureNextTokenAsTarget = inFromClause && nextChar == ',';
        }
        else
        {
            buffer[sanitizedPosition++] = nextChar;
            parsePosition++;
        }
    }

    /// <summary>
    /// Special handling for SQL Data Definition Language operations.
    /// </summary>
    private static bool LookAheadDdl(
        ReadOnlySpan<char> operation,
        ReadOnlySpan<char> sql,
        ref int index,
        Span<char> buffer,
        int summaryStartIndex,
        ref int sanitizedPosition,
        ref int summaryPosition,
        ref bool captureNextTokenAsTarget,
        ref bool inFromClause)
    {
        var initialIndex = index;

        if (LookAhead(operation, sql, ref index, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause, false, false))
        {
            for (; index < sql.Length && char.IsWhiteSpace(sql[index]); ++index)
            {
                buffer[sanitizedPosition++] = sql[index];
            }

            if (LookAhead("TABLE".AsSpan(), sql, ref index, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause, false, false) ||
                LookAhead("INDEX".AsSpan(), sql, ref index, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause, false, false) ||
                LookAhead("PROCEDURE".AsSpan(), sql, ref index, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause, false, false) ||
                LookAhead("VIEW".AsSpan(), sql, ref index, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause, false, false) ||
                LookAhead("DATABASE".AsSpan(), sql, ref index, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause, false, false))
            {
                captureNextTokenAsTarget = true;
            }

            for (var i = initialIndex; i < index; ++i)
            {
                var ch = sql[i];

                if (summaryPosition == 0)
                {
                    buffer.Slice(summaryStartIndex)[summaryPosition++] = sql[i];
                    continue;
                }

                if (buffer[summaryStartIndex + summaryPosition - 1] == ' ' && char.IsWhiteSpace(sql[i]))
                {
                    continue;
                }

                buffer.Slice(summaryStartIndex)[summaryPosition++] = sql[i];
            }

            return true;
        }

        return false;
    }

    private static bool LookAhead(
        ReadOnlySpan<char> compare,
        ReadOnlySpan<char> sql,
        ref int index,
        Span<char> buffer,
        int summaryStartIndex,
        ref int sanitizedPosition,
        ref int summaryPosition,
        ref bool captureNextTokenAsTarget,
        ref bool inFromClause,
        bool isOperation = true,
        bool captureNextTokenAsTargetIfMatched = false,
        bool inFromClauseIfMatched = false)
    {
        int i = index;
        var sqlLength = sql.Length;
        var compareLength = compare.Length;

        for (var j = 0; i < sqlLength && j < compareLength; ++i, ++j)
        {
            if (char.ToUpperInvariant(sql[i]) != compare[j])
            {
                return false;
            }
        }

        if (i >= sqlLength)
        {
            // when the sql ends with the beginning of the compare string
            return false;
        }

        var ch = sql[i];
        if (char.IsLetter(ch) || ch == '_' || char.IsDigit(ch))
        {
            return false;
        }

        if (isOperation)
        {
            if (summaryPosition > 0)
            {
                buffer.Slice(summaryStartIndex)[summaryPosition++] = ' ';
            }

            for (var k = index; k < i; ++k)
            {
                buffer[sanitizedPosition++] = sql[k];
                buffer.Slice(summaryStartIndex)[summaryPosition++] = sql[k];
            }
        }
        else
        {
            for (var k = index; k < i; ++k)
            {
                buffer[sanitizedPosition++] = sql[k];
            }
        }

        index = i;
        captureNextTokenAsTarget = captureNextTokenAsTargetIfMatched;
        inFromClause = inFromClauseIfMatched;
        return true;
    }
}
