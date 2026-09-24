namespace KaraokeDJ.Audio;

/// <summary>
/// Velocità del piatto misurata dai messaggi MIDI.
///
/// Le console mandano un messaggio per ogni scatto dell'encoder e la velocità sta nella FREQUENZA
/// degli scatti, non nel valore: il valore dice solo il verso (avanti / indietro). Chi legge il valore
/// come se fosse una velocità ottiene un piatto che striscia e non va mai indietro davvero.
///
/// Misurato sul registratore MIDI con una Hercules DJControl Instinct P8 (24/09/2026):
/// girata secca senza mano 280–345 scatti/s, mano appoggiata sopra 43–96 scatti/s.
/// Da lì la taratura: <see cref="TicksPerSecondAtNormalSpeed"/> = 120 scatti/s = velocità del disco.
/// </summary>
public sealed class JogMeter
{
    /// <summary>Scatti al secondo che corrispondono alla velocità normale del brano (1×).</summary>
    public const double TicksPerSecondAtNormalSpeed = 120;
    /// <summary>Quanto audio vale uno scatto a velocità normale (per la ricerca a deck fermo).</summary>
    public const double SecondsPerTick = 1.0 / TicksPerSecondAtNormalSpeed;

    /// <summary>Finestra su cui si misura: abbastanza lunga da non tremare, abbastanza corta da seguire la mano.</summary>
    private const double WindowSec = 0.06;
    /// <summary>Senza scatti per più di così il piatto è fermo (mano che tiene il disco).</summary>
    private const double StillSec = 0.09;

    private readonly Queue<(DateTime At, int Delta)> _ticks = new();
    private DateTime _last = DateTime.MinValue;

    /// <summary>Registra uno scatto (±1, o più se la console manda salti) e restituisce la velocità in "×".</summary>
    public double Add(int delta)
    {
        var now = DateTime.UtcNow;
        _last = now;
        _ticks.Enqueue((now, delta));
        Trim(now);
        return Speed(now);
    }

    /// <summary>Velocità adesso: 0 se il piatto ha smesso di mandare (disco tenuto fermo).</summary>
    public double Current()
    {
        var now = DateTime.UtcNow;
        if (_last == DateTime.MinValue || (now - _last).TotalSeconds > StillSec) return 0;
        Trim(now);
        return Speed(now);
    }

    public void Reset() { _ticks.Clear(); _last = DateTime.MinValue; }

    private void Trim(DateTime now)
    {
        while (_ticks.Count > 0 && (now - _ticks.Peek().At).TotalSeconds > WindowSec) _ticks.Dequeue();
    }

    private double Speed(DateTime now)
    {
        if (_ticks.Count == 0) return 0;
        int sum = 0;
        foreach (var t in _ticks) sum += t.Delta;
        // sulla finestra piena: scatti/s → volte la velocità normale
        return sum / WindowSec / TicksPerSecondAtNormalSpeed;
    }
}
