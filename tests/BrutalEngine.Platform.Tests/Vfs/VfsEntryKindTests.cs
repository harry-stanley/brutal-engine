using System;
using BrutalEngine.Platform.Vfs;
using Xunit;

namespace BrutalEngine.Platform.Tests.Vfs;

public sealed class VfsEntryKindTests
{
    // Values written from docs/design/platform.md section 5: VfsEntryKind { None = 0, File, Directory }.
    [Fact]
    public void Values_MatchDesign()
    {
        Assert.Equal(0, (int)VfsEntryKind.None);
        Assert.Equal(1, (int)VfsEntryKind.File);
        Assert.Equal(2, (int)VfsEntryKind.Directory);
        Assert.Equal(["None", "File", "Directory"], Enum.GetNames<VfsEntryKind>());
        Assert.Equal(VfsEntryKind.None, default(VfsEntryKind));
    }
}
