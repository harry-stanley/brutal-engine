using System.Globalization;
using Xunit;

namespace BrutalEngine.Platform.Tests;

public sealed class PayloadRecordTests
{
    [Fact]
    public void KeyEventData_ToString_ForScancodeKey_DoesNotThrow()
    {
        KeyEventData key = new(Scancode.F1, Keycode.FromScancode(Scancode.F1), KeyModifiers.Shift, IsRepeat: true);

        Assert.Equal(
            "KeyEventData { Scancode = F1, Keycode = Keycode { Value = 1073741882 }, Modifiers = Shift, IsRepeat = True }",
            key.ToString());
    }

    [Fact]
    public void KeyEventData_Equality_ComparesEveryField()
    {
        KeyEventData key = new(Scancode.A, new Keycode(97), KeyModifiers.LeftControl, IsRepeat: false);

        Assert.Equal(key, key with { });
        Assert.NotEqual(key, key with { Scancode = Scancode.B });
        Assert.NotEqual(key, key with { Keycode = new Keycode(98) });
        Assert.NotEqual(key, key with { Modifiers = KeyModifiers.None });
        Assert.NotEqual(key, key with { IsRepeat = true });
    }

    [Fact]
    public void MouseRecords_ToString_UseInvariantCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NegativeSign = "~";
        CultureInfo previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
        try
        {
            Assert.Equal(
                "MouseMotionData { X = 10.5, Y = 20, DeltaX = -1.5, DeltaY = 0, Buttons = Left, Right }",
                new MouseMotionData(10.5f, 20f, -1.5f, 0f, MouseButtons.Left | MouseButtons.Right).ToString());
            Assert.Equal(
                "MouseButtonData { Button = Left, Clicks = 2, X = 10.5, Y = -20 }",
                new MouseButtonData(MouseButton.Left, 2, 10.5f, -20f).ToString());
            Assert.Equal(
                "MouseWheelData { X = 0, Y = -1.5, MouseX = 10.5, MouseY = 20 }",
                new MouseWheelData(0f, -1.5f, 10.5f, 20f).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Constructors_StoreEveryField()
    {
        TextEventData text = new(Offset: 3, Length: 5);
        MouseMotionData motion = new(1f, 2f, 3f, 4f, MouseButtons.X2);
        MouseButtonData button = new(MouseButton.X1, 3, 5f, 6f);
        MouseWheelData wheel = new(7f, 8f, 9f, 10f);

        Assert.Equal((3, 5), (text.Offset, text.Length));
        Assert.Equal((1f, 2f, 3f, 4f, MouseButtons.X2), (motion.X, motion.Y, motion.DeltaX, motion.DeltaY, motion.Buttons));
        Assert.Equal((MouseButton.X1, (byte)3, 5f, 6f), (button.Button, button.Clicks, button.X, button.Y));
        Assert.Equal((7f, 8f, 9f, 10f), (wheel.X, wheel.Y, wheel.MouseX, wheel.MouseY));
    }
}
