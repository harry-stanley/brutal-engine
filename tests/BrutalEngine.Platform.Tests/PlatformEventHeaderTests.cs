using System.Globalization;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class PlatformEventHeaderTests
{
    [Fact]
    public void Members_EqualityAndToString()
    {
        PlatformEventHeader header = new(PlatformEventKind.KeyDown, new WindowId(3), Timestamp: -120);

        Assert.Equal(
            (PlatformEventKind.KeyDown, new WindowId(3), -120L),
            (header.Kind, header.Window, header.Timestamp));
        Assert.Equal(default, new PlatformEventHeader(PlatformEventKind.None, default, 0));
        Assert.Equal(header, header with { });
        Assert.NotEqual(header, header with { Kind = PlatformEventKind.KeyUp });
        Assert.NotEqual(header, header with { Window = new WindowId(4) });
        Assert.NotEqual(header, header with { Timestamp = 120 });

        // A culture whose negative sign isn't '-': the timestamp must still print with the invariant one.
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NegativeSign = "~";
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            Assert.Equal(
                "PlatformEventHeader { Kind = KeyDown, Window = WindowId { Value = 3 }, Timestamp = -120 }",
                header.ToString());
            Assert.Equal(
                "PlatformEventHeader { Kind = 65535, Window = WindowId { Value = 0 }, Timestamp = 0 }",
                new PlatformEventHeader((PlatformEventKind)ushort.MaxValue, default, 0).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
