using System.Globalization;

namespace Matmon.Core.Domain;

/// <summary>A resolved node of the OID tree.</summary>
public sealed record MibNode(
    string Oid,
    string Name,
    string Module,
    string Kind,
    MibSyntax? Syntax,
    IReadOnlyDictionary<long, string>? Enums,
    string? Units,
    string? Access,
    string? Description,
    string? Status = null);

/// <summary>What a walked OID is: the node it falls under plus the instance part (<c>.0</c>, a table index).</summary>
public sealed record MibTranslation(MibNode Node, string Instance)
{
    /// <summary><c>sysUpTime.0</c>, <c>ifInOctets.3</c> - the name a person reads.</summary>
    public string Name => Instance.Length == 0 ? Node.Name : $"{Node.Name}.{Instance}";

    /// <summary>True when the OID is a real MIB object (an OBJECT-TYPE or one of its instances). Otherwise only an
    /// arc above it is known (<c>enterprises.6574.1.2.0</c>) - useful context, but not a name to label a channel.</summary>
    public bool IsObject => Node.Kind == "OBJECT-TYPE";

    /// <summary>The value with its enum label when the syntax names it: <c>up (1)</c> instead of <c>1</c>.</summary>
    public string FormatValue(string? value)
    {
        if (Node.Enums is { Count: > 0 } enums
            && long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var number)
            && enums.TryGetValue(number, out var label))
        {
            return $"{label} ({number})";
        }
        return value ?? string.Empty;
    }
}

/// <summary>Per-module load result for the MIB page.</summary>
public sealed record MibModuleStatus(
    string Name,
    string Source,
    int NodeCount,
    int UnresolvedCount,
    IReadOnlyList<string> MissingImports,
    IReadOnlyList<string> Errors,
    string? LastUpdated);

/// <summary>
/// Turns parsed modules into the OID tree and answers "what is 1.3.6.1.2.1.2.2.1.10.3?". Built once per change
/// of the MIB set and then read-only, so lookups need no locking.
///
/// Symbol resolution follows the module's own definitions first, then its IMPORTS, then - leniently - any loaded
/// module that defines the name: vendor MIBs forget imports often enough that being strict would leave most of
/// their tree unnamed. A module referenced by an IMPORT that is not loaded is reported, because that is the
/// thing the user can fix (upload it).
/// </summary>
public sealed class MibRegistry
{
    private static readonly Dictionary<string, string> Roots = new(StringComparer.Ordinal)
    {
        ["ccitt"] = "0",
        ["iso"] = "1",
        ["joint-iso-ccitt"] = "2"
    };

    private readonly Dictionary<string, MibNode> _byOid = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MibNode> _byName = new(StringComparer.Ordinal);

    public static readonly MibRegistry Empty = new([]);

    public IReadOnlyList<MibModuleStatus> Modules { get; }

    public int NodeCount => _byOid.Count;

    /// <param name="modules">In load order; a later module of the same name replaces an earlier one (an upload
    /// overrides the shipped copy).</param>
    public MibRegistry(IEnumerable<(MibModule Module, string Source)> modules)
    {
        var byName = new Dictionary<string, (MibModule Module, string Source)>(StringComparer.Ordinal);
        foreach (var entry in modules)
        {
            byName[entry.Module.Name] = entry;
        }

        var resolver = new Resolver(byName.ToDictionary(pair => pair.Key, pair => pair.Value.Module, StringComparer.Ordinal));
        var statuses = new List<MibModuleStatus>();
        foreach (var (module, source) in byName.Values)
        {
            var unresolved = 0;
            foreach (var definition in module.Definitions)
            {
                var oid = resolver.ResolveOid(module.Name, definition.Name);
                if (oid is null)
                {
                    unresolved++;
                    continue;
                }
                var node = new MibNode(
                    oid, definition.Name, module.Name, definition.Kind, definition.Syntax,
                    definition.Syntax is null ? null : resolver.ResolveEnums(module.Name, definition.Syntax),
                    definition.Units, definition.Access, definition.Description, definition.Status);
                // First definition of an OID wins, except that a real OBJECT-TYPE beats a bare OBJECT IDENTIFIER and
                // an SMIv2 definition beats its SMIv1 predecessor (sysUpTime is in RFC1213-MIB and SNMPv2-MIB).
                if (!_byOid.TryGetValue(oid, out var existing)
                    || (existing.Kind == "OBJECT IDENTIFIER" && node.Kind != "OBJECT IDENTIFIER")
                    || (IsSmiV1Status(existing.Status) && node.Status is "current" or "deprecated" && node.Kind == existing.Kind))
                {
                    _byOid[oid] = node;
                }
                _byName.TryAdd(definition.Name, node);
            }

            var missing = module.Imports.Values.Distinct(StringComparer.Ordinal)
                .Where(name => !byName.ContainsKey(name) && !IsSmiPseudoModule(name))
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();
            statuses.Add(new MibModuleStatus(module.Name, source, module.Definitions.Count - unresolved, unresolved, missing, module.Errors, module.LastUpdated));
        }

        Modules = statuses.OrderBy(status => status.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The node for a walked OID (longest known prefix), or null when no loaded MIB covers it.</summary>
    public MibTranslation? Translate(string? oid)
    {
        var normalized = (oid ?? string.Empty).Trim().TrimStart('.');
        if (normalized.Length == 0)
        {
            return null;
        }

        var candidate = normalized;
        while (true)
        {
            if (_byOid.TryGetValue(candidate, out var node))
            {
                return new MibTranslation(node, candidate.Length == normalized.Length ? string.Empty : normalized[(candidate.Length + 1)..]);
            }
            var dot = candidate.LastIndexOf('.');
            if (dot < 0)
            {
                return null;
            }
            candidate = candidate[..dot];
        }
    }

    /// <summary>A symbolic name to its node ("sysDescr" -> 1.3.6.1.2.1.1.1).</summary>
    public MibNode? Find(string name) => _byName.GetValueOrDefault(name.Trim());

    private static bool IsSmiV1Status(string? status) => status is "mandatory" or "optional";

    // These only define macros (RFC-1212 is SMIv1's OBJECT-TYPE macro, which nobody ships as a file); importing
    // from them is not a missing dependency.
    private static bool IsSmiPseudoModule(string name) => name is "ASN1" or "ASN.1" or "RFC-1212" or "RFC1212";

    private sealed class Resolver(Dictionary<string, MibModule> modules)
    {
        private readonly Dictionary<(string Module, string Symbol), string?> _oids = new();
        private readonly HashSet<(string Module, string Symbol)> _resolving = [];
        private readonly Dictionary<string, (MibModule Module, MibDefinition Definition)> _global = BuildGlobal(modules);

        private static Dictionary<string, (MibModule, MibDefinition)> BuildGlobal(Dictionary<string, MibModule> modules)
        {
            var global = new Dictionary<string, (MibModule, MibDefinition)>(StringComparer.Ordinal);
            foreach (var module in modules.Values)
            {
                foreach (var definition in module.Definitions)
                {
                    global.TryAdd(definition.Name, (module, definition));
                }
            }
            return global;
        }

        public string? ResolveOid(string moduleName, string symbol)
        {
            if (Roots.TryGetValue(symbol, out var root))
            {
                return root;
            }
            var key = (moduleName, symbol);
            if (_oids.TryGetValue(key, out var cached))
            {
                return cached;
            }
            if (!_resolving.Add(key))
            {
                return null; // a cycle in a broken MIB
            }

            string? result = null;
            var (owner, definition) = Locate(moduleName, symbol);
            if (definition is not null && definition.Oid.Count > 0)
            {
                var first = definition.Oid[0];
                var prefix = first.Name is { } parentName && first.Number is null
                    ? ResolveOid(owner!.Name, parentName)
                    : first.Number?.ToString(CultureInfo.InvariantCulture);
                if (prefix is not null)
                {
                    var parts = new List<string> { prefix };
                    foreach (var component in definition.Oid.Skip(1))
                    {
                        if (component.Number is { } number)
                        {
                            parts.Add(number.ToString(CultureInfo.InvariantCulture));
                        }
                        else if (component.Name is { } name && ResolveOid(owner!.Name, name) is { } resolved && resolved.StartsWith(string.Join('.', parts) + ".", StringComparison.Ordinal))
                        {
                            parts = [resolved]; // { iso org dod } style - each name refines the path
                        }
                        else
                        {
                            parts.Clear();
                            break;
                        }
                    }
                    result = parts.Count > 0 ? string.Join('.', parts) : null;
                }
            }

            _resolving.Remove(key);
            _oids[key] = result;
            return result;
        }

        private (MibModule? Module, MibDefinition? Definition) Locate(string moduleName, string symbol)
        {
            if (modules.TryGetValue(moduleName, out var module))
            {
                var own = module.Definitions.FirstOrDefault(definition => definition.Name == symbol);
                if (own is not null)
                {
                    return (module, own);
                }
                if (module.Imports.TryGetValue(symbol, out var from) && modules.TryGetValue(from, out var imported))
                {
                    var found = imported.Definitions.FirstOrDefault(definition => definition.Name == symbol);
                    if (found is not null)
                    {
                        return (imported, found);
                    }
                }
            }
            return _global.TryGetValue(symbol, out var global) ? (global.Module, global.Definition) : (null, null);
        }

        /// <summary>Enums of a syntax, following textual conventions (TruthValue -> true(1)/false(2)).</summary>
        public IReadOnlyDictionary<long, string>? ResolveEnums(string moduleName, MibSyntax syntax)
        {
            var current = syntax;
            var module = moduleName;
            for (var depth = 0; depth < 8; depth++)
            {
                if (current.Enums is { Count: > 0 } enums)
                {
                    return enums;
                }
                var type = FindType(module, current.BaseType);
                if (type is null)
                {
                    return null;
                }
                module = type.Value.Module;
                current = type.Value.Type.Syntax;
            }
            return null;
        }

        private (string Module, MibTypeDefinition Type)? FindType(string moduleName, string name)
        {
            if (modules.TryGetValue(moduleName, out var module))
            {
                if (module.Types.TryGetValue(name, out var own))
                {
                    return (module.Name, own);
                }
                if (module.Imports.TryGetValue(name, out var from) && modules.TryGetValue(from, out var imported) && imported.Types.TryGetValue(name, out var importedType))
                {
                    return (imported.Name, importedType);
                }
            }
            foreach (var candidate in modules.Values)
            {
                if (candidate.Types.TryGetValue(name, out var type))
                {
                    return (candidate.Name, type);
                }
            }
            return null;
        }
    }
}
