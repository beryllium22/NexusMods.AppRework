using System.Globalization;
using FluentAssertions;
using ImageMagick;

namespace NexusMods.UI.Tests;

public class PngComparerCompatibilityTests
{
    [Fact]
    public async Task RegisteredPngComparerAcceptsReencodedPixelsAndRejectsChangedPixels()
    {
        var directory = Directory.CreateTempSubdirectory("nexus-png-comparer-");
        try
        {
            using var original = new MagickImage(MagickColors.Red, 16, 16);
            var expected = original.ToByteArray(MagickFormat.Png24);
            using var decoded = new MagickImage(expected);
            var reencoded = decoded.ToByteArray(MagickFormat.Png32);
            reencoded.SequenceEqual(expected).Should().BeFalse("the registered image comparer must handle different encodings of the same pixels");

            await File.WriteAllBytesAsync(Path.Combine(directory.FullName, "equivalent.verified.png"), expected);
            await VerifyPng(reencoded, directory.FullName, "equivalent").WaitAsync(TimeSpan.FromSeconds(10));

            using var changed = new MagickImage(MagickColors.Blue, 16, 16);
            await File.WriteAllBytesAsync(Path.Combine(directory.FullName, "changed.verified.png"), expected);
            Func<Task> verifyChanged = () => VerifyPng(changed.ToByteArray(MagickFormat.Png32), directory.FullName, "changed")
                .WaitAsync(TimeSpan.FromSeconds(10));

            var rejection = await verifyChanged.Should().ThrowAsync<Exception>();
            rejection.Which.GetType().Name.Should().Be("VerifyException", "a native/API failure must not masquerade as a successful difference check");
            var threshold = 0.005D.ToString(CultureInfo.CurrentCulture);
            rejection.Which.Message.Should().Contain($"threshold({threshold})");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task VerifyPng(byte[] image, string directory, string name)
    {
        await using var stream = new MemoryStream(image, writable: false);
        await Verify(stream, extension: "png")
            .UseDirectory(directory)
            .UseFileName(name)
            .DisableDiff();
    }
}
