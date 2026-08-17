using System.Globalization;
using MemoryMcp.Core.Retrieval;

namespace MemoryMcp.Core.Schemas;

/// <summary>
/// What a note type's schema says about how it should rank, age and be linted (MEMP-253).
/// <para>Four INDEPENDENT facts, not one classification. An earlier attempt to collapse them into a single
/// class enum could not reproduce today's behaviour: <c>skill</c> and <c>memory_rule</c> are both durable
/// guidance yet only one expects tags, and <c>sprint</c> and <c>backlog_item</c> are both workflow yet differ
/// on both lint rules. They differ because they are genuinely different properties of a type, so each is
/// declared separately.</para>
/// </summary>
/// <param name="Class">Ranking class: <c>canonical</c> outranks <c>ordinary</c>, which outranks <c>episodic</c>.</param>
/// <param name="HalfLifeDays">Days until a note of this type is half as fresh.</param>
/// <param name="ExpectsTags">False for a type found by key or list rather than by facet; the no_tags lint skips it.</param>
/// <param name="ExpectsLinks">False for a type not expected to sit in the link graph; the orphan_note lint skips it.</param>
/// <param name="ClaimLike">True when the note asserts something that can quietly stop being true, so the
/// optional fact horizon applies to it.</param>
public sealed record TypeTraits(
    string Class, double HalfLifeDays, bool ExpectsTags, bool ExpectsLinks, bool ClaimLike)
{
    /// <summary>What a type gets when neither its schema nor the bridge says anything.</summary>
    public static readonly TypeTraits Default = new("ordinary", 90.0, true, true, false);
}

/// <summary>
/// Answers "how does this type behave in retrieval" from the type's own schema instead of from a literal in C#
/// (MEMP-253).
/// <para><b>Why the bridge still exists.</b> Thirteen of the twenty-five live types are agent-authored: their
/// schemas live only in the production database, so they cannot be annotated from this repository. Retiring the
/// tables outright would silently reset them — <c>reference</c>, the largest of them at 244 notes, would drop
/// from a ten-year half-life to ninety days and from canonical to ordinary. So the tables survive as an
/// explicitly named BRIDGE for exactly those types, an annotation always wins over it, and
/// <see cref="TypesOnBridge"/> reports how many are left so the debt shrinks visibly (MEMP-266) rather than
/// being forgotten.</para>
/// </summary>
public sealed class TypePolicy
{
    /// <summary>Ranking goodness by class: canonical knowledge outranks ordinary notes, which outrank logs.</summary>
    private const double CanonicalGoodness = 2d;
    private const double EphemeralGoodness = 0d;
    private const double OrdinaryGoodness = 1d;

    // The pre-MEMP-253 tables, kept ONLY for types whose schema cannot yet carry annotations. Every entry here
    // is a type still waiting on MEMP-266; the list is expected to shrink to nothing.
    private static readonly IReadOnlyDictionary<string, TypeTraits> Bridge =
        new Dictionary<string, TypeTraits>(StringComparer.Ordinal)
        {
            ["journal"] = new("episodic", 14.0, ExpectsTags: false, ExpectsLinks: false, ClaimLike: false),
            ["measurement"] = new("ordinary", 30.0, true, true, false),
            ["idea"] = new("ordinary", 120.0, true, true, false),
            ["menu"] = new("ordinary", 365.0, true, true, false),
            ["reference"] = new("canonical", 3650.0, true, true, ClaimLike: true),
            ["technique"] = new("canonical", 3650.0, true, true, false),
            ["equipment"] = new("canonical", 3650.0, true, true, false),
            ["seed_variety"] = new("canonical", 3650.0, true, true, false),
            ["spice"] = new("canonical", 3650.0, true, true, false),
        };

    private readonly SchemaRegistry? _schemas;
    private readonly Dictionary<string, TypeTraits> _cache = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    /// <summary>Creates the policy over a schema registry; null uses the bridge and defaults alone.</summary>
    /// <param name="schemas">Where type annotations are read from.</param>
    public TypePolicy(SchemaRegistry? schemas = null) => _schemas = schemas;

    /// <summary>
    /// The policy a caller gets when none was supplied: the annotations of the types this build SHIPS, plus the
    /// bridge for the agent-authored ones.
    /// <para>It reads the embedded schemas rather than starting empty, because those annotations are part of the
    /// binary — a caller that did not wire a registry has not opted out of the shipped types' own declarations,
    /// it simply has no agent-authored ones to add. Starting empty silently reverted every built-in type to the
    /// defaults, which is how this was found.</para>
    /// </summary>
    public static TypePolicy Bridged => Shipped.Value;

    private static readonly Lazy<TypePolicy> Shipped =
        new(() => new TypePolicy(SchemaRegistry.FromEmbeddedResources()), isThreadSafe: true);

    /// <summary>Traits for a type: its annotation, else the bridge, else the defaults.</summary>
    /// <param name="type">The note type.</param>
    public TypeTraits For(string? type)
    {
        if (string.IsNullOrEmpty(type))
        {
            return TypeTraits.Default;
        }

        lock (_gate)
        {
            if (_cache.TryGetValue(type, out var cached))
            {
                return cached;
            }

            var traits = Declared(type) ?? Bridge.GetValueOrDefault(type) ?? TypeTraits.Default;
            _cache[type] = traits;
            return traits;
        }
    }

    /// <summary>Per-type ranking goodness (MEMP-193), now read from the type rather than a set literal.</summary>
    /// <param name="type">The note type.</param>
    public double Goodness(string? type) => For(type).Class switch
    {
        "canonical" => CanonicalGoodness,
        "episodic" => EphemeralGoodness,
        _ => OrdinaryGoodness,
    };

    /// <summary>Half-life in days for the recency decay.</summary>
    /// <param name="type">The note type.</param>
    public double HalfLifeDays(string? type) => For(type).HalfLifeDays;

    /// <summary>Types still relying on the bridge rather than on their own schema — the MEMP-266 work list.</summary>
    public IReadOnlyList<string> TypesOnBridge =>
        [.. Bridge.Keys.Where(type => Declared(type) is null).OrderBy(type => type, StringComparer.Ordinal)];

    /// <summary>
    /// SQL ORDER BY body for the <c>recency</c> sort: freshest-relative-to-its-type first, ordering by age in
    /// days divided by the type's half-life. Monotonic in the decay score, so SQLite needs no exp().
    /// </summary>
    public string RecencyOrderByClause()
    {
        var cases = string.Join(" ", KnownTypes()
            .Select(type => $"WHEN {Quote(type)} THEN {Number(HalfLifeDays(type))}"));
        var halfLife = cases.Length == 0
            ? Number(TypeTraits.Default.HalfLifeDays)
            : $"CASE n.type {cases} ELSE {Number(TypeTraits.Default.HalfLifeDays)} END";
        return $"(julianday('now') - julianday(n.updated_utc)) / ({halfLife}) ASC";
    }

    /// <summary>
    /// A quoted SQL list of the types NOT expecting tags, for the <c>no_tags</c> lint. Returns null when every
    /// known type expects them, so the caller can omit the clause rather than emit an empty <c>NOT IN ()</c>.
    /// </summary>
    public string? TypesNotExpectingTags() => SqlList(traits => !traits.ExpectsTags);

    /// <summary>A quoted SQL list of the types not expected to sit in the link graph, for <c>orphan_note</c>.</summary>
    public string? TypesNotExpectingLinks() => SqlList(traits => !traits.ExpectsLinks);

    /// <summary>True when the optional fact horizon applies to this type (MEMP-240).</summary>
    /// <param name="type">The note type.</param>
    public bool IsClaimLike(string? type) => For(type).ClaimLike;

    private string? SqlList(Func<TypeTraits, bool> predicate)
    {
        var types = KnownTypes().Where(type => predicate(For(type))).Select(Quote).ToList();
        return types.Count == 0 ? null : string.Join(",", types);
    }

    // Every type either the registry or the bridge knows about. A type nobody declares takes the defaults,
    // which the SQL ELSE branch already covers.
    private IEnumerable<string> KnownTypes() =>
        (_schemas?.All.Select(definition => definition.Type) ?? [])
        .Concat(Bridge.Keys)
        .Distinct(StringComparer.Ordinal)
        .OrderBy(type => type, StringComparer.Ordinal);

    private TypeTraits? Declared(string type)
    {
        var schema = _schemas?.GetLatest(type);
        if (schema is null)
        {
            return null;
        }

        try
        {
            return RetrievalMapping.FromSchema(schema.Type, schema.Version, schema.Json)?.Traits;
        }
        catch (ArgumentException)
        {
            // A malformed annotation falls back rather than taking the server down; schema_upsert rejects it
            // loudly at the point an author can still fix it.
            return null;
        }
    }

    // Type names now reach SQL from an agent-writable registry rather than from constants, so they are quoted
    // rather than trusted. SQLite escapes a quote by doubling it; anything else passes through as data.
    private static string Quote(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string Number(double value) =>
        value.ToString("0.0###########", CultureInfo.InvariantCulture);
}
