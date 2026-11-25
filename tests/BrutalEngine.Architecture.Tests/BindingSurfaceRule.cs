using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace BrutalEngine.Architecture.Tests;

/// <summary>
/// docs/03-architecture.md 3.1, "no backend type leaks upward": outside the L2 backends, no public surface exposes a
/// type from a BRUTAL native binding namespace. The public surface is every public type (and protected nested type of
/// an unsealed public type) with its base type, interfaces, generic constraints, and the types in the signatures of
/// its public and protected fields, properties, indexers, events, constructors, methods and operators, including
/// array, pointer, by-ref, function-pointer and generic-argument positions.
/// </summary>
internal static class BindingSurfaceRule
{
    public const string Name = "no Brutal.*Api binding types in a public surface outside the backends";

    private const BindingFlags DeclaredMembers =
        BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

    /// <summary>
    /// Binding namespaces that don't follow the Brutal.&lt;Name&gt;Api pattern. Every other native binding in BRUTAL
    /// does: Box2DApi, FmodApi, GlfwApi, GliApi, GltfApi, ImGuiApi, ImPlotApi, JoltApi, KtxApi, NativeFileDialogApi,
    /// NotCursesApi, OpenAlApi, OpenGlApi, RakNetApi, RenderDocApi, SdlApi, ShaderCApi, SlangApi, StbApi, TextureApi
    /// and VulkanApi, plus sub-namespaces such as Brutal.VulkanApi.Vma and Brutal.SdlApi.Ttf (from a search for
    /// "namespace Brutal." across the BRUTAL sources).
    /// </summary>
    private static readonly string[] _unsuffixedBindingNamespaces = ["Brutal.MeshOptimizer"];

    /// <summary>True for Brutal.&lt;Name&gt;Api, its sub-namespaces, and the unsuffixed binding namespaces.</summary>
    public static bool IsBindingNamespace(string? ns)
    {
        if (ns is null)
        {
            return false;
        }

        string[] parts = ns.Split('.');
        if (parts.Length >= 2 && parts[0] == "Brutal" && parts[1].Length > "Api".Length && parts[1].EndsWith("Api", StringComparison.Ordinal))
        {
            return true;
        }

        return _unsuffixedBindingNamespaces.Any(b => ns == b || ns.StartsWith(b + ".", StringComparison.Ordinal));
    }

    public static IReadOnlyList<string> Check(IEnumerable<Type> types)
    {
        ArgumentNullException.ThrowIfNull(types);

        List<string> violations = [];
        foreach (Type type in types.Where(IsExposed).OrderBy(t => t.FullName, StringComparer.Ordinal))
        {
            CheckType(type, violations);
        }

        return violations;
    }

    private static void CheckType(Type type, List<string> violations)
    {
        string owner = Format(type);

        Inspect(type.BaseType, owner, "base type", violations);
        foreach (Type implemented in type.GetInterfaces())
        {
            Inspect(implemented, owner, "interface", violations);
        }

        if (type.IsGenericTypeDefinition)
        {
            InspectConstraints(type.GetGenericArguments(), owner, violations);
        }

        HashSet<MethodInfo> accessors = [];

        foreach (PropertyInfo property in type.GetProperties(DeclaredMembers))
        {
            accessors.UnionWith(property.GetAccessors(nonPublic: true));
            if (IsExposed(property.GetMethod, type) || IsExposed(property.SetMethod, type))
            {
                string member = $"{owner}.{property.Name}";
                Inspect(property.PropertyType, member, "property type", violations);
                foreach (ParameterInfo parameter in property.GetIndexParameters())
                {
                    Inspect(parameter.ParameterType, member, $"indexer parameter '{parameter.Name}'", violations);
                }
            }
        }

        foreach (EventInfo @event in type.GetEvents(DeclaredMembers))
        {
            accessors.UnionWith(new[] { @event.AddMethod, @event.RemoveMethod, @event.RaiseMethod }.OfType<MethodInfo>());
            if (IsExposed(@event.AddMethod, type) || IsExposed(@event.RemoveMethod, type))
            {
                Inspect(@event.EventHandlerType, $"{owner}.{@event.Name}", "event type", violations);
            }
        }

        foreach (FieldInfo field in type.GetFields(DeclaredMembers))
        {
            if (IsExposed(field.IsPublic, field.IsFamily || field.IsFamilyOrAssembly, type))
            {
                Inspect(field.FieldType, $"{owner}.{field.Name}", "field type", violations);
            }
        }

        foreach (ConstructorInfo constructor in type.GetConstructors(DeclaredMembers))
        {
            if (IsExposed(constructor, type))
            {
                InspectParameters(constructor, $"{owner}.{constructor.Name}", violations);
            }
        }

        foreach (MethodInfo method in type.GetMethods(DeclaredMembers))
        {
            if (accessors.Contains(method) || !IsExposed(method, type))
            {
                continue;
            }

            string member = $"{owner}.{method.Name}";
            Inspect(method.ReturnType, member, "return type", violations);
            InspectParameters(method, member, violations);
            if (method.IsGenericMethodDefinition)
            {
                InspectConstraints(method.GetGenericArguments(), member, violations);
            }
        }
    }

    private static void InspectParameters(MethodBase method, string member, List<string> violations)
    {
        foreach (ParameterInfo parameter in method.GetParameters())
        {
            Inspect(parameter.ParameterType, member, $"parameter '{parameter.Name}'", violations);
        }
    }

    private static void InspectConstraints(Type[] genericParameters, string member, List<string> violations)
    {
        foreach (Type parameter in genericParameters)
        {
            foreach (Type constraint in parameter.GetGenericParameterConstraints())
            {
                Inspect(constraint, member, $"constraint on '{parameter.Name}'", violations);
            }
        }
    }

    private static void Inspect(Type? type, string member, string role, List<string> violations)
    {
        List<Type> leaked = [];
        Collect(type, [], leaked);
        foreach (Type binding in leaked)
        {
            violations.Add(
                $"{member} ({role}) exposes {Format(binding)} from binding namespace {binding.Namespace}. Only L2 backends may expose binding types: wrap it in an engine type or make the member internal (docs/03-architecture.md 3.1).");
        }
    }

    /// <summary>Collects the binding types that appear anywhere in <paramref name="type"/>.</summary>
    private static void Collect(Type? type, HashSet<Type> seen, List<Type> leaked)
    {
        if (type is null || !seen.Add(type))
        {
            return;
        }

        if (type.HasElementType)
        {
            // Arrays, pointers and by-ref parameters.
            Collect(type.GetElementType(), seen, leaked);
        }
        else if (type.IsFunctionPointer)
        {
            Collect(type.GetFunctionPointerReturnType(), seen, leaked);
            foreach (Type parameter in type.GetFunctionPointerParameterTypes())
            {
                Collect(parameter, seen, leaked);
            }
        }
        else if (type.IsConstructedGenericType)
        {
            Collect(type.GetGenericTypeDefinition(), seen, leaked);
            foreach (Type argument in type.GenericTypeArguments)
            {
                Collect(argument, seen, leaked);
            }
        }
        else if (!type.IsGenericParameter && IsBindingNamespace(type.Namespace))
        {
            // Generic parameters are skipped: their constraints are checked once, where they are declared.
            leaked.Add(type);
        }
    }

    private static bool IsExposed(Type type) =>
        type.IsNested
            ? IsExposed(type.IsNestedPublic, type.IsNestedFamily || type.IsNestedFamORAssem, type.DeclaringType!) && IsExposed(type.DeclaringType!)
            : type.IsPublic;

    private static bool IsExposed(MethodBase? method, Type declaringType) =>
        method is not null && IsExposed(method.IsPublic, method.IsFamily || method.IsFamilyOrAssembly, declaringType);

    /// <summary>Public members are exposed. Protected ones are too, unless no other assembly can derive from the type.</summary>
    private static bool IsExposed(bool isPublic, bool isProtected, Type declaringType) => isPublic || (isProtected && !declaringType.IsSealed);

    private static string Format(Type type) => (type.FullName ?? type.Name).Replace('+', '.');
}
