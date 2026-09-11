using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace KaraokeDJ.Audio;

/// <summary>Riproduzione one-shot di jingle/effetti, sommati nel mixer principale.</summary>
public sealed class PadPlayer
{
    private readonly MixingSampleProvider _mixer;
    private readonly List<OneShot> _active = new();
    private readonly object _gate = new();

    public PadPlayer(MixingSampleProvider mixer) => _mixer = mixer;

    public float Volume { get; set; } = 1f;

    public event Action<int>? PadFinished;

    public void Play(int padIndex, string path)
    {
        var (reader, provider) = SourceFactory.Open(path);
        var vol = new VolumeSampleProvider(provider) { Volume = Volume };
        var shot = new OneShot(padIndex, reader, vol, this);
        lock (_gate)
        {
            // Un pad ripremuto riparte da capo: fermiamo l'istanza precedente
            foreach (var a in _active.Where(a => a.PadIndex == padIndex).ToList())
            {
                _mixer.RemoveMixerInput(a);
                a.Dispose();
                _active.Remove(a);
            }
            _active.Add(shot);
        }
        _mixer.AddMixerInput(shot);
    }

    public bool IsPlaying(int padIndex)
    {
        lock (_gate) return _active.Any(a => a.PadIndex == padIndex);
    }

    public void Stop(int padIndex)
    {
        List<OneShot> stopped;
        lock (_gate)
        {
            stopped = _active.Where(a => a.PadIndex == padIndex).ToList();
            foreach (var a in stopped)
            {
                _mixer.RemoveMixerInput(a);
                a.Dispose();
                _active.Remove(a);
            }
        }
        foreach (var a in stopped) PadFinished?.Invoke(a.PadIndex);
    }

    public void StopAll()
    {
        List<OneShot> stopped;
        lock (_gate)
        {
            stopped = _active.ToList();
            foreach (var a in stopped)
            {
                _mixer.RemoveMixerInput(a);
                a.Dispose();
            }
            _active.Clear();
        }
        foreach (var a in stopped) PadFinished?.Invoke(a.PadIndex);
    }

    private void Finished(OneShot shot)
    {
        lock (_gate)
        {
            _active.Remove(shot);
            shot.Dispose();
        }
        PadFinished?.Invoke(shot.PadIndex);
    }

    private sealed class OneShot : ISampleProvider, IDisposable
    {
        private readonly WaveStream _reader;
        private readonly ISampleProvider _provider;
        private readonly PadPlayer _owner;
        private bool _done;

        public OneShot(int padIndex, WaveStream reader, ISampleProvider provider, PadPlayer owner)
        {
            PadIndex = padIndex; _reader = reader; _provider = provider; _owner = owner;
        }

        public int PadIndex { get; }
        public WaveFormat WaveFormat => _provider.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            if (_done) return 0;
            int n;
            try { n = _provider.Read(buffer, offset, count); }
            catch { n = 0; }
            if (n == 0)
            {
                _done = true;
                _owner.Finished(this);
            }
            return n;
        }

        public void Dispose() { try { _reader.Dispose(); } catch { } }
    }
}
