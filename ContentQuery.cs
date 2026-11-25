using System.Text.RegularExpressions;

namespace FileInventory;

public enum ContentMatchMode { Smart, AllWords, AnyWord, ExactPhrase }

public sealed record ContentSearchOptions(ContentMatchMode Mode = ContentMatchMode.Smart, bool FuzzyMatching = true);

public sealed record ContentSearchResult(List<ContentSearchHit> Hits, string Summary,
    IReadOnlyList<string> FuzzyCorrections, bool UsedRelaxedMatch);

static class ContentQuery
{
    const string WordPattern = @"[\p{L}\p{N}][\p{L}\p{M}\p{N}]*";
    internal static readonly Regex Words = new(WordPattern, RegexOptions.Compiled);
    static readonly Regex Tokens = new("\"[^\"]*\"|\"|&&|\\|\\|?|[()]|(?<![\\p{L}\\p{M}\\p{N}])-|" +
        WordPattern + @"\*?", RegexOptions.Compiled);

    internal abstract record Node;
    internal sealed record Term(string Text, bool Prefix = false, bool Phrase = false) : Node;
    internal sealed record Exclude(Node Child) : Node;
    internal sealed record Join(Node Left, Node Right, bool Any = false) : Node;

    public static (Node? Root, bool Explicit) Parse(string input, ContentMatchMode mode)
    {
        // Bound FTS expression size and parser recursion for pasted input.
        if (input.Length > 4096) return (null, false);
        if (mode == ContentMatchMode.ExactPhrase)
        {
            string phrase = PhraseText(input);
            return (phrase.Length == 0 ? null : new Term(phrase, Phrase: true), false);
        }

        var tokens = Tokens.Matches(input).Select(m => m.Value).Take(97).ToArray();
        if (tokens.Length is 0 or > 96 || tokens.Count(t => Words.IsMatch(t)) > 32) return (null, false);
        int position = 0;
        bool explicitSyntax = false;
        string Current() => position < tokens.Length ? tokens[position] : "";
        string Operator() => Current().ToUpperInvariant();
        bool StartsOperand() => Operator() is not ("" or ")" or "AND" or "&&" or "OR" or "|" or "||");
        var root = ParseOr(0);
        return (position == tokens.Length ? root : null, explicitSyntax);

        Node? ParseOr(int depth)
        {
            var left = ParseAdjacent(depth);
            while (left is not null && Operator() is "OR" or "|" or "||")
            {
                explicitSyntax = true;
                position++;
                var right = ParseAdjacent(depth);
                left = right is null ? null : new Join(left, right, Any: true);
            }
            return left;
        }

        Node? ParseAdjacent(int depth)
        {
            var first = ParseAnd(depth);
            if (mode != ContentMatchMode.AnyWord || first is null || !StartsOperand()) return first;

            Node? included = null, excluded = null;
            Add(first);
            while (StartsOperand())
            {
                var next = ParseAnd(depth);
                if (next is null) return null;
                Add(next);
            }
            if (excluded is null) return included;
            var exclusion = new Exclude(excluded);
            return included is null ? exclusion : new Join(included, exclusion);

            void Add(Node node)
            {
                if (node is Exclude not)
                    excluded = excluded is null ? not.Child : new Join(excluded, not.Child, Any: true);
                else
                    included = included is null ? node : new Join(included, node, Any: true);
            }
        }

        Node? ParseAnd(int depth)
        {
            var left = ParseOperand(depth);
            while (left is not null)
            {
                bool explicitAnd = Operator() is "AND" or "&&";
                if (!explicitAnd && (mode == ContentMatchMode.AnyWord || !StartsOperand())) break;
                if (explicitAnd)
                {
                    explicitSyntax = true;
                    position++;
                }
                var right = ParseOperand(depth);
                if (right is null) return null;
                left = new Join(left, right);
            }
            return left;
        }

        Node? ParseOperand(int depth)
        {
            if (depth >= 12 || !StartsOperand()) return null;
            string token = Current();
            string op = Operator();
            position++;
            if (op is "NOT" or "-")
            {
                explicitSyntax |= op == "NOT";
                return ParseOperand(depth + 1) is { } child ? new Exclude(child) : null;
            }
            if (op == "(")
            {
                explicitSyntax = true;
                var group = ParseOr(depth + 1);
                if (Current() != ")") return null;
                position++;
                return group;
            }
            if (token[0] == '"')
            {
                if (token.Length == 1) return null;
                string phrase = PhraseText(token);
                return phrase.Length == 0 ? null : new Term(phrase, Phrase: true);
            }
            bool prefix = token.EndsWith('*');
            return new Term(prefix ? token[..^1] : token, prefix);
        }
    }

    static string PhraseText(string input) => string.Join(" ", Words.Matches(input).Select(m => m.Value));

    public static IEnumerable<Term> PositiveTerms(Node node, bool excluded = false) => node switch
    {
        Term term => excluded ? [] : [term],
        Exclude not => PositiveTerms(not.Child, !excluded),
        Join join => PositiveTerms(join.Left, excluded).Concat(PositiveTerms(join.Right, excluded)),
        _ => throw new InvalidOperationException()
    };

    public static IEnumerable<Node> Conjuncts(Node node) => node is Join { Any: false } join
        ? Conjuncts(join.Left).Concat(Conjuncts(join.Right)) : [node];

    public static string Quote(string text, bool prefix = false) =>
        "\"" + text.Replace("\"", "\"\"") + "\"" + (prefix ? "*" : "");

    // Carry complements separately so every NOT sent to FTS5 has a positive left operand.
    public static (string Match, bool Complement) Compile(Node node, Func<Term, bool, string> render,
        bool excluded = false)
    {
        if (node is Term term) return (render(term, excluded), false);
        if (node is Exclude not)
        {
            var child = Compile(not.Child, render, !excluded);
            return (child.Match, !child.Complement);
        }
        var join = (Join)node;
        var left = Compile(join.Left, render, excluded);
        var right = Compile(join.Right, render, excluded);
        if (left.Complement == right.Complement)
        {
            string op = join.Any != left.Complement ? "OR" : "AND";
            return ($"({left.Match} {op} {right.Match})", left.Complement);
        }
        if (left.Complement != join.Any) (left, right) = (right, left);
        return ($"({left.Match} NOT {right.Match})", join.Any);
    }
}
