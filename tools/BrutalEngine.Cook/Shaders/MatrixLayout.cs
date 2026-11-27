namespace BrutalEngine.Cook.Shaders;

/// <summary>The default matrix layout Slang compiles with (<c>--matrix-layout</c>); a shader can still override it per declaration.</summary>
internal enum MatrixLayout
{
    /// <summary>Column-major, the default (Slang's <c>ColumnMajor</c>).</summary>
    ColumnMajor,

    /// <summary>Row-major (Slang's <c>RowMajor</c>).</summary>
    RowMajor,
}
