using System.Runtime.CompilerServices;
using ImageMagick;

namespace NexusMods.UI.Tests;

public static class ModuleInitializer
{
    [ModuleInitializer]
    public static void Init()
    {
        // Only PNG snapshots are used here. This comparer detects the format from the
        // stream; the plugin's other converters embed Magick.NET 14 format values that
        // changed in 15. Do not auto-initialize plugins and register those converters.
        VerifyImageMagick.RegisterComparer(threshold: 0.005D, metric: ErrorMetric.Fuzz, extension: "png");
    }
}
