namespace ScreenEase.Core;

public readonly record struct RgbScale(double Red, double Green, double Blue);

public readonly record struct RgbChannels(int Red, int Green, int Blue);

public sealed record GammaRamp(ushort[] Red, ushort[] Green, ushort[] Blue);

public static class ColorTemperature
{
    private const int GammaEntryCount = 256;
    private const int ChannelMaximum = 255;
    private const int IdentityStep = 257;

    public static RgbScale ToRgbScale(int kelvin)
    {
        var channels = ToRgbChannels(kelvin);
        return new RgbScale(
            channels.Red / (double)ChannelMaximum,
            channels.Green / (double)ChannelMaximum,
            channels.Blue / (double)ChannelMaximum);
    }

    public static RgbChannels ToRgbChannels(int kelvin)
    {
        var temperature = Validation.ClampKelvin(kelvin) / 100.0;

        var red = temperature <= 66
            ? 255
            : 329.698727466 * Math.Pow(temperature - 60, -0.1332047592);

        double green = temperature <= 66
            ? 99.4708025861 * Math.Log(temperature) - 161.1195681661
            : 288.1221695283 * Math.Pow(temperature - 60, -0.0755148492);

        var blue = temperature >= 66
            ? 255
            : temperature <= 19
                ? 0
                : 138.5177312231 * Math.Log(temperature - 10) - 305.0447927307;

        return new RgbChannels(ToChannel(red), ToChannel(green), ToChannel(blue));
    }

    public static GammaRamp BuildGammaRamp(int kelvin, int brightnessPercent) =>
        BuildGammaRamp(kelvin, brightnessPercent, terminalOffset: 0);

    public static GammaRamp BuildGammaRamp(int kelvin, int brightnessPercent, int terminalOffset)
    {
        var color = ToRgbChannels(kelvin);
        var brightness = Validation.ClampBrightness(brightnessPercent);
        return BuildRamp(
            ScaleStep(color.Red, brightness),
            ScaleStep(color.Green, brightness),
            ScaleStep(color.Blue, brightness),
            terminalOffset);
    }

    public static GammaRamp BuildIdentityRamp() =>
        BuildRamp(IdentityStep, IdentityStep, IdentityStep, terminalOffset: 0);

    private static GammaRamp BuildRamp(int redStep, int greenStep, int blueStep, int terminalOffset)
    {
        var red = BuildChannelRamp(redStep, terminalOffset);
        var green = BuildChannelRamp(greenStep, terminalOffset);
        var blue = BuildChannelRamp(blueStep, terminalOffset);
        return new GammaRamp(red, green, blue);
    }

    private static ushort[] BuildChannelRamp(int step, int terminalOffset)
    {
        var values = new ushort[GammaEntryCount];
        var accumulated = 0;

        for (var index = 0; index < values.Length; index++)
        {
            values[index] = ToUShort(accumulated);
            accumulated += step;
        }

        values[^1] = ToUShort(values[^1] + Math.Clamp(terminalOffset, 0, 2));
        return values;
    }

    private static int ScaleStep(int channel, int brightnessPercent) =>
        RoundToInt(channel * (Validation.ClampBrightness(brightnessPercent) / 100.0));

    private static int ToChannel(double value) =>
        RoundToInt(Math.Clamp(value, 0, ChannelMaximum));

    private static int RoundToInt(double value) =>
        (int)Math.Floor(value + 0.5);

    private static ushort ToUShort(int value) =>
        (ushort)Math.Clamp(value, ushort.MinValue, ushort.MaxValue);
}


