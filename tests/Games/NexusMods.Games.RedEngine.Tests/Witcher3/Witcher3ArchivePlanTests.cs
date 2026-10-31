using FluentAssertions;
using NexusMods.Games.RedEngine.Witcher3;
using NexusMods.Paths;

namespace NexusMods.Games.RedEngine.Tests.Witcher3;

public class Witcher3ArchivePlanTests
{
    public static IEnumerable<object[]> SupportedLayouts()
    {
        yield return [new[] { "Mods/modWeather/content/bundles/weather.bundle" }, new[] { "Mods/modWeather/content/bundles/weather.bundle" }];
        yield return [new[] { "modWeather/content/scripts/game/weather.ws" }, new[] { "Mods/modWeather/content/scripts/game/weather.ws" }];
        yield return [new[] { "DLC/customWorld/content/bundles/world.bundle" }, new[] { "DLC/customWorld/content/bundles/world.bundle" }];
        yield return [new[] { "dlcCustom/content/bundles/world.bundle" }, new[] { "DLC/dlcCustom/content/bundles/world.bundle" }];
        yield return [new[] { "Release/Mods/modWeather/content/a.bundle", "Release/DLC/dlcWeather/content/b.bundle", "Release/README.txt" }, new[] { "Mods/modWeather/content/a.bundle", "DLC/dlcWeather/content/b.bundle" }];
        yield return [new[] { "Release/modWeather/content/a.bundle", "Release/dlcWeather/content/b.bundle", "LICENSE.md" }, new[] { "Mods/modWeather/content/a.bundle", "DLC/dlcWeather/content/b.bundle" }];
        yield return [new[] { "mOdS\\modWeather\\CONTENT\\a.bundle", "DLC/dlc17/content/b.bundle" }, new[] { "Mods/modWeather/CONTENT/a.bundle", "DLC/dlc17/content/b.bundle" }];
        yield return [new[] { "Mods/modWeather/content/a.bundle", "Mods/modWeather/metadata.xml" }, new[] { "Mods/modWeather/content/a.bundle", "Mods/modWeather/metadata.xml" }];
    }

    [Theory]
    [MemberData(nameof(SupportedLayouts))]
    public void MapsEveryPayloadFileAndPreservesContentLayout(string[] source, string[] expected)
    {
        Witcher3ArchiveInstaller.TryCreatePlan(source, out var plan, out var reason).Should().BeTrue(reason);
        plan.Select(mapping => mapping.TargetPath).Should().Equal(expected.Select(path => (RelativePath)path));
        plan.Select(mapping => source[mapping.Index]).Should().OnlyContain(path => !path.EndsWith("README.txt") && !path.EndsWith("LICENSE.md"));
    }

    [Theory]
    [InlineData("../Mods/modWeather/content/a.bundle")]
    [InlineData("Mods/modWeather/content/../../outside.txt")]
    [InlineData("/Mods/modWeather/content/a.bundle")]
    [InlineData("C:/Mods/modWeather/content/a.bundle")]
    [InlineData("\\\\server\\Mods\\modWeather\\content\\a.bundle")]
    [InlineData("Mods/modWeather/content/a.bundle:stream")]
    [InlineData("Mods/modWeather/content/CON.txt")]
    [InlineData("Mods/modWeather/content/LPT1")]
    [InlineData("Mods/modWeather/content/COM¹.txt")]
    [InlineData("Mods/modWeather/content/trailing. ")]
    [InlineData("Mods/modWeather/content/trailing.")]
    [InlineData("Mods//modWeather/content/a.bundle")]
    [InlineData("Mods/./modWeather/content/a.bundle")]
    public void RejectsWindowsPathHazardsBeforeProducingAPlan(string path)
    {
        Witcher3ArchiveInstaller.TryCreatePlan(["Mods/modValid/content/good.bundle", path], out var plan, out var reason).Should().BeFalse();
        plan.Should().BeEmpty();
        reason.Should().Contain("Windows path");
    }

    [Theory]
    [InlineData("bob")]
    [InlineData("EP1")]
    [InlineData("dlc1")]
    [InlineData("DLC16")]
    public void DoesNotAutomaticallyReplaceOriginalDlc(string folder)
    {
        Witcher3ArchiveInstaller.TryCreatePlan([$"DLC/{folder}/content/a.bundle"], out var plan, out var reason).Should().BeFalse();
        plan.Should().BeEmpty();
        reason.Should().Contain("original game DLC");
    }

    public static IEnumerable<object[]> UnsupportedLayouts()
    {
        yield return [new[] { "Mods/modValid/content/a.bundle", "bin/config/r4game/user_config_matrix/pc/input.xml" }];
        yield return [new[] { "Mods/modValid/content/a.bundle", "input.xml.part.txt" }];
        yield return [new[] { "Mods/modValid/content/a.bundle", "README.exe" }];
        yield return [new[] { "Mods/modValid/content/a.bundle", "Documents/The Witcher 3/mods.settings" }];
        yield return [new[] { "Option A/modFirst/content/a.bundle", "Option B/modSecond/content/b.bundle" }];
        yield return [new[] { "Release/Option A/modFirst/content/a.bundle", "Release/Option B/modSecond/content/b.bundle" }];
        yield return [new[] { "Mods/modFirst/content/a.bundle", "modFirst/content/b.bundle" }];
        yield return [new[] { "Mods/modFirst/content/a.bundle", "mods/MODFIRST/content/A.BUNDLE" }];
        yield return [new[] { "Mods/modFirst/content/data", "Mods/modFirst/content/data/a.bundle" }];
        yield return [new[] { "bin/modFirst/content/a.bundle" }];
        yield return [new[] { "Mods/InvalidName/content/a.bundle" }];
        yield return [new[] { "content/a.bundle" }];
        yield return [new[] { "README.txt" }];
        yield return [Array.Empty<string>()];
    }

    [Theory]
    [MemberData(nameof(UnsupportedLayouts))]
    public void LeavesPartialAmbiguousOrUnsupportedArchivesForManualReview(string[] paths)
    {
        Witcher3ArchiveInstaller.TryCreatePlan(paths, out var plan, out var reason).Should().BeFalse();
        plan.Should().BeEmpty();
        reason.Should().NotBeNullOrWhiteSpace();
    }
}
