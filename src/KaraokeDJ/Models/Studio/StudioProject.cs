using System.Text.Json;
using System.Text.Json.Serialization;

namespace KaraokeDJ.Models.Studio;

/// <summary>
/// Progetto dello Studio: una timeline a più corsie come la Playlist di FL Studio. Le clip stanno dove le metti,
/// ognuna con i suoi tagli, loop, tempo, tonalità ed effetti automatizzati; il mix "da DJ" è solo un modo di
/// disporle (due corsie alternate come i deck, passaggi scritti come automazioni che si possono ritoccare).
/// Tutto in secondi di timeline: la griglia in battute viene dal tempo del progetto.
/// </summary>
public sealed class StudioProject
{
    public int Version { get; set; } = 1;
    public string Name { get; set; } = "Nuovo mix";
    /// <summary>Tempo del progetto (griglia, e tempo a cui si portano le clip "a tempo"). 0 = dal primo brano.</summary>
    public double Bpm { get; set; }
    /// <summary>Dove cade il primo "1" della griglia del progetto (secondi di timeline).</summary>
    public double GridOffsetSec { get; set; }
    public List<StudioLane> Lanes { get; set; } = new();
    public List<StudioClip> Clips { get; set; } = new();
    public double MasterGainDb { get; set; }
    /// <summary>Volume uniformato fra i brani (misurato sul pezzo usato) e limitatore sul master.</summary>
    public bool Normalize { get; set; } = true;
    public string? LastExportPath { get; set; }

    [JsonIgnore] public double BeatSec => 60.0 / (Bpm > 0 ? Bpm : 120);
    [JsonIgnore] public double EndSec => Clips.Count == 0 ? 0 : Clips.Max(c => c.EndSec);

    public StudioLane? Lane(string id) => Lanes.FirstOrDefault(l => l.Id == id);

    public StudioLane AddLane(string name, LaneKind kind)
    {
        var l = new StudioLane { Name = name, Kind = kind };
        Lanes.Add(l);
        return l;
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Json);
    public static StudioProject FromJson(string json) => JsonSerializer.Deserialize<StudioProject>(json, Json) ?? new StudioProject();
    /// <summary>Copia indipendente: il motore suona una fotografia, mentre l'interfaccia modifica l'originale.</summary>
    public StudioProject Clone() => FromJson(ToJson());

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, ToJson());
        File.Move(tmp, path, overwrite: true);
    }

    public static StudioProject Load(string path) => FromJson(File.ReadAllText(path));
}

public enum LaneKind { Music, Voice, Fx }

public sealed class StudioLane
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Name { get; set; } = "";
    public LaneKind Kind { get; set; }
    public double GainDb { get; set; }
    public double Pan { get; set; }
    public bool Mute { get; set; }
    public bool Solo { get; set; }
    /// <summary>Quando suona una clip di questa corsia la musica si abbassa (annunci, frasi degli sposi).</summary>
    public bool DucksMusic { get; set; }
    public double DuckDb { get; set; } = -12;
}

public sealed class StudioClip
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string LaneId { get; set; } = "";
    /// <summary>Brano della libreria (se c'è); altrimenti <see cref="FilePath"/> (voce registrata, effetto).</summary>
    public string? TrackId { get; set; }
    public string? FilePath { get; set; }
    public string Label { get; set; } = "";

    /// <summary>Dove comincia sulla timeline (secondi).</summary>
    public double StartSec { get; set; }
    /// <summary>Pezzo del file usato (secondi del file).</summary>
    public double InSec { get; set; }
    public double OutSec { get; set; }

    /// <summary>BPM originali del brano (per la griglia della clip e per portarla a tempo). 0 = sconosciuti.</summary>
    public double Bpm { get; set; }
    /// <summary>Un battere del brano (secondi del file), per allineare tagli e loop alla sua griglia.</summary>
    public double BeatAnchorSec { get; set; }
    /// <summary>Velocità di riproduzione (1 = originale). Con "a tempo" la calcola lo Studio dal tempo del progetto.</summary>
    public double Tempo { get; set; } = 1;
    public bool Warp { get; set; } = true;
    public int KeyShift { get; set; }

    public double GainDb { get; set; }
    /// <summary>Correzione di volume misurata (uniformare i brani). NaN = da misurare.</summary>
    public double AutoGainDb { get; set; } = double.NaN;
    public double FadeInSec { get; set; }
    public double FadeOutSec { get; set; }

    public List<StudioLoop> Loops { get; set; } = new();
    public List<Automation> Autos { get; set; } = new();
    /// <summary>Passaggio in entrata scelto dall'assistente (solo etichetta: gli effetti veri sono nelle automazioni).</summary>
    public string? TransitionIn { get; set; }
    public double TransitionInSec { get; set; }

    /// <summary>Durata del pezzo di file suonato, loop compresi (secondi del file).</summary>
    [JsonIgnore] public double SourceLengthSec => Math.Max(0, OutSec - InSec) + Loops.Sum(l => l.LengthSec * l.Repeats);
    [JsonIgnore] public double LengthSec => SourceLengthSec / Math.Max(0.25, Tempo);
    [JsonIgnore] public double EndSec => StartSec + LengthSec;
    [JsonIgnore] public double EffectiveBpm => Bpm * Tempo;
    [JsonIgnore] public double FileBeatSec => Bpm > 0 ? 60.0 / Bpm : 0.5;

    public Automation Auto(string param)
    {
        var a = Autos.FirstOrDefault(x => x.Param == param);
        if (a == null) { a = new Automation { Param = param }; Autos.Add(a); }
        return a;
    }

    /// <summary>Valore dell'automazione al tempo <paramref name="t"/> (secondi dall'inizio della clip), o il neutro.</summary>
    public double ValueAt(string param, double t)
    {
        foreach (var a in Autos) if (a.Param == param && a.Points.Count > 0) return a.ValueAt(t);
        return AutoParams.Neutral(param);
    }

    /// <summary>Sequenza dei pezzi di file da suonare (loop espansi), in secondi del file.</summary>
    public List<(double From, double To)> Segments()
    {
        var segs = new List<(double, double)>();
        double pos = InSec;
        foreach (var l in Loops.Where(l => l.AtSec >= InSec && l.AtSec < OutSec && l.LengthSec > 0.01).OrderBy(l => l.AtSec))
        {
            double end = l.AtSec + l.LengthSec;
            if (l.AtSec > pos) segs.Add((pos, l.AtSec));
            // il loop suona una volta "normale" più le ripetizioni
            for (int i = 0; i < l.Repeats; i++) segs.Add((l.AtSec, end));
            pos = l.AtSec;
        }
        if (OutSec > pos) segs.Add((pos, OutSec));
        return segs;
    }

    /// <summary>Tempo sulla timeline (secondi dall'inizio della clip) → secondo del file che suona.</summary>
    public double FileTimeAt(double t)
    {
        double src = t * Tempo;
        foreach (var (from, to) in Segments())
        {
            double len = to - from;
            if (src < len) return from + src;
            src -= len;
        }
        return OutSec;
    }
}

/// <summary>Ripete un pezzo: una battuta ×4 per allungare, 1/4 di battito ×8 per un "roll" prima del drop.</summary>
public sealed class StudioLoop
{
    public double AtSec { get; set; }
    public double LengthSec { get; set; }
    public int Repeats { get; set; } = 1;
}

public sealed class Automation
{
    public string Param { get; set; } = "";
    public List<AutoPoint> Points { get; set; } = new();

    public double ValueAt(double t)
    {
        var p = Points;
        if (p.Count == 0) return AutoParams.Neutral(Param);
        if (t <= p[0].T) return p[0].V;
        if (t >= p[^1].T) return p[^1].V;
        for (int i = 1; i < p.Count; i++)
            if (t < p[i].T)
            {
                var a = p[i - 1]; var b = p[i];
                if (b.Step) return a.V;
                double f = (t - a.T) / Math.Max(1e-6, b.T - a.T);
                return a.V + (b.V - a.V) * f;
            }
        return p[^1].V;
    }

    /// <summary>Sostituisce i punti fra <paramref name="t0"/> e <paramref name="t1"/> con quelli nuovi.</summary>
    public void Replace(double t0, double t1, IEnumerable<AutoPoint> pts)
    {
        Points.RemoveAll(p => p.T >= t0 - 1e-6 && p.T <= t1 + 1e-6);
        Points.AddRange(pts);
        Points.Sort((a, b) => a.T.CompareTo(b.T));
    }
}

public sealed class AutoPoint
{
    public double T { get; set; }
    public double V { get; set; }
    /// <summary>Salto secco al valore del punto precedente fino a questo (on/off, taglio sul battere).</summary>
    public bool Step { get; set; }
    public AutoPoint() { }
    public AutoPoint(double t, double v, bool step = false) { T = t; V = v; Step = step; }
}

/// <summary>I parametri automatizzabili di una clip, con nome, scala e valore neutro.</summary>
public static class AutoParams
{
    public sealed record Info(string Id, string Name, double Min, double Max, double Neutral, string Unit);

    public static readonly Info[] All =
    {
        new("vol", "Volume", 0, 1.5, 1, ""),
        new("low", "Bassi", -26, 6, 0, "dB"),
        new("mid", "Medi", -26, 6, 0, "dB"),
        new("high", "Alti", -26, 6, 0, "dB"),
        new("filter", "Filtro (← passa-basso · passa-alto →)", -1, 1, 0, ""),
        new("echo", "Echo", 0, 1, 0, ""),
        new("dry", "Segnale diretto (0 = solo code dell'echo)", 0, 1, 1, ""),
        new("reverb", "Riverbero", 0, 1, 0, ""),
        new("flanger", "Flanger", 0, 1, 0, ""),
        new("phaser", "Phaser", 0, 1, 0, ""),
        new("gate", "Gate a tempo", 0, 1, 0, ""),
        new("crush", "Bitcrusher", 0, 1, 0, ""),
        new("pan", "Panorama", -1, 1, 0, ""),
    };

    public static Info Get(string id) => All.FirstOrDefault(i => i.Id == id) ?? All[0];
    public static double Neutral(string id) => Get(id).Neutral;
}
