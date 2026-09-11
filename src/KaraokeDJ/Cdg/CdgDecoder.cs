namespace KaraokeDJ.Cdg;

/// <summary>
/// Decoder CD+G (grafica karaoke). Schermo 300×216, 16 colori, 300 pacchetti/secondo.
/// Chiama <see cref="RenderAt"/> con il tempo audio per ottenere il frame corrente in BGRA.
/// </summary>
public sealed class CdgDecoder
{
    public const int Width = 300;
    public const int Height = 216;
    public const int PacketSize = 24;
    public const double PacketsPerSecond = 300.0;

    private const int CdgCommand = 0x09;
    private const int MemoryPreset = 1;
    private const int BorderPreset = 2;
    private const int TileBlock = 6;
    private const int ScrollPreset = 20;
    private const int ScrollCopy = 24;
    private const int DefineTransparent = 28;
    private const int LoadColorTableLow = 30;
    private const int LoadColorTableHigh = 31;
    private const int TileBlockXor = 38;

    private readonly byte[] _data;
    private readonly byte[] _pixels = new byte[Width * Height];     // indice colore per pixel
    private readonly uint[] _palette = new uint[16];                // BGRA
    private readonly byte[] _scratch = new byte[Width * Height];
    private int _packetIndex;
    private int _hOffset, _vOffset;
    private int _borderColor;

    public CdgDecoder(byte[] data)
    {
        _data = data;
        PacketCount = data.Length / PacketSize;
        Reset();
    }

    public int PacketCount { get; }
    public double DurationSec => PacketCount / PacketsPerSecond;
    public byte[] Frame { get; } = new byte[Width * Height * 4];
    public int FrameVersion { get; private set; }

    public void Reset()
    {
        Array.Clear(_pixels);
        Array.Clear(_palette);
        _packetIndex = 0;
        _hOffset = _vOffset = 0;
        _borderColor = 0;
    }

    /// <summary>Avanza (o riavvolge) fino al tempo indicato e aggiorna <see cref="Frame"/>. Ritorna true se il frame è cambiato.</summary>
    public bool RenderAt(double seconds)
    {
        int target = (int)Math.Clamp(seconds * PacketsPerSecond, 0, PacketCount);
        if (target < _packetIndex) Reset();
        if (target == _packetIndex && FrameVersion > 0) return false;

        for (; _packetIndex < target; _packetIndex++)
            ProcessPacket(_packetIndex);

        Compose();
        FrameVersion++;
        return true;
    }

    private void ProcessPacket(int i)
    {
        int p = i * PacketSize;
        if ((_data[p] & 0x3F) != CdgCommand) return;
        int instr = _data[p + 1] & 0x3F;
        var d = _data.AsSpan(p + 4, 16);

        switch (instr)
        {
            case MemoryPreset:
                if ((d[1] & 0x0F) == 0) Array.Fill(_pixels, (byte)(d[0] & 0x0F));
                break;
            case BorderPreset:
                _borderColor = d[0] & 0x0F;
                FillBorder(_borderColor);
                break;
            case TileBlock:
                DrawTile(d, false);
                break;
            case TileBlockXor:
                DrawTile(d, true);
                break;
            case ScrollPreset:
                Scroll(d, false);
                break;
            case ScrollCopy:
                Scroll(d, true);
                break;
            case DefineTransparent:
                break;
            case LoadColorTableLow:
                LoadPalette(d, 0);
                break;
            case LoadColorTableHigh:
                LoadPalette(d, 8);
                break;
        }
    }

    private void FillBorder(int color)
    {
        byte c = (byte)color;
        for (int y = 0; y < Height; y++)
        {
            bool edgeRow = y < 12 || y >= Height - 12;
            for (int x = 0; x < Width; x++)
            {
                if (edgeRow || x < 6 || x >= Width - 6)
                    _pixels[y * Width + x] = c;
            }
        }
    }

    private void DrawTile(ReadOnlySpan<byte> d, bool xor)
    {
        int color0 = d[0] & 0x0F;
        int color1 = d[1] & 0x0F;
        int row = d[2] & 0x1F;
        int col = d[3] & 0x3F;
        int x0 = col * 6;
        int y0 = row * 12;
        if (x0 + 6 > Width || y0 + 12 > Height) return;

        for (int y = 0; y < 12; y++)
        {
            int bits = d[4 + y] & 0x3F;
            for (int x = 0; x < 6; x++)
            {
                int bit = (bits >> (5 - x)) & 1;
                int color = bit == 1 ? color1 : color0;
                int idx = (y0 + y) * Width + (x0 + x);
                if (xor) _pixels[idx] = (byte)(_pixels[idx] ^ color);
                else _pixels[idx] = (byte)color;
            }
        }
    }

    private void Scroll(ReadOnlySpan<byte> d, bool copy)
    {
        int color = d[0] & 0x0F;
        int hScroll = d[1] & 0x3F;
        int vScroll = d[2] & 0x3F;
        int hCmd = (hScroll & 0x30) >> 4;
        int vCmd = (vScroll & 0x30) >> 4;
        _hOffset = hScroll & 0x07;
        _vOffset = vScroll & 0x0F;

        // Spec CD+G: 1 = il contenuto si sposta a destra/giù, 2 = a sinistra/su
        int dx = hCmd == 1 ? 6 : hCmd == 2 ? -6 : 0;
        int dy = vCmd == 1 ? 12 : vCmd == 2 ? -12 : 0;
        if (dx == 0 && dy == 0) return;

        Array.Copy(_pixels, _scratch, _pixels.Length);
        for (int y = 0; y < Height; y++)
        {
            int srcY = y - dy;
            for (int x = 0; x < Width; x++)
            {
                int srcX = x - dx;
                byte v;
                if (srcX >= 0 && srcX < Width && srcY >= 0 && srcY < Height)
                    v = _scratch[srcY * Width + srcX];
                else if (copy)
                    v = _scratch[((srcY + Height) % Height) * Width + ((srcX + Width) % Width)];
                else
                    v = (byte)color;
                _pixels[y * Width + x] = v;
            }
        }
    }

    private void LoadPalette(ReadOnlySpan<byte> d, int start)
    {
        for (int i = 0; i < 8; i++)
        {
            int b0 = d[2 * i] & 0x3F;
            int b1 = d[2 * i + 1] & 0x3F;
            int r = (b0 & 0x3C) >> 2;
            int g = ((b0 & 0x03) << 2) | ((b1 & 0x30) >> 4);
            int b = b1 & 0x0F;
            uint bgra = 0xFF000000u | (uint)(r * 17) << 16 | (uint)(g * 17) << 8 | (uint)(b * 17);
            _palette[start + i] = bgra;
        }
    }

    private void Compose()
    {
        var frame = Frame;
        // Applica l'offset di scroll fine (hOffset/vOffset) ricampionando l'origine
        for (int y = 0; y < Height; y++)
        {
            int srcY = Math.Clamp(y + _vOffset, 0, Height - 1);
            for (int x = 0; x < Width; x++)
            {
                int srcX = Math.Clamp(x + _hOffset, 0, Width - 1);
                uint c = _palette[_pixels[srcY * Width + srcX]];
                int o = (y * Width + x) * 4;
                frame[o] = (byte)(c & 0xFF);
                frame[o + 1] = (byte)((c >> 8) & 0xFF);
                frame[o + 2] = (byte)((c >> 16) & 0xFF);
                frame[o + 3] = 0xFF;
            }
        }
    }
}
