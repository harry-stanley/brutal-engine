using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace BrutalEngine.Architecture.Tests;

/// <summary>
/// docs/03-architecture.md 3.21 and docs/05-testing-strategy.md: no static mutable fields of non-primitive types in
/// engine assemblies, so no static state can hold game types across a reload of the game assembly. A field is mutable
/// when it is neither const nor readonly. Allowed types are primitives, enums, and unmanaged and function pointers,
/// none of which can reference a managed game type. Static auto-properties and field-like events count, through
/// their backing fields. Compiler-generated types (lambda caches, closures, PrivateImplementationDetails) are skipped.
/// </summary>
internal static partial class StaticStateRule
{
    public const string Name = "no static mutable fields of non-primitive types in engine assemblies";

    private const BindingFlags StaticFields = BindingFlags.DeclaredOnly | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

    public static IReadOnlyList<string> Check(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        List<string> violations = [];
        foreach (Type type in types.Where(t => !IsCompilerGenerated(t)).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            foreach (FieldInfo field in type.GetFields(StaticFields).OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                if (field.IsLiteral || field.IsInitOnly || IsAllowedType(field.FieldType))
                {
                    continue;
                }

                violations.Add(
                    $"{TypeNames.Display(type)}.{Describe(field)}: static mutable field of type {TypeNames.Display(field.FieldType)}. Engine assemblies keep mutable state in instances owned by the host, so a game assembly can be reloaded (docs/03-architecture.md 3.21). Move it into an instance, or make it readonly if it never changes.");
            }
        }

        return violations;
    }

    /// <summary>Primitives, enums and unmanaged or function pointers can't hold a reference to a game type.</summary>
    public static bool IsAllowedType(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return type.IsPrimitive || type.IsEnum || type.IsPointer || type.IsFunctionPointer;
    }

    private static bool IsCompilerGenerated(Type type)
    {
        for (Type? current = type; current is not null; current = current.DeclaringType)
        {
            if (current.IsDefined(typeof(CompilerGeneratedAttribute), inherit: false) || current.Name.StartsWith('<'))
            {
                return true;
            }
        }

        return false;
    }

    private static string Describe(FieldInfo field)
    {
        Match backingField = BackingField().Match(field.Name);
        return backingField.Success ? $"{backingField.Groups[1].Value} (auto-property backing field)" : field.Name;
    }

    [GeneratedRegex(@"^<(.+)>k__BackingField$")]
    private static partial Regex BackingField();
}
