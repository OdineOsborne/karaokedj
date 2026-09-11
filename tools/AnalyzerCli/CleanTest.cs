using KaraokeDJ.Services;

public static class CleanTest
{
    public static void Run()
    {
        var cases = new (string a, string t)[]
        {
            ("AC⧸DC", "AC⧸DC - Back In Black (Official 4K Video)"),
            ("ABKCOVEVO", "The Rolling Stones - (I Can't Get No) Satisfaction (Official Lyric Video)"),
            ("Lucio Battisti", "Lucio Battisti - Lucio Battisti - 7 e 40 (Official Audio)"),
            ("Karaoke Academy", "Domenico Modugno  - Nel blu dipinto di blu ＂Volare＂  ( Versione Karaoke Academy Italia)"),
            ("Black Sabbath", "Paranoid (2012 Remaster)"),
            ("Brian Martens Music", "Iron Maiden - Run To The Hills - Remastered"),
            ("Joan Jett and the Blackhearts", "I Love Rock 'N Roll - Joan Jett & the Blackhearts (Official Music Video) [Remastered HD]"),
            ("Enhanced Music Videos", "AC⧸DC - It's A Long Way To The Top [Official Music Video], Full HD (Remaster, Resync and Upscale)"),
            ("Guns N' Roses", "Guns N' Roses - Welcome To The Jungle"),
            ("", "Emma - ANTIDROGA (feat. Fabri Fibra)"),
            ("Test Artist", "Tono di prova"),
        };
        foreach (var (a, t) in cases)
        {
            var (na, nt) = TitleCleaner.Clean(a, t);
            Console.WriteLine($"[{a}] {t}\n   → [{na}] {nt}");
        }
    }
}
