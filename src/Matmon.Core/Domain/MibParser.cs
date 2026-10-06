using System.Globalization;
using System.Text;

namespace Matmon.Core.Domain;

/// <summary>One parsed MIB module (a file may hold several).</summary>
public sealed record MibModule(
    string Name,
    IReadOnlyDictionary<string, string> Imports,
    IReadOnlyList<MibDefinition> Definitions,
    IReadOnlyDictionary<string, MibTypeDefinition> Types,
    string? LastUpdated,
    IReadOnlyList<string> Errors);

/// <summary>A named node of the OID tree: OBJECT-TYPE, OBJECT IDENTIFIER, MODULE-IDENTITY, NOTIFICATION-TYPE ...</summary>
public sealed record MibDefinition(
    string Name,
    string Kind,
    IReadOnlyList<MibOidComponent> Oid,
    MibSyntax? Syntax = null,
    string? Units = null,
    string? Access = null,
    string? Status = null,
    string? Description = null,
    IReadOnlyList<string>? Index = null);

/// <summary>One component of an OID value: <c>enterprises</c>, <c>6574</c> or <c>org(3)</c>.</summary>
public sealed record MibOidComponent(string? Name, long? Number);

/// <summary>A SYNTAX: its base (a primitive or a textual-convention name) and any named numbers / bits.</summary>
public sealed record MibSyntax(string BaseType, IReadOnlyDictionary<long, string>? Enums = null);

/// <summary>A type assignment, typically a TEXTUAL-CONVENTION (TruthValue, DisplayString, ...).</summary>
public sealed record MibTypeDefinition(string Name, MibSyntax Syntax, string? DisplayHint, string? Description);

/// <summary>
/// A tolerant SMIv1/SMIv2 parser - enough of ASN.1 to build the OID tree and say what each node is: names, OIDs,
/// SYNTAX (with enums, through textual conventions), UNITS, MAX-ACCESS, DESCRIPTION, INDEX. It does not validate:
/// vendor MIBs are routinely sloppy (missing imports, SMIv1/v2 mixed, stray commas), and a parser that rejects a
/// file because of a typo in a REFERENCE clause helps nobody. An assignment that cannot be read is recorded in
/// <see cref="MibModule.Errors"/> and skipped; the rest of the module still loads.
///
/// The one structural trick: a type assignment (<c>Foo ::= ...</c>) has no terminator, so where it ends is only
/// knowable by parsing the type expression itself (<see cref="ParseType"/>). Everything else ends at its value.
/// </summary>
public static class MibParser
{
    private static readonly HashSet<string> MacroKinds = new(StringComparer.Ordinal)
    {
        "OBJECT-TYPE", "MODULE-IDENTITY", "OBJECT-IDENTITY", "NOTIFICATION-TYPE", "TRAP-TYPE",
        "OBJECT-GROUP", "NOTIFICATION-GROUP", "MODULE-COMPLIANCE", "AGENT-CAPABILITIES", "TEXTUAL-CONVENTION"
    };

    public static IReadOnlyList<MibModule> Parse(string text)
    {
        var tokens = Tokenize(text);
        var modules = new List<MibModule>();
        var position = 0;
        while (position < tokens.Count)
        {
            // NAME [ { oid } ] DEFINITIONS [tags] ::= BEGIN
            var definitions = IndexOf(tokens, position, "DEFINITIONS");
            if (definitions < 0)
            {
                break;
            }
            var nameIndex = definitions - 1;
            if (nameIndex >= 0 && tokens[nameIndex].Text == "}")
            {
                while (nameIndex >= 0 && tokens[nameIndex].Text != "{")
                {
                    nameIndex--;
                }
                nameIndex--;
            }
            var begin = IndexOf(tokens, definitions, "BEGIN");
            if (nameIndex < 0 || begin < 0 || !tokens[nameIndex].IsIdentifier)
            {
                position = definitions + 1;
                continue;
            }

            var reader = new Reader(tokens, begin + 1);
            modules.Add(ParseModule(tokens[nameIndex].Text, reader));
            position = reader.Position;
        }

        return modules;
    }

    private static MibModule ParseModule(string name, Reader reader)
    {
        var imports = new Dictionary<string, string>(StringComparer.Ordinal);
        var definitions = new List<MibDefinition>();
        var types = new Dictionary<string, MibTypeDefinition>(StringComparer.Ordinal);
        var errors = new List<string>();
        string? lastUpdated = null;

        while (!reader.AtEnd && reader.Peek().Text != "END")
        {
            var start = reader.Position;
            try
            {
                var token = reader.Peek();
                if (token.Text == "IMPORTS")
                {
                    reader.Next();
                    ParseImports(reader, imports);
                    continue;
                }
                if (token.Text == "EXPORTS")
                {
                    reader.SkipUntil(";");
                    continue;
                }
                if (!token.IsIdentifier)
                {
                    reader.Next();
                    continue;
                }
                // A MACRO definition (in the SMI modules themselves) is opaque - skip to its END.
                if (reader.Peek(1).Text == "MACRO")
                {
                    reader.SkipUntil("END");
                    continue;
                }

                if (char.IsUpper(token.Text[0]) && reader.Peek(1).Text == "::=")
                {
                    reader.Next();
                    reader.Next();
                    var type = ParseTypeAssignment(token.Text, reader);
                    if (type is not null)
                    {
                        types[type.Name] = type;
                    }
                    continue;
                }

                var definition = ParseValueAssignment(reader, out var updated);
                lastUpdated ??= updated;
                if (definition is not null)
                {
                    definitions.Add(definition);
                }
            }
            catch (FormatException exception)
            {
                errors.Add($"{reader.Describe(start)}: {exception.Message}");
                reader.Resync(start + 1);
            }
        }

        if (!reader.AtEnd)
        {
            reader.Next(); // END
        }

        return new MibModule(name, imports, definitions, types, lastUpdated, errors);
    }

    private static void ParseImports(Reader reader, Dictionary<string, string> imports)
    {
        var pending = new List<string>();
        while (!reader.AtEnd)
        {
            var token = reader.Next();
            if (token.Text == ";")
            {
                return;
            }
            if (token.Text == ",")
            {
                continue;
            }
            if (token.Text == "FROM")
            {
                var module = reader.Next().Text;
                foreach (var symbol in pending)
                {
                    imports.TryAdd(symbol, module);
                }
                pending.Clear();
                // "FROM Module { oid }" (rare) - skip the oid.
                if (reader.Peek().Text == "{")
                {
                    reader.SkipBalanced();
                }
                continue;
            }
            pending.Add(token.Text);
        }
    }

    /// <summary><c>name KIND clauses ::= value</c>. Returns null for assignments that are not OID-tree nodes.</summary>
    private static MibDefinition? ParseValueAssignment(Reader reader, out string? lastUpdated)
    {
        lastUpdated = null;
        var name = reader.Next().Text;
        string kind;
        if (reader.Peek().Text == "OBJECT" && reader.Peek(1).Text == "IDENTIFIER")
        {
            reader.Next();
            reader.Next();
            kind = "OBJECT IDENTIFIER";
        }
        else
        {
            kind = reader.Next().Text;
        }

        MibSyntax? syntax = null;
        string? units = null, access = null, status = null, description = null;
        List<string>? index = null;
        var opaque = kind is "MODULE-COMPLIANCE" or "AGENT-CAPABILITIES";

        while (!reader.AtEnd && reader.Peek().Text != "::=")
        {
            var clause = reader.Next();
            if (opaque)
            {
                if (clause.Text == "{")
                {
                    reader.Back();
                    reader.SkipBalanced();
                }
                continue;
            }
            switch (clause.Text)
            {
                case "SYNTAX":
                    syntax = ParseType(reader, out _);
                    break;
                case "UNITS":
                    units = reader.Next().StringValue;
                    break;
                case "MAX-ACCESS" or "ACCESS" or "MIN-ACCESS":
                    access = reader.Next().Text;
                    break;
                case "STATUS":
                    status = reader.Next().Text;
                    break;
                case "DESCRIPTION":
                    // MODULE-IDENTITY repeats DESCRIPTION per REVISION - the first one describes the module.
                    description ??= reader.Next().StringValue;
                    break;
                case "LAST-UPDATED":
                    lastUpdated = reader.Next().StringValue;
                    break;
                case "INDEX":
                    index = ReadIndex(reader);
                    break;
                case "{" or "(":
                    reader.Back();
                    reader.SkipBalanced();
                    break;
            }
        }

        if (reader.AtEnd)
        {
            throw new FormatException($"'{name}' has no value");
        }
        reader.Next(); // ::=

        if (reader.Peek().Text != "{")
        {
            // TRAP-TYPE ::= 6, a plain INTEGER value, ... - not a tree node.
            reader.Next();
            return null;
        }

        var oid = ReadOidValue(reader);
        return kind is "OBJECT IDENTIFIER" || MacroKinds.Contains(kind)
            ? new MibDefinition(name, kind, oid, syntax, units, access, status, description, index)
            : null;
    }

    private static List<string> ReadIndex(Reader reader)
    {
        var index = new List<string>();
        if (reader.Next().Text != "{")
        {
            return index;
        }
        while (!reader.AtEnd)
        {
            var token = reader.Next();
            if (token.Text == "}")
            {
                break;
            }
            if (token.IsIdentifier && token.Text != "IMPLIED")
            {
                index.Add(token.Text);
            }
        }
        return index;
    }

    private static List<MibOidComponent> ReadOidValue(Reader reader)
    {
        reader.Expect("{");
        var components = new List<MibOidComponent>();
        while (!reader.AtEnd)
        {
            var token = reader.Next();
            if (token.Text == "}")
            {
                return components;
            }
            if (token.IsNumber)
            {
                components.Add(new MibOidComponent(null, token.Number));
                continue;
            }
            if (!token.IsIdentifier)
            {
                continue;
            }
            // name(number)
            if (reader.Peek().Text == "(" && reader.Peek(1).IsNumber && reader.Peek(2).Text == ")")
            {
                reader.Next();
                var number = reader.Next().Number;
                reader.Next();
                components.Add(new MibOidComponent(token.Text, number));
                continue;
            }
            components.Add(new MibOidComponent(token.Text, null));
        }
        throw new FormatException("unterminated OID value");
    }

    private static MibTypeDefinition? ParseTypeAssignment(string name, Reader reader)
    {
        var syntax = ParseType(reader, out var convention);
        return syntax is null ? null : new MibTypeDefinition(name, syntax, convention?.DisplayHint, convention?.Description);
    }

    private sealed record Convention(string? DisplayHint, string? Description);

    /// <summary>
    /// Reads one type expression and stops exactly at its end - the only way to know where a type assignment
    /// stops. Handles tags, TEXTUAL-CONVENTION, SEQUENCE [OF], CHOICE, named numbers and constraints.
    /// </summary>
    private static MibSyntax? ParseType(Reader reader, out Convention? convention)
    {
        convention = null;
        if (reader.Peek().Text == "[")
        {
            reader.SkipBalanced(); // [APPLICATION 4]
        }
        if (reader.Peek().Text is "IMPLICIT" or "EXPLICIT")
        {
            reader.Next();
        }

        var head = reader.Next();
        switch (head.Text)
        {
            case "TEXTUAL-CONVENTION":
            {
                string? hint = null, description = null;
                MibSyntax? inner = null;
                while (!reader.AtEnd)
                {
                    var clause = reader.Next();
                    if (clause.Text == "DISPLAY-HINT")
                    {
                        hint = reader.Next().StringValue;
                    }
                    else if (clause.Text == "DESCRIPTION")
                    {
                        description = reader.Next().StringValue;
                    }
                    else if (clause.Text is "STATUS")
                    {
                        reader.Next();
                    }
                    else if (clause.Text is "REFERENCE")
                    {
                        reader.Next();
                    }
                    else if (clause.Text == "SYNTAX")
                    {
                        inner = ParseType(reader, out _);
                        break;
                    }
                }
                convention = new Convention(hint, description);
                return inner;
            }
            case "SEQUENCE" or "SET":
                if (reader.Peek().Text == "OF")
                {
                    reader.Next();
                    var element = ParseType(reader, out _);
                    return new MibSyntax($"SEQUENCE OF {element?.BaseType}");
                }
                reader.SkipBalanced();
                return new MibSyntax("SEQUENCE");
            case "CHOICE":
                reader.SkipBalanced();
                return new MibSyntax("CHOICE");
            case "OBJECT":
                reader.Expect("IDENTIFIER");
                return new MibSyntax("OBJECT IDENTIFIER");
            case "OCTET":
                reader.Expect("STRING");
                SkipConstraint(reader);
                return new MibSyntax("OCTET STRING");
            case "BIT":
                reader.Expect("STRING");
                return new MibSyntax("BIT STRING", ReadNamedNumbers(reader));
        }

        if (!head.IsIdentifier)
        {
            throw new FormatException($"unexpected '{head.Text}' in a type");
        }

        // INTEGER, BITS, Integer32, a textual-convention name, ... with optional named numbers and constraint.
        var enums = ReadNamedNumbers(reader);
        SkipConstraint(reader);
        return new MibSyntax(head.Text, enums);
    }

    private static Dictionary<long, string>? ReadNamedNumbers(Reader reader)
    {
        if (reader.Peek().Text != "{")
        {
            return null;
        }
        reader.Next();
        var values = new Dictionary<long, string>();
        while (!reader.AtEnd)
        {
            var token = reader.Next();
            if (token.Text == "}")
            {
                break;
            }
            if (token.IsIdentifier && reader.Peek().Text == "(")
            {
                reader.Next();
                var number = reader.Next();
                reader.Next(); // )
                if (number.IsNumber)
                {
                    values.TryAdd(number.Number, token.Text);
                }
            }
        }
        return values;
    }

    private static void SkipConstraint(Reader reader)
    {
        if (reader.Peek().Text == "(")
        {
            reader.SkipBalanced();
        }
    }

    private static int IndexOf(List<Token> tokens, int from, string text)
    {
        for (var index = from; index < tokens.Count; index++)
        {
            if (tokens[index].Text == text && tokens[index].Kind != TokenKind.String)
            {
                return index;
            }
        }
        return -1;
    }

    // ---- tokens -----------------------------------------------------------------------------------------

    private enum TokenKind { Word, String, Symbol }

    private readonly record struct Token(TokenKind Kind, string Text, int Line)
    {
        public bool IsIdentifier => Kind == TokenKind.Word && (char.IsLetter(Text[0]) || Text[0] == '_');
        public bool IsNumber => Kind == TokenKind.Word && long.TryParse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out _);
        public long Number => long.Parse(Text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        public string? StringValue => Kind == TokenKind.String ? Text : null;
    }

    private static List<Token> Tokenize(string text)
    {
        var tokens = new List<Token>();
        var line = 1;
        var index = 0;
        while (index < text.Length)
        {
            var c = text[index];
            if (c == '\n')
            {
                line++;
                index++;
                continue;
            }
            if (char.IsWhiteSpace(c))
            {
                index++;
                continue;
            }
            // Comment: "--" to the end of the line or to the next "--".
            if (c == '-' && index + 1 < text.Length && text[index + 1] == '-')
            {
                index += 2;
                while (index < text.Length && text[index] != '\n')
                {
                    if (text[index] == '-' && index + 1 < text.Length && text[index + 1] == '-')
                    {
                        index += 2;
                        break;
                    }
                    index++;
                }
                continue;
            }
            if (c == '"')
            {
                var builder = new StringBuilder();
                var startLine = line;
                index++;
                while (index < text.Length)
                {
                    if (text[index] == '"')
                    {
                        if (index + 1 < text.Length && text[index + 1] == '"')
                        {
                            builder.Append('"');
                            index += 2;
                            continue;
                        }
                        index++;
                        break;
                    }
                    if (text[index] == '\n')
                    {
                        line++;
                    }
                    builder.Append(text[index]);
                    index++;
                }
                tokens.Add(new Token(TokenKind.String, NormalizeDescription(builder.ToString()), startLine));
                continue;
            }
            // 'hex'H / 'bits'B
            if (c == '\'')
            {
                var end = text.IndexOf('\'', index + 1);
                if (end < 0)
                {
                    break;
                }
                var length = end - index + 1 + (end + 1 < text.Length && char.IsLetter(text[end + 1]) ? 1 : 0);
                tokens.Add(new Token(TokenKind.Word, "0", line));
                index += length;
                continue;
            }
            if (c == ':' && index + 2 < text.Length && text[index + 1] == ':' && text[index + 2] == '=')
            {
                tokens.Add(new Token(TokenKind.Symbol, "::=", line));
                index += 3;
                continue;
            }
            if (c == '.' && index + 1 < text.Length && text[index + 1] == '.')
            {
                tokens.Add(new Token(TokenKind.Symbol, "..", line));
                index += 2;
                continue;
            }
            if (char.IsLetterOrDigit(c) || c == '_' || (c == '-' && index + 1 < text.Length && char.IsDigit(text[index + 1])))
            {
                var start = index;
                index++;
                while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'
                    || (text[index] == '-' && !(index + 1 < text.Length && text[index + 1] == '-'))))
                {
                    index++;
                }
                tokens.Add(new Token(TokenKind.Word, text[start..index], line));
                continue;
            }
            tokens.Add(new Token(TokenKind.Symbol, c.ToString(), line));
            index++;
        }
        return tokens;
    }

    /// <summary>DESCRIPTION text is indented to the MIB's layout - collapse it to readable prose.</summary>
    private static string NormalizeDescription(string value)
    {
        var lines = value.Replace("\r", string.Empty).Split('\n').Select(part => part.Trim());
        var builder = new StringBuilder();
        foreach (var part in lines)
        {
            if (part.Length == 0)
            {
                if (builder.Length > 0 && !builder.ToString().EndsWith("\n\n", StringComparison.Ordinal))
                {
                    builder.Append("\n\n");
                }
                continue;
            }
            if (builder.Length > 0 && !builder.ToString().EndsWith('\n'))
            {
                builder.Append(' ');
            }
            builder.Append(part);
        }
        return builder.ToString().Trim();
    }

    private sealed class Reader(List<Token> tokens, int position)
    {
        private static readonly Token EndToken = new(TokenKind.Symbol, "\0", 0);

        public int Position { get; private set; } = position;

        public bool AtEnd => Position >= tokens.Count;

        public Token Peek(int ahead = 0) => Position + ahead < tokens.Count ? tokens[Position + ahead] : EndToken;

        public Token Next() => Position < tokens.Count ? tokens[Position++] : throw new FormatException("unexpected end of file");

        public void Back() => Position--;

        public void Expect(string text)
        {
            var token = Next();
            if (token.Text != text)
            {
                throw new FormatException($"expected '{text}' but found '{token.Text}'");
            }
        }

        public void SkipUntil(string text)
        {
            while (!AtEnd && Next().Text != text)
            {
            }
        }

        /// <summary>Skips one bracketed group starting at the current token ({...}, (...) or [...]).</summary>
        public void SkipBalanced()
        {
            var open = Next().Text;
            var close = open switch { "{" => "}", "(" => ")", "[" => "]", _ => throw new FormatException($"expected a bracket, found '{open}'") };
            var depth = 1;
            while (!AtEnd && depth > 0)
            {
                var token = Next();
                if (token.Kind == TokenKind.String)
                {
                    continue;
                }
                if (token.Text == open)
                {
                    depth++;
                }
                else if (token.Text == close)
                {
                    depth--;
                }
            }
        }

        /// <summary>After a failed assignment: skip to the next thing that looks like the start of one.</summary>
        public void Resync(int from)
        {
            Position = from;
            while (!AtEnd)
            {
                var token = Peek();
                if (token.Text == "END")
                {
                    return;
                }
                if (token.IsIdentifier && (Peek(1).Text == "::=" || MacroKinds.Contains(Peek(1).Text)
                    || (Peek(1).Text == "OBJECT" && Peek(2).Text == "IDENTIFIER")))
                {
                    return;
                }
                Position++;
            }
        }

        public string Describe(int at) => at < tokens.Count ? $"line {tokens[at].Line} near '{tokens[at].Text}'" : "end of file";
    }
}
