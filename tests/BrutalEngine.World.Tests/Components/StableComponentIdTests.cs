using System;
using Xunit;

namespace BrutalEngine.World.Tests.Components;

public sealed class StableComponentIdTests
{
    // FNV-1a 64 of "MyGame.\uFFFD" (UTF-8 4d 79 47 61 6d 65 2e ef bf bd): what every lone surrogate after "MyGame."
    // must hash to, because it is encoded as U+FFFD.
    private const ulong ReplacementVector = 0x97AE253931CFB7E2;

    // FNV-1a 64 of "MyGame.\uFFFD\uFFFD" (UTF-8 4d 79 47 61 6d 65 2e ef bf bd ef bf bd), from the same Python reference.
    private const ulong TwoReplacementsVector = 0xA41A1FEABFBC58BF;

    // FNV-1a 64 of "MyGame.\uFFFDX" (UTF-8 4d 79 47 61 6d 65 2e ef bf bd 58), from the same Python reference.
    private const ulong ReplacementThenXVector = 0x8CA0FA2FA3F5310E;

    /// <summary>
    /// FNV-1a 64 (offset basis 0xcbf29ce484222325, prime 0x100000001b3) over each name's UTF-8 bytes, computed
    /// independently of the code under test. "", "a" and "foobar" are the published FNV-1a 64 test vectors of the FNV
    /// reference code (Landon Curt Noll's test_fnv.c, also in the IETF FNV draft). The others come from a separate
    /// reference implementation (Python, over <c>str.encode("utf-8")</c>); their UTF-8 bytes are listed so each can be
    /// rechecked by hand.
    /// </summary>
    public static TheoryData<string, ulong> Vectors => new()
    {
        { "", 0xCBF29CE484222325 },
        { "a", 0xAF63DC4C8601EC8C },
        { "foobar", 0x85944171F73967E8 },

        // 4d 79 47 61 6d 65 2e 56 65 6c 6f 63 69 74 79
        { "MyGame.Velocity", 0x20E35CE39F30210A },

        // A nested type's metadata name: 4d 79 47 61 6d 65 2e 4f 75 74 65 72 2b 49 6e 6e 65 72
        { "MyGame.Outer+Inner", 0xA3844C0A085FF527 },

        // Two-byte UTF-8 (U+00F6, U+00DF): 4d 79 47 61 6d 65 2e 47 72 c3 b6 c3 9f 65
        { "MyGame.Gr\u00F6\u00DFe", 0xBA889ABB110A57D2 },

        // Three-byte UTF-8 (U+901F, U+5EA6): 4d 79 47 61 6d 65 2e e9 80 9f e5 ba a6
        { "MyGame.\u901F\u5EA6", 0x25C50F90DFEB3BDC },

        // Four-byte UTF-8 from a surrogate pair (U+1F680): 4d 79 47 61 6d 65 2e f0 9f 9a 80
        { "MyGame.\uD83D\uDE80", 0x99558A00633515D2 },

        // U+FFFD itself: 4d 79 47 61 6d 65 2e ef bf bd
        { "MyGame.\uFFFD", ReplacementVector },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void KnownVectors(string fullName, ulong expected)
    {
        var id = StableComponentId.FromFullName(fullName);

        Assert.Equal(expected, id.Value);
    }

    [Fact]
    public void FromFullName_LoneSurrogates_HashAsReplacementCharacter()
    {
        // Lone surrogates are built from code units here rather than written in theory data, so test reports never
        // have to carry invalid UTF-16. A high surrogate without its low half, a low surrogate on its own, and a pair
        // in the wrong order (two lone surrogates, so two replacement characters) all encode as U+FFFD.
        string loneHigh = "MyGame." + (char)0xD83D;
        string loneLow = "MyGame." + (char)0xDE80;
        string reversed = "MyGame." + (char)0xDE80 + (char)0xD83D;

        Assert.Equal(ReplacementVector, StableComponentId.FromFullName(loneHigh).Value);
        Assert.Equal(ReplacementVector, StableComponentId.FromFullName(loneLow).Value);
        Assert.Equal(TwoReplacementsVector, StableComponentId.FromFullName(reversed).Value);
        Assert.Equal(StableComponentId.FromFullName("MyGame.\uFFFD\uFFFD"), StableComponentId.FromFullName(reversed));
    }

    [Fact]
    public void FromFullName_HighSurrogateBeforeAscii_ReplacesOnlyTheSurrogate()
    {
        string name = "MyGame." + (char)0xD83D + "X";

        Assert.Equal(ReplacementThenXVector, StableComponentId.FromFullName(name).Value);
        Assert.Equal(StableComponentId.FromFullName("MyGame.\uFFFDX"), StableComponentId.FromFullName(name));
    }

    [Fact]
    public void FromFullName_DoesNotAllocate()
    {
        string ascii = "MyGame.Velocity";
        string nonAscii = "MyGame.Gr\u00F6\u00DFe.\u901F\u5EA6.\uD83D\uDE80";
        string loneSurrogate = "MyGame." + (char)0xD83D + "X";

        // Warm up, so the measured window contains no first-call work.
        ulong sum = StableComponentId.FromFullName(ascii).Value
            + StableComponentId.FromFullName(nonAscii).Value
            + StableComponentId.FromFullName(loneSurrogate).Value;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
        {
            sum += StableComponentId.FromFullName(ascii).Value;
            sum += StableComponentId.FromFullName(nonAscii).Value;
            sum += StableComponentId.FromFullName(loneSurrogate).Value;
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.NotEqual(0UL, sum);
        Assert.Equal(0, allocated);
    }

    [Fact]
    public void FromFullName_SpanOfLargerString_HashesOnlyTheSpan()
    {
        string text = "xxMyGame.Velocityyy";

        var id = StableComponentId.FromFullName(text.AsSpan(2, "MyGame.Velocity".Length));

        Assert.Equal(0x20E35CE39F30210AUL, id.Value);
    }
}
