using System.Text.Json.Nodes;
using Json.Schema;

namespace MemoryMcp.Core.Schemas;

/// <summary>The result of validating a payload against a type's schema.</summary>
/// <param name="IsValid">True when the payload satisfies the schema.</param>
/// <param name="Errors">Human-readable validation errors (empty when valid).</param>
public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors);

/// <summary>Validates note payloads against the JSON Schema registered for their type.</summary>
public sealed class SchemaValidator
{
    private readonly SchemaRegistry _registry;

    /// <summary>Creates a validator backed by the given registry.</summary>
    /// <param name="registry">The schema registry to resolve type contracts from.</param>
    public SchemaValidator(SchemaRegistry registry) =>
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <summary>Validates <paramref name="payloadJson"/> against the latest schema for <paramref name="type"/>.</summary>
    /// <param name="type">The note type whose contract the payload must satisfy.</param>
    /// <param name="payloadJson">The payload as a JSON document.</param>
    /// <returns>A result describing validity and any errors. Unknown types are rejected.</returns>
    public ValidationResult Validate(string type, string payloadJson)
    {
        var definition = _registry.GetLatest(type);
        if (definition is null)
        {
            return new ValidationResult(false, new[] { $"No schema registered for type '{type}'." });
        }

        JsonNode? instance;
        try
        {
            instance = JsonNode.Parse(payloadJson);
        }
        catch (Exception ex)
        {
            return new ValidationResult(false, new[] { $"Payload is not valid JSON: {ex.Message}" });
        }

        var results = definition.Compiled.Evaluate(instance, new EvaluationOptions
        {
            OutputFormat = OutputFormat.List,
        });

        if (results.IsValid)
        {
            return new ValidationResult(true, Array.Empty<string>());
        }

        var errors = new List<string>();
        foreach (var detail in results.Details)
        {
            if (detail.Errors is null)
            {
                continue;
            }

            var location = detail.InstanceLocation.ToString();
            location = string.IsNullOrEmpty(location) ? "(root)" : location;
            foreach (var error in detail.Errors)
            {
                errors.Add($"{location}: {error.Value}");
            }
        }

        if (errors.Count == 0)
        {
            errors.Add("Payload failed schema validation.");
        }

        return new ValidationResult(false, errors);
    }
}
