namespace BrutalEngine.Cook;

/// <summary>
/// The process exit codes of brutal-cook, one per failure class (docs/design/cook.md). Scripts and CI rely on these
/// exact values. 1 is never used, so a crash can't pass for a known failure; later verbs add codes from 6 up.
/// </summary>
internal enum CookExitCode
{
    /// <summary>Every requested output was written, or help was printed.</summary>
    Success = 0,

    /// <summary>Slang reported errors. Its diagnostics went to standard error unchanged, and no output was written.</summary>
    CompileFailed = 2,

    /// <summary>The input file doesn't exist.</summary>
    InputNotFound = 3,

    /// <summary>The output directory couldn't be created, or an output file couldn't be written.</summary>
    OutputFailed = 4,

    /// <summary>
    /// The Slang runtime failed to load, a Slang global session or compile request couldn't be created, Slang has no
    /// <c>spirv_1_6</c> profile, or it produced no code for an entry point.
    /// </summary>
    ToolFailure = 5,

    /// <summary>A bad command line: no verb, an unknown verb, or invalid arguments. Usage went to standard error.</summary>
    Usage = 64,
}
