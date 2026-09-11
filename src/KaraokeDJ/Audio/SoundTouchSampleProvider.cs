using NAudio.Wave;
using SoundTouch;

namespace KaraokeDJ.Audio;

/// <summary>
/// Cambio tonalità / tempo tramite SoundTouch. Quando i parametri sono neutri
/// il segnale passa inalterato (bypass) per non degradare la qualità.
/// </summary>
public sealed class SoundTouchSampleProvider : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly SoundTouchProcessor _st = new();
    private readonly int _channels;
    private readonly float[] _srcBuf;
    private bool _sourceEnded;
    private bool _flushed;
    private int _semitones;
    private double _tempo = 1.0;
    private bool _keyLock = true;

    public SoundTouchSampleProvider(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        _srcBuf = new float[2048 * _channels];
        _st.SampleRate = source.WaveFormat.SampleRate;
        _st.Channels = _channels;
        _st.SetSetting(SettingId.UseQuickSeek, 0);
        _st.SetSetting(SettingId.UseAntiAliasFilter, 1);
        // Parametri più adatti a musica con voce (il default SoundTouch è tarato per parlato)
        _st.SetSetting(SettingId.SequenceDurationMs, 40);
        _st.SetSetting(SettingId.SeekWindowDurationMs, 15);
        _st.SetSetting(SettingId.OverlapDurationMs, 8);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public bool Bypass => _semitones == 0 && Math.Abs(_tempo - 1.0) < 0.0005;

    public int Semitones
    {
        get => _semitones;
        set { _semitones = Math.Clamp(value, -12, 12); _st.PitchSemiTones = _semitones; }
    }

    public double Tempo
    {
        get => _tempo;
        set { _tempo = Math.Clamp(value, 0.5, 1.5); ApplySpeed(); }
    }

    /// <summary>true: cambiare velocità non cambia il tono (master tempo). false: il tono segue la velocità (vinile).</summary>
    public bool KeyLock
    {
        get => _keyLock;
        set { _keyLock = value; ApplySpeed(); }
    }

    private void ApplySpeed()
    {
        if (_keyLock) { _st.Tempo = _tempo; _st.Rate = 1.0; }
        else { _st.Tempo = 1.0; _st.Rate = _tempo; }
    }

    public void Reset()
    {
        _st.Clear();
        _sourceEnded = false;
        _flushed = false;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (Bypass)
            return _source.Read(buffer, offset, count);

        int framesWanted = count / _channels;
        int got = 0;
        while (got < framesWanted)
        {
            int n = _st.ReceiveSamples(buffer.AsSpan(offset + got * _channels), framesWanted - got);
            got += n;
            if (got >= framesWanted) break;

            if (_sourceEnded)
            {
                if (_flushed) break;
                _st.Flush();
                _flushed = true;
                continue;
            }

            int read = _source.Read(_srcBuf, 0, _srcBuf.Length);
            if (read == 0)
            {
                _sourceEnded = true;
                continue;
            }
            _st.PutSamples(_srcBuf.AsSpan(0, read), read / _channels);
        }
        return got * _channels;
    }
}
