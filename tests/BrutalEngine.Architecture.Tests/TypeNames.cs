using System;
using System.Linq;

namespace BrutalEngine.Architecture.Tests;

/// <summary>Readable C#-style type names for failure messages (List&lt;Int32&gt;, not List`1[[System.Int32, ...]]).</summary>
internal static class TypeNames
{
    public static string Display(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (type.IsGenericParameter)
        {
            return type.Name;
        }

        if (type.HasElementType)
        {
            string element = Display(type.GetElementType()!);
            return type.IsArray ? $"{element}[{new string(',', type.GetArrayRank() - 1)}]" : type.IsPointer ? element + "*" : element + "&";
        }

        if (type.IsFunctionPointer)
        {
            return $"delegate*<{string.Join(", ", type.GetFunctionPointerParameterTypes().Append(type.GetFunctionPointerReturnType()).Select(Display))}>";
        }

        string name = type.Name;
        int arity = name.IndexOf('`', StringComparison.Ordinal);
        if (arity >= 0)
        {
            name = name[..arity];
        }

        Type[] arguments = type.GetGenericArguments();
        if (type.IsNested)
        {
            // A nested type repeats its declaring type's generic arguments first.
            int inherited = type.DeclaringType!.GetGenericArguments().Length;
            arguments = arguments[inherited..];
            name = Display(type.DeclaringType) + "." + name;
        }
        else if (type.Namespace is not null)
        {
            name = type.Namespace + "." + name;
        }

        return arguments.Length == 0 ? name : $"{name}<{string.Join(", ", arguments.Select(Display))}>";
    }
}
