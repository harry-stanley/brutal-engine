using System;
using System.IO;
using Xunit;

namespace BrutalEngine.Platform.Sdl3.Tests;

public sealed class OptionsValidatorTests
{
    private static readonly string _absolute = Path.GetTempPath();

    // 64 characters, including every punctuation character the rule allows.
    private const string AllNameCharacters = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz01234567 ._-";

    public static TheoryData<string> InvalidRules =>
    [
        "OptionsNull",
        "OrganizationNull", "OrganizationEmpty", "OrganizationTooLong", "OrganizationSlash", "OrganizationNonAscii",
        "ApplicationNull", "ApplicationEmpty", "ApplicationTooLong", "ApplicationColon",
        "HeadlessNegative", "HeadlessUndefined",
        "EventCapacityTooSmall", "EventCapacityTooLarge",
        "TextCapacityTooSmall", "TextCapacityTooLarge",
        "ClockNull",
        "UserDataDirectoryEmpty", "UserDataDirectoryRelative",
        "VulkanLibraryPathEmpty", "VulkanLibraryPathRelative",
    ];

    public static TheoryData<string> ValidRules =>
    [
        "Defaults", "NameOneCharacter", "NameSixtyFourAllowedCharacters", "Offscreen", "NoSurface",
        "EventCapacityMinimum", "EventCapacityMaximum", "TextCapacityMinimum", "TextCapacityMaximum",
        "UserDataDirectoryAbsolute", "VulkanLibraryPathAbsolute", "SdlOptionsNull",
    ];

    [Theory]
    [MemberData(nameof(InvalidRules))]
    public void InvalidOption_Throws(string rule)
    {
        (PlatformOptions? options, SdlPlatformOptions? sdlOptions, Type expected, string named) = Invalid(rule);

        Exception exception = Assert.Throws(expected, () => OptionsValidator.Validate(options!, sdlOptions));

        Assert.Contains(named, exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ValidRules))]
    public void ValidOption_DoesNotThrow(string rule)
    {
        (PlatformOptions options, SdlPlatformOptions? sdlOptions) = Valid(rule);

        OptionsValidator.Validate(options, sdlOptions);
    }

    private static PlatformOptions Base => new() { Organization = "Ahwoo", Application = "Game" };

    private static (PlatformOptions? Options, SdlPlatformOptions? Sdl, Type Expected, string Named) Invalid(string rule)
    {
        Type argument = typeof(ArgumentException);
        Type range = typeof(ArgumentOutOfRangeException);
        return rule switch
        {
            "OptionsNull" => (null, null, typeof(ArgumentNullException), "options"),
            "OrganizationNull" => (Base with { Organization = null! }, null, argument, "Organization"),
            "OrganizationEmpty" => (Base with { Organization = "" }, null, argument, "Organization"),
            "OrganizationTooLong" => (Base with { Organization = new string('a', 65) }, null, argument, "Organization"),
            "OrganizationSlash" => (Base with { Organization = "a/b" }, null, argument, "Organization"),
            "OrganizationNonAscii" => (Base with { Organization = "Caf\u00e9" }, null, argument, "Organization"),
            "ApplicationNull" => (Base with { Application = null! }, null, argument, "Application"),
            "ApplicationEmpty" => (Base with { Application = "" }, null, argument, "Application"),
            "ApplicationTooLong" => (Base with { Application = new string('a', 65) }, null, argument, "Application"),
            "ApplicationColon" => (Base with { Application = "a:b" }, null, argument, "Application"),
            "HeadlessNegative" => (Base with { Headless = (HeadlessMode)(-1) }, null, range, "Headless"),
            "HeadlessUndefined" => (Base with { Headless = (HeadlessMode)3 }, null, range, "Headless"),
            "EventCapacityTooSmall" => (Base with { EventCapacity = 63 }, null, range, "EventCapacity"),
            "EventCapacityTooLarge" => (Base with { EventCapacity = 65537 }, null, range, "EventCapacity"),
            "TextCapacityTooSmall" => (Base with { TextCapacityBytes = 2047 }, null, range, "TextCapacityBytes"),
            "TextCapacityTooLarge" => (Base with { TextCapacityBytes = 1048577 }, null, range, "TextCapacityBytes"),
            "ClockNull" => (Base with { Clock = null! }, null, argument, "Clock"),
            "UserDataDirectoryEmpty" => (Base with { UserDataDirectory = "" }, null, argument, "UserDataDirectory"),
            "UserDataDirectoryRelative" => (Base with { UserDataDirectory = "saves" }, null, argument, "UserDataDirectory"),
            "VulkanLibraryPathEmpty" => (Base, new() { VulkanLibraryPath = "" }, argument, "VulkanLibraryPath"),
            "VulkanLibraryPathRelative" => (Base, new() { VulkanLibraryPath = "lib/vulkan" }, argument, "VulkanLibraryPath"),
            _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
        };
    }

    private static (PlatformOptions Options, SdlPlatformOptions? Sdl) Valid(string rule) => rule switch
    {
        "Defaults" => (Base with { Logger = null }, new()),
        "NameOneCharacter" => (Base with { Organization = "a", Application = "Z" }, null),
        "NameSixtyFourAllowedCharacters" => (Base with { Organization = AllNameCharacters, Application = AllNameCharacters }, null),
        "Offscreen" => (Base with { Headless = HeadlessMode.Offscreen }, null),
        "NoSurface" => (Base with { Headless = HeadlessMode.NoSurface }, null),
        "EventCapacityMinimum" => (Base with { EventCapacity = 64 }, null),
        "EventCapacityMaximum" => (Base with { EventCapacity = 65536 }, null),
        "TextCapacityMinimum" => (Base with { TextCapacityBytes = 2048 }, null),
        "TextCapacityMaximum" => (Base with { TextCapacityBytes = 1048576 }, null),
        "UserDataDirectoryAbsolute" => (Base with { UserDataDirectory = _absolute }, null),
        "VulkanLibraryPathAbsolute" => (Base, new() { VulkanLibraryPath = Path.Combine(_absolute, "vulkan-1.dll") }),
        "SdlOptionsNull" => (Base, null),
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown rule."),
    };
}
