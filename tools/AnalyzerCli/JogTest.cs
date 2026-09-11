using KaraokeDJ.Audio;
using KaraokeDJ.Models;

/// <summary>Esercita il motore jog (scratch/reverse/spin/slow) su un file e riporta livello e posizione per fase.</summary>
public static class JogTest
{
    public static void Run(string path)
    {
        var deck = new Deck("T");
        deck.Load(new Track { FilePath = path, Title = "test" }, path);
        deck.Play();
        var buf = new float[4410 * 2];
        void Phase(string name, double secs)
        {
            int blocks = (int)(secs * 10);
            float peak = 0; int nonzero = 0;
            for (int b = 0; b < blocks; b++)
            {
                int n = deck.Read(buf, 0, buf.Length);
                for (int i = 0; i < n; i++) { float a = Math.Abs(buf[i]); if (a > peak) peak = a; if (a > 0.001f) nonzero++; }
            }
            Console.WriteLine($"{name,-22} pos {deck.PositionSec,6:0.00}s  peak {peak:0.00}  attivo {100.0 * nonzero / (blocks * buf.Length):0}%  playing={deck.IsPlaying} jog={deck.JogActive}  {deck.JogDebug}");
        }
        Phase("play 2s", 2);
        deck.JogStart(); deck.JogRate(-1, 0.05); Phase("reverse 1s", 1);
        deck.JogRate(2.5, 0.05); Phase("fast fwd 1s", 1);
        deck.JogRate(0, 0.02); Phase("hold 0.5s", 0.5);
        deck.JogRate(1, 0.02); Phase("scratch fwd 0.5s", 0.5);
        deck.JogEnd(0.1); Phase("release 1s", 1);
        Phase("play 1s", 1);
        deck.Spin(3.5, 0.7); Phase("spin fwd 1.5s", 1.5);
        deck.Spin(-3.5, 0.7); Phase("spin back 1.5s", 1.5);
        deck.JogStart(); deck.JogRate(0, 0.9); Phase("slow hold 1.5s", 1.5);
        deck.JogEnd(0.12); Phase("slow release 1s", 1);
        deck.Pause(); deck.JogStart(); deck.JogRate(-1, 0.05); Phase("scratch paused 0.5s", 0.5);
        deck.JogEnd(0.05); Phase("release→pausa 0.5s", 0.5);
        deck.Eject();
    }
}
