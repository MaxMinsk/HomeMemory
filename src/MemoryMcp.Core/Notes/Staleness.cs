using System.Globalization;
using System.Text.Json;

namespace MemoryMcp.Core.Notes;

/// <summary>
/// Why a note's content may no longer be true (MEMP-240).
/// </summary>
/// <param name="Reason">
/// <c>expired</c> — the note declared a <c>valid_until</c> that has passed; <c>past_window</c> — the note has not
/// been re-verified within its staleness window.
/// </param>
/// <param name="AgeDays">Days since the expiry date, or since the note was last stated to be true.</param>
/// <param name="WindowDays">The window that was exceeded; null for an expiry, which needs no window.</param>
public sealed record StalenessHint(string Reason, int AgeDays, int? WindowDays);

/// <summary>
/// Configuration for how aggressively facts are treated as aging (MEMP-240).
/// </summary>
/// <param name="TypePolicy">Supplies which types carry a claim; null uses the bridged default.</param>
/// <param name="FactHorizonDays">
/// A default staleness window, in days, for fact-like notes that declare no window of their own. <b>0 disables
/// it</b>, which is the default: without an opt-in every long-lived reference note in the corpus would be flagged
/// the moment it passed the horizon, and a hint that fires on almost every hit is one an agent learns to ignore.
/// Notes that declare <c>valid_until</c> or <c>stale_after_days</c> are evaluated regardless of this setting.
/// </param>
public sealed record StalenessOptions(int FactHorizonDays = 0, Schemas.TypePolicy? TypePolicy = null)
{
    /// <summary>Which types carry a claim that can go stale — the type's own declaration (MEMP-253).</summary>
    public Schemas.TypePolicy Types => TypePolicy ?? Schemas.TypePolicy.Bridged;

    /// <summary>Reads <c>MEMORY_STALENESS_FACT_DAYS</c>; disabled unless set to a positive integer.</summary>
    public static StalenessOptions FromEnvironment()
    {
        var raw = Environment.GetEnvironmentVariable("MEMORY_STALENESS_FACT_DAYS");
        return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) && days > 0
            ? new StalenessOptions(days)
            : new StalenessOptions();
    }
}

/// <summary>
/// Decides whether a note's content has aged out of being trustworthy (MEMP-240). Facts get contradicted and
/// superseded, but nothing in the store noticed: recall served a note whose truth had expired exactly as
/// confidently as one written yesterday. This is deliberately a HINT and never a filter — the server reports what
/// the note itself claims about its own validity, and the agent decides what to do with it.
/// <para>Two conventions are honoured, both opt-in per note and both already present in the built-in schemas:
/// <c>valid_to</c> (alias <c>valid_until</c>), an explicit date after which the note is no longer claimed to hold,
/// and <c>stale_after_days</c>, a re-verification window measured from the note's own <c>as_of</c>/<c>updated</c>/
/// <c>last_verified_at</c> timestamp, falling back to the envelope's <c>updated_utc</c>. A <c>project_state</c>
/// ages against a default window because tracking current state is the whole point of the type.</para>
/// </summary>
public static class Staleness
{
    /// <summary>A project_state has no window of its own, but stale project state is actively misleading.</summary>
    public const int DefaultProjectStateDays = 14;



    private const DateTimeStyles Styles = DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal;

    /// <summary>
    /// Evaluates a note's temporal validity, or null when it makes no claim that has aged.
    /// </summary>
    /// <param name="type">The note type.</param>
    /// <param name="payloadJson">The note's payload, read for the validity conventions.</param>
    /// <param name="updatedUtc">The envelope's last-updated timestamp, used when the payload states no date.</param>
    /// <param name="now">The current time.</param>
    /// <param name="options">Staleness configuration; defaults to the fact horizon being off.</param>
    /// <param name="createdUtc">
    /// When the note was created. Used as the LAST-RESORT baseline for the re-verification window, ahead of
    /// <paramref name="updatedUtc"/> (MEMP-246): for a note that has never stated when it was last checked, the
    /// honest answer to "how long since anyone verified this?" is "since it was written". Measuring from the
    /// last edit instead would let a retag or a typo fix silently reset the clock on a claim nobody re-read.
    /// </param>
    public static StalenessHint? Evaluate(
        string type, string? payloadJson, string? updatedUtc, DateTimeOffset now, StalenessOptions? options = null,
        string? createdUtc = null)
    {
        // `valid_to` is the name the built-in fact schema already uses; `valid_until` is accepted as an alias,
        // since agent-authored types in the live corpus reach for that spelling instead.
        var expiry = Date(payloadJson, "valid_to") ?? Date(payloadJson, "valid_until");
        if (expiry is { } expired && now > expired)
        {
            return new StalenessHint("expired", (int)(now - expired).TotalDays, null);
        }

        if (Window(type, payloadJson, options ?? new StalenessOptions()) is not int window)
        {
            return null;
        }

        // The note's own statement of when it was last true beats the envelope timestamp: a retag or a typo fix
        // bumps updated_utc without re-checking anything the note asserts.
        var since = Date(payloadJson, "as_of") ?? Date(payloadJson, "updated") ?? Date(payloadJson, "last_verified_at")
            ?? Parse(createdUtc) ?? Parse(updatedUtc);
        if (since is not { } reference)
        {
            return null;
        }

        var age = (int)(now - reference).TotalDays;
        return age > window ? new StalenessHint("past_window", age, window) : null;
    }

    // The re-verification window in days, or null when the note is not subject to one.
    private static int? Window(string type, string? payloadJson, StalenessOptions options)
    {
        if (Element(payloadJson, "stale_after_days") is { ValueKind: JsonValueKind.Number } declared
            && declared.TryGetInt32(out var days) && days > 0)
        {
            return days;
        }

        if (string.Equals(type, "project_state", StringComparison.Ordinal))
        {
            return DefaultProjectStateDays;
        }

        return options.FactHorizonDays > 0 && options.Types.IsClaimLike(type)
            ? options.FactHorizonDays
            : null;
    }

    /// <summary>Reads a payload property, or null when the payload is absent, malformed or lacks it.</summary>
    /// <param name="payloadJson">The note payload.</param>
    /// <param name="name">The property name.</param>
    public static JsonElement? Element(string? payloadJson, string name)
    {
        if (string.IsNullOrEmpty(payloadJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value)
                    ? value.Clone()
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static DateTimeOffset? Date(string? payloadJson, string name) =>
        Element(payloadJson, name) is { ValueKind: JsonValueKind.String } element ? Parse(element.GetString()) : null;

    private static DateTimeOffset? Parse(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, Styles, out var when) ? when : null;
}
