using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace BrutalEngine.Graphics.Vulkan.Tests.Instances;

/// <summary>
/// Finds, in one C# source file's text, the ways the backend could make one of Vulkan's global commands without
/// <c>GlobalCommands</c>. The file is scanned whole, so a call may span lines; comments are skipped, and string and
/// char literals are told apart from code. It flags, each with its 1-based line:
/// <list type="bullet">
/// <item>the raw bindings' names as identifiers, qualified or not (<c>vkCreateInstance</c>,
/// <c>vkEnumerateInstanceExtensionProperties</c>, <c>vkEnumerateInstanceLayerProperties</c>,
/// <c>vkEnumerateInstanceVersion</c>: <c>Brutal.Vulkan/Generated/PInvoke/VulkanApi/VulkanApi.gen.cs:26</c>, <c>:51</c>,
/// <c>:74</c>, <c>:95</c>), so calls through an alias, a method group and <c>&amp;</c> function pointers are caught;</item>
/// <item>BRUTAL's public wrappers of them: <c>EnumerateInstanceExtensionProperties_Count</c>/<c>_Write</c>,
/// <c>EnumerateInstanceLayerProperties_Count</c>/<c>_Write</c>, <c>EnumerateInstanceVersion</c>
/// (<c>Brutal.Vulkan/Generated/PInvoke/VulkanApi/VulkanApiEx.gen.cs:151</c>, <c>:161</c>, <c>:231</c>, <c>:241</c>,
/// <c>:264</c>), and <c>Vulkan.CreateInstance</c> (<c>Brutal.Vulkan/Manual/Partials/Vulkan.cs:10</c>; BRUTAL's
/// <c>Instance</c> constructor is internal, <c>Brutal.Vulkan/Manual/Classes/Instance.cs:22</c>);</item>
/// <item>a <c>using static</c> or alias <c>using</c> of BRUTAL's <c>Vulkan</c> class or its <c>PInvoke</c> class, which
/// would let a call name neither;</item>
/// <item>a call through BRUTAL's bound <c>Vulkan.GetInstanceProcAddr</c>, and a string literal that is exactly a
/// global command's name: a lookup of the command by name.</item>
/// </list>
/// It doesn't see a name built at run time, a function pointer obtained some other way (an export address cast to a
/// <c>delegate*</c>), reflection, or Unicode escapes inside identifiers.
/// </summary>
internal static partial class GlobalCommandScan
{
    /// <summary>Returns <c>"line: what"</c> for each match, in source order; empty when the text makes no global command.</summary>
    /// <param name="source">A C# file's text.</param>
    /// <returns>The matches.</returns>
    /// <exception cref="FormatException">The text ends inside a comment, literal or interpolation.</exception>
    public static List<string> FindBypasses(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        List<(int Offset, string Literal)> literals = [];
        string code = CodeOf(source, literals);
        List<(int Offset, string What)> found = [];
        foreach (Regex rule in (Regex[])[CommandName(), CreateInstanceWrapper(), BindingUsing(), ProcAddrCall()])
        {
            foreach (Match match in rule.Matches(code))
            {
                found.Add((match.Index, Whitespace().Replace(match.Value, " ")));
            }
        }

        foreach ((int offset, string literal) in literals)
        {
            if (CommandLiteral().IsMatch(literal))
            {
                found.Add((offset, '"' + literal + '"'));
            }
        }

        found.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        List<string> report = [];
        foreach ((int offset, string what) in found)
        {
            report.Add($"{LineOf(source, offset)}: {what}");
        }

        return report;
    }

    // The source with every comment and literal blanked to spaces (line breaks kept, so offsets and lines still hold),
    // leaving the code, including the code inside interpolation holes. Each literal's text goes to literals.
    private static string CodeOf(string source, List<(int Offset, string Literal)> literals)
    {
        char[] code = source.ToCharArray();
        int i = 0;
        ScanCode(source, code, ref i, literals, inHole: false);
        return new string(code);
    }

    // Code up to the end, or, in an interpolation hole, up to the '}' (or format ':') that ends it at depth 0.
    private static void ScanCode(string s, char[] code, ref int i, List<(int Offset, string Literal)> literals, bool inHole)
    {
        int depth = 0;
        while (i < s.Length)
        {
            char c = s[i];
            if ((c == '/' && At(s, i + 1) == '/') || (c == '#' && !inHole && StartsLine(s, i)))
            {
                // A comment, or a preprocessor line, whose text (#region It's ...) isn't code.
                int end = s.IndexOf('\n', i);
                end = end < 0 ? s.Length : end;
                Blank(code, i, end);
                i = end;
            }
            else if (c == '/' && At(s, i + 1) == '*')
            {
                int end = s.IndexOf("*/", i + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    throw new FormatException("unterminated block comment at offset " + i);
                }

                Blank(code, i, end + 2);
                i = end + 2;
            }
            else if (c == '\'')
            {
                ScanChar(s, code, ref i);
            }
            else if (c == '"' || (c is '$' or '@' && StartsString(s, i)))
            {
                ScanString(s, code, ref i, literals);
            }
            else if (inHole && c is '(' or '[' or '{')
            {
                depth++;
                i++;
            }
            else if (inHole && c is ')' or ']' or '}' && depth > 0)
            {
                depth--;
                i++;
            }
            else if (inHole && depth == 0 && (c == '}' || (c == ':' && At(s, i + 1) != ':' && At(s, i - 1) != ':')))
            {
                return;
            }
            else
            {
                i++;
            }
        }

        if (inHole)
        {
            throw new FormatException("unterminated interpolation hole");
        }
    }

    private static void ScanChar(string s, char[] code, ref int i)
    {
        int start = i++;
        while (i < s.Length && s[i] != '\'' && s[i] != '\n')
        {
            i += s[i] == '\\' ? 2 : 1;
        }

        if (i >= s.Length || s[i] != '\'')
        {
            throw new FormatException("unterminated char literal at offset " + start);
        }

        Blank(code, start, ++i);
    }

    // A string literal of any kind, from its prefix ($, @) to its closing quotes.
    private static void ScanString(string s, char[] code, ref int i, List<(int Offset, string Literal)> literals)
    {
        int start = i;
        int dollars = 0;
        bool verbatim = false;
        for (; s[i] != '"'; i++)
        {
            dollars += s[i] == '$' ? 1 : 0;
            verbatim |= s[i] == '@';
        }

        int quotes = Run(s, i, '"');
        bool raw = !verbatim && quotes >= 3;
        if (!raw && !verbatim && quotes == 2)
        {
            // "" is an empty literal.
            i += 2;
            Blank(code, start, i);
            return;
        }

        int delimiter = raw ? quotes : 1;
        i += delimiter;
        bool holes = false;
        int contentStart = i;
        int blankFrom = start;
        while (true)
        {
            if (i >= s.Length || (!raw && !verbatim && s[i] == '\n'))
            {
                throw new FormatException("unterminated string literal at offset " + start);
            }

            char c = s[i];
            if (!raw && !verbatim && c == '\\')
            {
                i += 2;
            }
            else if (c == '"' && verbatim && At(s, i + 1) == '"')
            {
                i += 2;
            }
            else if (c == '"' && Run(s, i, '"') >= delimiter)
            {
                literals.Add((contentStart, s[contentStart..i].Trim()));
                i += delimiter;
                break;
            }
            else if (c == '{' && dollars > 0 && (raw ? Run(s, i, '{') >= dollars : At(s, i + 1) != '{'))
            {
                // A hole: blank the literal text before it and keep its code, then blank its format and closing braces.
                i += raw ? Run(s, i, '{') : 1;
                Blank(code, blankFrom, i);
                holes = true;
                ScanCode(s, code, ref i, literals, inHole: true);
                blankFrom = i;
                while (i < s.Length && s[i] != '}')
                {
                    i++;
                }

                i += raw ? dollars : 1;
            }
            else
            {
                // {{ and }} are literal braces in a non-raw interpolated string.
                i += c is '{' or '}' && !raw && dollars > 0 && At(s, i + 1) == c ? 2 : 1;
            }
        }

        Blank(code, blankFrom, i);
        if (holes)
        {
            // Only a literal without holes is a constant name a lookup could pass.
            literals.RemoveAt(literals.Count - 1);
        }
    }

    // Whether the $ and @ at i prefix a string literal rather than a verbatim identifier.
    private static bool StartsString(string s, int i)
    {
        while (At(s, i) is '$' or '@')
        {
            i++;
        }

        return At(s, i) == '"';
    }

    // Whether only spaces and tabs come before i on its line.
    private static bool StartsLine(string s, int i)
    {
        while (--i >= 0 && s[i] is ' ' or '\t')
        {
        }

        return i < 0 || s[i] == '\n';
    }

    private static int Run(string s, int i, char c)
    {
        int n = 0;
        while (At(s, i + n) == c)
        {
            n++;
        }

        return n;
    }

    private static char At(string s, int i) => i >= 0 && i < s.Length ? s[i] : '\0';

    private static void Blank(char[] code, int from, int to)
    {
        for (int i = from; i < to && i < code.Length; i++)
        {
            if (code[i] is not '\n' and not '\r')
            {
                code[i] = ' ';
            }
        }
    }

    private static int LineOf(string source, int offset)
    {
        int line = 1;
        for (int i = 0; i < offset; i++)
        {
            line += source[i] == '\n' ? 1 : 0;
        }

        return line;
    }

    // The raw bindings and BRUTAL's enumeration wrappers, as whole identifiers (a leading @ included).
    [GeneratedRegex(@"(?<![\w@])@?(vkCreateInstance|vkEnumerateInstanceExtensionProperties|vkEnumerateInstanceLayerProperties|vkEnumerateInstanceVersion|EnumerateInstanceExtensionProperties_Count|EnumerateInstanceExtensionProperties_Write|EnumerateInstanceLayerProperties_Count|EnumerateInstanceLayerProperties_Write|EnumerateInstanceVersion)(?!\w)")]
    private static partial Regex CommandName();

    // BRUTAL's Vulkan.CreateInstance, however it is qualified or spaced; the gate's own GlobalCommands.CreateInstance
    // and the engine's VulkanCall.CreateInstance don't match.
    [GeneratedRegex(@"(?<![\w@])@?Vulkan\s*\.\s*@?CreateInstance(?!\w)")]
    private static partial Regex CreateInstanceWrapper();

    // using static, or an alias, of Brutal.VulkanApi.Vulkan or Vulkan.PInvoke (global using included).
    [GeneratedRegex(@"\busing\s+(static\s+|@?\w+\s*=\s*)(global\s*::\s*)?(Brutal\s*\.\s*VulkanApi\s*\.\s*)?@?Vulkan(\s*\.\s*@?PInvoke)?\s*;")]
    private static partial Regex BindingUsing();

    // A call through BRUTAL's bound vkGetInstanceProcAddr; reading the pointer (no call) doesn't match.
    [GeneratedRegex(@"(?<![\w@])@?GetInstanceProcAddr\s*\.\s*Invoke\s*\(")]
    private static partial Regex ProcAddrCall();

    [GeneratedRegex(@"^(vkCreateInstance|vkEnumerateInstanceExtensionProperties|vkEnumerateInstanceLayerProperties|vkEnumerateInstanceVersion)$")]
    private static partial Regex CommandLiteral();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
