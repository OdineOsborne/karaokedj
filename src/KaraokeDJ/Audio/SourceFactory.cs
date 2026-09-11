using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KaraokeDJ.Audio;

/// <summary>Apre qualunque file supportato e lo normalizza a 44.1 kHz stereo float.</summary>
public static class SourceFactory
{
    public const int SampleRate = 44100;
    public const int Channels = 2;
    public static readonly WaveFormat Format = WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    public static readonly string[] AudioExtensions = { ".mp3", ".wav", ".m4a", ".aac", ".wma", ".flac", ".aif", ".aiff" };
    public static readonly string[] VideoExtensions = { ".mp4", ".mkv", ".avi", ".mpg", ".mpeg", ".mov", ".wmv", ".m4v" };

    public static (WaveStream reader, ISampleProvider provider) Open(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        WaveStream reader = ext switch
        {
            ".wav" => new WaveFileReader(path),
            ".aif" or ".aiff" => new AiffFileReader(path),
            _ => new MediaFoundationReader(path),
        };

        ISampleProvider sp = reader.ToSampleProvider();
        if (sp.WaveFormat.Channels == 1)
            sp = new MonoToStereoSampleProvider(sp);
        else if (sp.WaveFormat.Channels > 2)
            throw new NotSupportedException($"File con {sp.WaveFormat.Channels} canali non supportato: {path}");
        if (sp.WaveFormat.SampleRate != SampleRate)
            sp = new WdlResamplingSampleProvider(sp, SampleRate);
        return (reader, sp);
    }
}
