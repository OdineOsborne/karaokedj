using System.IO.Compression;
using KaraokeDJ.Audio;
using KaraokeDJ.Models;

namespace KaraokeDJ.Services;

public sealed class ScanProgress
{
    public int Done { get; init; }
    public int Total { get; init; }
    public string Current { get; init; } = "";
}

/// <summary>Scansione cartelle, lettura metadati, cache su disco e preparazione file per la riproduzione.</summary>
public sealed class LibraryService
{
    private sealed class LibraryFile
    {
        public List<Track> Tracks { get; set; } = new();
    }

    /// <summary>Alzare quando ReadTags legge campi nuovi: forza la rilettura dei tag di tutta la libreria.</summary>
    public const int TagsVersion = 2;

    private readonly Dictionary<string, Track> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<Track> Tracks => _byPath.Values;

    public void Load()
    {
        var lib = JsonStore.Load<LibraryFile>(AppPaths.LibraryFile);
        _byPath.Clear();
        foreach (var t in lib.Tracks) _byPath[t.FilePath] = t;
    }

    public void Save() => JsonStore.Save(AppPaths.LibraryFile, new LibraryFile { Tracks = _byPath.Values.ToList() });

    public Track? FindById(string id) => _byPath.Values.FirstOrDefault(t => t.Id == id);

    public void Remove(Track t) => _byPath.Remove(t.FilePath);

    public Track? FindByPath(string path) => _byPath.TryGetValue(path, out var t) ? t : null;

    /// <summary>Aggiunge (o aggiorna) un singolo file, es. dopo un download.</summary>
    public Track? AddFile(string path)
    {
        var t = BuildTrack(path);
        if (t != null) _byPath[path] = t;
        return t;
    }

    public async Task<List<Track>> ScanAsync(IEnumerable<string> folders, IProgress<ScanProgress>? progress, CancellationToken ct)
    {
        return await Task.Run(() =>
        {
            var files = new List<string>();
            foreach (var folder in folders.Where(Directory.Exists))
            {
                try
                {
                    files.AddRange(Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                        .Where(IsCandidate));
                }
                catch { }
            }

            // Rimuove dalla cache i file spariti
            var set = new HashSet<string>(files, StringComparer.OrdinalIgnoreCase);
            foreach (var key in _byPath.Keys.Where(k => !set.Contains(k)).ToList())
                _byPath.Remove(key);

            int done = 0;
            var added = new List<Track>();
            foreach (var f in files)
            {
                ct.ThrowIfCancellationRequested();
                done++;
                if (done % 20 == 0 || done == files.Count)
                    progress?.Report(new ScanProgress { Done = done, Total = files.Count, Current = Path.GetFileName(f) });

                var info = new FileInfo(f);
                if (_byPath.TryGetValue(f, out var existing)
                    && existing.FileSize == info.Length
                    && existing.FileModified == info.LastWriteTimeUtc
                    && existing.DurationSec > 0
                    && existing.TagsVersion >= TagsVersion)
                {
                    // aggiorna solo l'associazione cdg (potrebbe essere stato aggiunto)
                    RefreshCdgLink(existing);
                    continue;
                }

                var t = BuildTrack(f);
                if (t == null) { if (existing != null) _byPath.Remove(f); continue; }
                if (existing != null)
                {
                    t.Id = existing.Id;
                    if (t.Bpm <= 0) t.Bpm = existing.Bpm;
                    if (string.IsNullOrEmpty(t.Key)) t.Key = existing.Key;
                    t.Analyzed = existing.Analyzed;
                    t.IntroEndSec = existing.IntroEndSec; t.OutroStartSec = existing.OutroStartSec; t.CuesManual = existing.CuesManual; t.CueSec = existing.CueSec; t.BeatOffsetSec = existing.BeatOffsetSec; t.BeatManual = existing.BeatManual;
                    t.PlayCount = existing.PlayCount; t.LastPlayedUtc = existing.LastPlayedUtc;
                    t.Dedication = existing.Dedication; t.DedicationTitle = existing.DedicationTitle;
                    t.InstrumentalPath = existing.InstrumentalPath; t.VocalsPath = existing.VocalsPath; t.StemsDir = existing.StemsDir; t.IsSuno = existing.IsSuno;
                }
                _byPath[f] = t;
                added.Add(t);
            }
            Save();
            return added;
        }, ct);
    }

    private static bool IsCandidate(string path)
    {
        var name = Path.GetFileName(path);
        if (name.StartsWith("._") || name.StartsWith(".")) return false; // AppleDouble / nascosti
        // versioni strumentali (base senza voce, stem Demucs, ecc.): non sono brani da scaletta
        if (System.Text.RegularExpressions.Regex.IsMatch(name, @"[(\[]\s*instrumental\s*[)\]]", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) return false;
        try { if ((File.GetAttributes(path) & (FileAttributes.Hidden | FileAttributes.System)) != 0) return false; } catch { }
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".cdg") return false;
        if (ext == ".zip") return true;
        return SourceFactory.AudioExtensions.Contains(ext) || SourceFactory.VideoExtensions.Contains(ext);
    }

    private static void RefreshCdgLink(Track t)
    {
        if (t.Kind is TrackKind.Video or TrackKind.CdgZip) return;
        var cdg = Path.ChangeExtension(t.FilePath, ".cdg");
        if (File.Exists(cdg)) { t.CdgPath = cdg; t.Kind = TrackKind.Cdg; }
        else { t.CdgPath = null; t.Kind = TrackKind.Audio; }
    }

    private static Track? BuildTrack(string path)
    {
        try
        {
            var info = new FileInfo(path);
            var ext = info.Extension.ToLowerInvariant();
            var t = new Track
            {
                FilePath = path,
                FileSize = info.Length,
                FileModified = info.LastWriteTimeUtc,
                TagsVersion = TagsVersion,
            };

            if (ext == ".zip")
            {
                using var zip = ZipFile.OpenRead(path);
                var mp3 = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase));
                var cdg = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".cdg", StringComparison.OrdinalIgnoreCase));
                if (mp3 == null || cdg == null) return null;
                t.Kind = TrackKind.CdgZip;
                ParseFileName(Path.GetFileNameWithoutExtension(path), t);
                try
                {
                    using var s = mp3.Open();
                    using var ms = new MemoryStream();
                    s.CopyTo(ms);
                    ms.Position = 0;
                    ReadTags(new StreamAbstraction(mp3.Name, ms), t, keepFileNameTitle: true);
                }
                catch { }
                return t;
            }

            t.Kind = SourceFactory.VideoExtensions.Contains(ext) ? TrackKind.Video : TrackKind.Audio;
            ParseFileName(Path.GetFileNameWithoutExtension(path), t);
            RefreshCdgLink(t);
            try { ReadTags(new TagLib.File.LocalFileAbstraction(path), t, keepFileNameTitle: false); } catch { }

            if (t.DurationSec <= 0)
            {
                try
                {
                    using var r = new NAudio.Wave.MediaFoundationReader(path);
                    t.DurationSec = r.TotalTime.TotalSeconds;
                }
                catch { }
            }
            // file non decodificabile (corrotto, formato non supportato, resource fork): fuori dalla libreria
            if (t.DurationSec <= 0) return null;
            return t;
        }
        catch
        {
            return null;
        }
    }

    private static void ReadTags(TagLib.File.IFileAbstraction abstraction, Track t, bool keepFileNameTitle)
    {
        using var tf = TagLib.File.Create(abstraction);
        var title = tf.Tag.Title?.Trim();
        var artist = (tf.Tag.FirstPerformer ?? tf.Tag.FirstAlbumArtist)?.Trim();
        if (!keepFileNameTitle || string.IsNullOrEmpty(t.Title))
        {
            if (!string.IsNullOrEmpty(title)) t.Title = title;
            if (!string.IsNullOrEmpty(artist)) t.Artist = artist;
        }
        if (tf.Properties?.Duration.TotalSeconds > 0)
            t.DurationSec = tf.Properties.Duration.TotalSeconds;
        // BPM e tonalità dai tag (TBPM / TKEY), se presenti
        if (tf.Tag.BeatsPerMinute > 0) t.Bpm = tf.Tag.BeatsPerMinute;
        if (tf.Tag.Year is > 1900 and < 2100) t.Year = (int)tf.Tag.Year;
        var genre = tf.Tag.FirstGenre?.Trim();
        if (!string.IsNullOrEmpty(genre)) t.Genre = genre;
        var composers = tf.Tag.Composers;
        if (composers is { Length: > 0 }) t.Composer = string.Join(", ", composers.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c.Trim()));
        var key = NormalizeKey(tf.Tag.InitialKey);
        if (key != null) t.Key = key;
    }

    /// <summary>Converte notazioni comuni ("Am", "A minor", "Abm", "8A" Camelot, "o" per minore) nel formato interno "A#m".</summary>
    private static string? NormalizeKey(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        var camelot = System.Text.RegularExpressions.Regex.Match(s, @"^(\d{1,2})\s*([ABab])$");
        if (camelot.Success)
        {
            int n = int.Parse(camelot.Groups[1].Value);
            bool minor = char.ToUpperInvariant(camelot.Groups[2].Value[0]) == 'A';
            string[] majors = { "", "B", "F#", "C#", "G#", "D#", "A#", "F", "C", "G", "D", "A", "E" };
            if (n < 1 || n > 12) return null;
            var major = majors[n];
            return minor ? Audio.AudioAnalyzer.Transpose(major, -3) + "m" : major;
        }
        var m = System.Text.RegularExpressions.Regex.Match(s, @"^([A-Ga-g])([#b♭♯]?)\s*(m|min|minor|o|-)?\s*(maj|major)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        string root = m.Groups[1].Value.ToUpperInvariant();
        string acc = m.Groups[2].Value;
        string[] notes = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
        int idx = Array.IndexOf(notes, root);
        if (acc is "#" or "♯") idx = (idx + 1) % 12;
        else if (acc is "b" or "♭") idx = (idx + 11) % 12;
        bool isMinor = m.Groups[3].Success && m.Groups[3].Value.Length > 0;
        return notes[idx] + (isMinor ? "m" : "");
    }

    /// <summary>"Artista - Titolo" oppure "01. Artista - Titolo" oppure solo titolo.</summary>
    private static void ParseFileName(string name, Track t)
    {
        name = System.Text.RegularExpressions.Regex.Replace(name, @"^\s*\d{1,3}[\.\-_ ]+\s*", "");
        var idx = name.IndexOf(" - ", StringComparison.Ordinal);
        if (idx > 0)
        {
            t.Artist = name[..idx].Trim();
            t.Title = name[(idx + 3)..].Trim();
        }
        else
        {
            t.Title = name.Trim();
        }
    }

    /// <summary>Restituisce i percorsi audio e cdg pronti per il deck (estrae gli zip nella cache).</summary>
    public static (string audioPath, string? cdgPath) PrepareForPlayback(Track t)
    {
        if (t.Kind != TrackKind.CdgZip) return (t.FilePath, t.CdgPath);

        var dir = Path.Combine(AppPaths.CacheDir, t.Id);
        var mp3Out = Path.Combine(dir, "audio.mp3");
        var cdgOut = Path.Combine(dir, "graphics.cdg");
        if (!(File.Exists(mp3Out) && File.Exists(cdgOut)))
        {
            Directory.CreateDirectory(dir);
            using var zip = ZipFile.OpenRead(t.FilePath);
            var mp3 = zip.Entries.First(e => e.Name.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase));
            var cdg = zip.Entries.First(e => e.Name.EndsWith(".cdg", StringComparison.OrdinalIgnoreCase));
            mp3.ExtractToFile(mp3Out, true);
            cdg.ExtractToFile(cdgOut, true);
        }
        return (mp3Out, cdgOut);
    }

    private sealed class StreamAbstraction : TagLib.File.IFileAbstraction
    {
        public StreamAbstraction(string name, System.IO.Stream s) { Name = name; ReadStream = s; WriteStream = s; }
        public string Name { get; }
        public System.IO.Stream ReadStream { get; }
        public System.IO.Stream WriteStream { get; }
        public void CloseStream(System.IO.Stream stream) { }
    }
}
