using System;
using System.Collections.Generic;
using System.Linq;
using MOSAIC.Components.Basics;
using MOSAIC.Components.Interfaces;

namespace MOSAIC.Components.Factory;

/// <summary>Checks declared connections before any registered block constructor opens a device.</summary>
internal static class GraphValidation
{
    internal static Dictionary<string, string> Validate(
        IReadOnlyDictionary<string, JsonModel> models, IBlockFactory factory)
    {
        var errors = new Dictionary<string, string>(StringComparer.Ordinal);
        var types = models.ToDictionary(pair => pair.Key, pair => factory.GetBlockType(pair.Value.Type));
        foreach (var (key, model) in models)
        {
            var inputs = model.Inputs ?? Array.Empty<string>();
            if (inputs.Distinct(StringComparer.Ordinal).Count() != inputs.Count)
                errors.TryAdd(key, "Duplicate input connections are not allowed.");
            foreach (var input in inputs)
                if (!models.ContainsKey(input)) errors.TryAdd(key, $"Input '{input}' does not exist.");
            if (types[key] is not { } type) continue; // Custom factories are also checked after construction.
            var constraints = BlockConstraints.For(type);
            var reason = CheckInputs(inputs.Count, constraints, inputs.Where(types.ContainsKey).Select(input => types[input]));
            if (reason is not null) errors.TryAdd(key, reason);
        }

        var visited = new HashSet<string>(StringComparer.Ordinal);
        var path = new List<string>();
        void Visit(string key)
        {
            var cycle = path.IndexOf(key);
            if (cycle >= 0)
            {
                foreach (var member in path.Skip(cycle)) errors.TryAdd(member, "Feedback cycles are not supported; remove the cyclic connection.");
                return;
            }
            if (!visited.Add(key)) return;
            path.Add(key);
            foreach (var input in models[key].Inputs ?? Array.Empty<string>())
                if (models.ContainsKey(input)) Visit(input);
            path.RemoveAt(path.Count - 1);
        }
        foreach (var key in models.Keys) Visit(key);
        bool changed;
        do
        {
            changed = false;
            foreach (var (key, model) in models)
                foreach (var input in model.Inputs ?? Array.Empty<string>())
                    if (errors.ContainsKey(input)) changed |= errors.TryAdd(key, $"Input '{input}' has an invalid configuration.");
        } while (changed);
        return errors;
    }

    internal static string? CheckInputs(int count, BlockConstraints.Inputs constraints, IEnumerable<Type?> inputs)
    {
        if (count < constraints.Min || count > constraints.Max)
            return $"Requires {constraints.Min}–{constraints.Max} input connection(s); found {count}.";
        foreach (var type in inputs)
            if (type is not null && constraints.Allowable.Count > 0 && !constraints.Allowable.Contains(type.Name))
                return $"Input type '{type.Name}' is not allowed; expected {string.Join(", ", constraints.Allowable)}.";
        return null;
    }
}
