namespace ScreenEase.MyPowerTools;

/// <summary>
/// Gamma ramp math shared by the platform gamma drivers. The Windows driver writes these
/// values through SetDeviceGammaRamp and the macOS driver normalises them into CoreGraphics
/// transfer tables, so both platforms must derive the ramp from exactly the same numbers.
/// </summary>
internal static class ScreenEaseGammaRampMath
{
    public const int RampLength = 256;

    public static ScreenEaseGammaRamp BuildGammaRamp(int kelvin, int brightnessPercent, int terminalOffset = 0)
    {
        var color = ToRgbChannels(kelvin);
        var brightness = Math.Clamp(brightnessPercent, 1, 150);
        return BuildRamp(
            ScaleStep(color.Red, brightness),
            ScaleStep(color.Green, brightness),
            ScaleStep(color.Blue, brightness),
            terminalOffset);
    }

    public static ScreenEaseGammaRamp BuildIdentityRamp() => BuildRamp(257, 257, 257, 0);

    private static ScreenEaseRgbChannels ToRgbChannels(int kelvin)
    {
        var temperature = Math.Clamp(kelvin, 1000, 10000) / 100.0;
        var red = temperature <= 66
            ? 255
            : 329.698727466 * Math.Pow(temperature - 60, -0.1332047592);
        var green = temperature <= 66
            ? 99.4708025861 * Math.Log(temperature) - 161.1195681661
            : 288.1221695283 * Math.Pow(temperature - 60, -0.0755148492);
        var blue = temperature >= 66
            ? 255
            : temperature <= 19
                ? 0
                : 138.5177312231 * Math.Log(temperature - 10) - 305.0447927307;
        return new ScreenEaseRgbChannels(ToChannel(red), ToChannel(green), ToChannel(blue));
    }

    private static ScreenEaseGammaRamp BuildRamp(int redStep, int greenStep, int blueStep, int terminalOffset)
    {
        return new ScreenEaseGammaRamp(
            BuildChannelRamp(redStep, terminalOffset),
            BuildChannelRamp(greenStep, terminalOffset),
            BuildChannelRamp(blueStep, terminalOffset));
    }

    private static ushort[] BuildChannelRamp(int step, int terminalOffset)
    {
        var values = new ushort[RampLength];
        var accumulated = 0;
        for (var index = 0; index < values.Length; index++)
        {
            values[index] = (ushort)Math.Clamp(accumulated, ushort.MinValue, ushort.MaxValue);
            accumulated += step;
        }

        values[^1] = (ushort)Math.Clamp(values[^1] + Math.Clamp(terminalOffset, 0, 2), ushort.MinValue, ushort.MaxValue);
        return values;
    }

    private static int ScaleStep(int channel, int brightnessPercent) =>
        (int)Math.Floor(channel * (Math.Clamp(brightnessPercent, 1, 150) / 100.0) + 0.5);

    private static int ToChannel(double value) => (int)Math.Floor(Math.Clamp(value, 0, 255) + 0.5);
}

internal sealed record ScreenEaseGammaRamp(ushort[] Red, ushort[] Green, ushort[] Blue);

internal readonly record struct ScreenEaseRgbChannels(int Red, int Green, int Blue);
