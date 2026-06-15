using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MemoryMcp.Core.Query;

/// <summary>A bound SQL parameter for a compiled filter.</summary>
/// <param name="Name">Parameter placeholder, e.g. <c>$f0</c>.</param>
/// <param name="Value">The value to bind.</param>
public sealed record FilterParameter(string Name, object Value);

/// <summary>A compiled filter: a SQL boolean fragment plus the parameters it references.</summary>
/// <param name="Sql">SQL fragment over the <c>n</c> alias (e.g. <c>(n.status = $f0)</c>).</param>
/// <param name="Parameters">Parameters to bind on the command.</param>
public sealed record CompiledFilter(string Sql, IReadOnlyList<FilterParameter> Parameters);

/// <summary>
/// Compiles a small, safe filter DSL into parameterized SQL over the <c>n</c> (notes) alias.
/// Grammar: <c>expr := or; or := and (OR and)*; and := primary (AND primary)*;
/// primary := '(' expr ')' | field op value; field := ident | payload.ident;
/// op := == | != | in; value := string | number; (in takes a parenthesized list)</c>.
/// Values are always bound as parameters; field names are whitelisted (envelope) or validated
/// (payload.&lt;key&gt;) — user input never reaches the SQL text directly.
/// </summary>
public static class NoteFilter
{
    private static readonly HashSet<string> EnvelopeColumns = new(StringComparer.Ordinal)
    {
        "id", "domain", "type", "title", "body", "status", "dedup_key",
        "source_agent", "schema_ver", "created_utc", "updated_utc", "deleted",
    };

    private static readonly Regex PayloadKey = new("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>Compiles a filter expression into parameterized SQL. Throws <see cref="FilterException"/> on errors.</summary>
    /// <param name="expression">The filter expression.</param>
    public static CompiledFilter Compile(string expression)
    {
        var parser = new Parser(Tokenize(expression));
        var sql = parser.ParseExpression();
        parser.ExpectEnd();
        return new CompiledFilter(sql, parser.Parameters);
    }

    private enum Kind { LParen, RParen, Comma, Eq, NotEq, And, Or, In, Ident, Str, Num, End }

    private readonly record struct Token(Kind Kind, string Text);

    private static List<Token> Tokenize(string input)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < input.Length)
        {
            var c = input[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            switch (c)
            {
                case '(': tokens.Add(new Token(Kind.LParen, "(")); i++; continue;
                case ')': tokens.Add(new Token(Kind.RParen, ")")); i++; continue;
                case ',': tokens.Add(new Token(Kind.Comma, ",")); i++; continue;
                case '=':
                    i += i + 1 < input.Length && input[i + 1] == '=' ? 2 : 1; // accept = or ==
                    tokens.Add(new Token(Kind.Eq, "=="));
                    continue;
                case '!':
                    if (i + 1 < input.Length && input[i + 1] == '=') { tokens.Add(new Token(Kind.NotEq, "!=")); i += 2; continue; }
                    throw new FilterException("Invalid filter: expected '!=' after '!'.");
                case '\'':
                case '"':
                    i = ReadString(input, i, tokens);
                    continue;
            }

            if (char.IsDigit(c)) { i = ReadNumber(input, i, tokens); continue; }
            if (char.IsLetter(c) || c == '_') { i = ReadWord(input, i, tokens); continue; }

            throw new FilterException($"Invalid filter: unexpected character '{c}'.");
        }

        tokens.Add(new Token(Kind.End, string.Empty));
        return tokens;
    }

    private static int ReadNumber(string input, int start, List<Token> tokens)
    {
        var i = start;
        while (i < input.Length && (char.IsDigit(input[i]) || input[i] == '.')) i++;
        tokens.Add(new Token(Kind.Num, input[start..i]));
        return i;
    }

    private static int ReadWord(string input, int start, List<Token> tokens)
    {
        var i = start;
        while (i < input.Length && (char.IsLetterOrDigit(input[i]) || input[i] == '_' || input[i] == '.')) i++;
        var word = input[start..i];
        var kind = word.ToLowerInvariant() switch
        {
            "and" => Kind.And,
            "or" => Kind.Or,
            "in" => Kind.In,
            _ => Kind.Ident,
        };
        tokens.Add(new Token(kind, word));
        return i;
    }

    private static int ReadString(string input, int i, List<Token> tokens)
    {
        var quote = input[i];
        i++;
        var builder = new StringBuilder();
        while (i < input.Length)
        {
            if (input[i] == quote)
            {
                if (i + 1 < input.Length && input[i + 1] == quote) { builder.Append(quote); i += 2; continue; } // doubled = escape
                tokens.Add(new Token(Kind.Str, builder.ToString()));
                return i + 1;
            }

            builder.Append(input[i]);
            i++;
        }

        throw new FilterException("Invalid filter: unterminated string literal.");
    }

    private sealed class Parser
    {
        private readonly List<Token> _tokens;
        private int _pos;
        private int _paramIndex;

        public Parser(List<Token> tokens) => _tokens = tokens;

        public List<FilterParameter> Parameters { get; } = new();

        public string ParseExpression() => ParseOr();

        public void ExpectEnd()
        {
            if (Peek().Kind != Kind.End)
            {
                throw new FilterException($"Invalid filter: unexpected '{Peek().Text}'.");
            }
        }

        private string ParseOr()
        {
            var left = ParseAnd();
            while (Peek().Kind == Kind.Or)
            {
                Next();
                left = $"({left} OR {ParseAnd()})";
            }

            return left;
        }

        private string ParseAnd()
        {
            var left = ParsePrimary();
            while (Peek().Kind == Kind.And)
            {
                Next();
                left = $"({left} AND {ParsePrimary()})";
            }

            return left;
        }

        private string ParsePrimary()
        {
            if (Peek().Kind == Kind.LParen)
            {
                Next();
                var inner = ParseOr();
                Expect(Kind.RParen);
                return $"({inner})";
            }

            return ParseComparison();
        }

        private string ParseComparison()
        {
            var column = ParseField();
            var op = Next();
            switch (op.Kind)
            {
                case Kind.Eq:
                    return $"{column} = {AddParam(ParseScalar())}";
                case Kind.NotEq:
                    return $"{column} <> {AddParam(ParseScalar())}";
                case Kind.In:
                    Expect(Kind.LParen);
                    var placeholders = new List<string> { AddParam(ParseScalar()) };
                    while (Peek().Kind == Kind.Comma)
                    {
                        Next();
                        placeholders.Add(AddParam(ParseScalar()));
                    }

                    Expect(Kind.RParen);
                    return $"{column} IN ({string.Join(", ", placeholders)})";
                default:
                    throw new FilterException($"Invalid filter: expected an operator (== != in), got '{op.Text}'.");
            }
        }

        private string ParseField()
        {
            var token = Next();
            if (token.Kind != Kind.Ident)
            {
                throw new FilterException($"Invalid filter: expected a field name, got '{token.Text}'.");
            }

            var name = token.Text;
            if (name.StartsWith("payload.", StringComparison.Ordinal))
            {
                var key = name["payload.".Length..];
                if (!PayloadKey.IsMatch(key))
                {
                    throw new FilterException($"Invalid filter: invalid payload field '{name}'.");
                }

                return $"json_extract(n.payload_json, '$.{key}')";
            }

            if (!EnvelopeColumns.Contains(name))
            {
                throw new FilterException($"Invalid filter: unknown field '{name}'.");
            }

            return $"n.{name}";
        }

        private object ParseScalar()
        {
            var token = Next();
            return token.Kind switch
            {
                Kind.Str => token.Text,
                Kind.Num => ParseNumber(token.Text),
                _ => throw new FilterException($"Invalid filter: expected a value, got '{token.Text}'."),
            };
        }

        private string AddParam(object value)
        {
            var name = $"$f{_paramIndex++}";
            Parameters.Add(new FilterParameter(name, value));
            return name;
        }

        private static object ParseNumber(string text) =>
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                ? l
                : double.Parse(text, CultureInfo.InvariantCulture);

        private Token Peek() => _tokens[_pos];

        private Token Next() => _tokens[_pos++];

        private void Expect(Kind kind)
        {
            var token = Next();
            if (token.Kind != kind)
            {
                throw new FilterException($"Invalid filter: expected '{kind}', got '{token.Text}'.");
            }
        }
    }
}
