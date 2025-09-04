// Copyright The OpenTelemetry Authors
// SPDX-License-Identifier: Apache-2.0

using System.Buffers;

namespace OpenTelemetry.Instrumentation;

internal static class SqlProcessor
{
    private const int CacheCapacity = 0;
    private static readonly Dictionary<string, SqlStatementInfo> Cache = [];

    public static SqlStatementInfo GetSanitizedSql(string? sql)
    {
        if (sql == null)
        {
            return default;
        }

        if (!Cache.TryGetValue(sql, out var sqlStatementInfo))
        {
            sqlStatementInfo = SanitizeSql(sql);

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

    private static SqlStatementInfo SanitizeSql(string sql)
    {
        // We use a single buffer for both sanitized SQL and DB query summary
        // DB query summary starts from the index of the length of the input SQL
        // We rent a buffer twice the size of the input SQL to ensure we have enough space
        var buffer = ArrayPool<char>.Shared.Rent(sql.Length * 2);

        var summaryStartIndex = sql.Length;
        var captureNextTokenAsTarget = false;
        var inFromClause = false;
        var sanitizedPosition = 0;
        var summaryPosition = 0;
        var bufferSpan = buffer.AsSpan();

        for (var i = 0; i < sql.Length; ++i)
        {
            if (SkipComment(sql, ref i))
            {
                continue;
            }

            if (SanitizeStringLiteral(sql, ref i) ||
                SanitizeHexLiteral(sql, ref i) ||
                SanitizeNumericLiteral(sql, ref i))
            {
                bufferSpan[sanitizedPosition++] = '?';
                continue;
            }

            WriteToken(sql, ref i, bufferSpan, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause);
        }

        var sqlStatementInfo = new SqlStatementInfo(
            bufferSpan.Slice(0, sanitizedPosition).ToString(),
            bufferSpan.Slice(summaryStartIndex, summaryPosition).ToString());

        // We don't clear the buffer as we know the content has been sanitized
        ArrayPool<char>.Shared.Return(buffer);

        return sqlStatementInfo;
    }

    private static bool SkipComment(string sql, ref int index)
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

            index = i;
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

            index = i;
            return true;
        }

        return false;
    }

    private static bool SanitizeStringLiteral(string sql, ref int index)
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

            index = i;
            return true;
        }

        return false;
    }

    private static bool SanitizeHexLiteral(string sql, ref int index)
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

            index = i;
            return true;
        }

        return false;
    }

    private static bool SanitizeNumericLiteral(string sql, ref int index)
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

            index = i;
            return true;
        }

        return false;
    }

    private static void WriteToken(
        string sql,
        ref int index,
        Span<char> buffer,
        int summaryStartIndex,
        ref int sanitizedPosition,
        ref int summaryPosition,
        ref bool captureNextTokenAsTarget,
        ref bool inFromClause)
    {
        var i = index;
        var ch = sql[i];

        if (LookAhead("SELECT", sql, ref i, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause) ||
            LookAhead("UPDATE", sql, ref i, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause) ||
            LookAhead("INSERT", sql, ref i, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause) ||
            LookAhead("DELETE", sql, ref i, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause) ||
            LookAheadDdl("CREATE", sql, ref i, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause) ||
            LookAheadDdl("ALTER", sql, ref i, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause) ||
            LookAheadDdl("DROP", sql, ref i, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause) ||
            LookAhead("INTO", sql, ref i, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause, false, true) ||
            LookAhead("FROM", sql, ref i, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause, false, true, true) ||
            LookAhead("JOIN", sql, ref i, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause, false, true))
        {
            i -= 1;
        }
        else if (char.IsLetter(ch) || ch == '_')
        {
            for (; i < sql.Length; i++)
            {
                ch = sql[i];
                if (char.IsLetter(ch) || ch == '_' || ch == '.' || char.IsDigit(ch))
                {
                    buffer[sanitizedPosition++] = ch;
                    continue;
                }

                break;
            }

            if (captureNextTokenAsTarget)
            {
                captureNextTokenAsTarget = false;
                var toAppend = sql.AsSpan(index, i - index);

                if (summaryPosition > 0)
                {
                    buffer[summaryStartIndex + summaryPosition++] = ' ';
                }

                toAppend.CopyTo(buffer.Slice(summaryStartIndex + summaryPosition));
                summaryPosition += toAppend.Length;
            }

            i -= 1;

            if (inFromClause && ch == ',')
            {
                captureNextTokenAsTarget = true;
            }
        }
        else
        {
            buffer[sanitizedPosition++] = ch;
        }

        index = i;
    }

    /// <summary>
    /// Special handling for SQL Data Definition Language operations.
    /// </summary>
    private static bool LookAheadDdl(
        string operation,
        string sql,
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

            if (LookAhead("TABLE", sql, ref index, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause, false, false) ||
                LookAhead("INDEX", sql, ref index, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause, false, false) ||
                LookAhead("PROCEDURE", sql, ref index, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause, false, false) ||
                LookAhead("VIEW", sql, ref index, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause, false, false) ||
                LookAhead("DATABASE", sql, ref index, buffer, summaryStartIndex, ref sanitizedPosition, ref summaryPosition, ref captureNextTokenAsTarget, ref inFromClause, false, false))
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
        string compare,
        string sql,
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
