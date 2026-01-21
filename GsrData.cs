using System;
using System.Buffers.Binary;
using System.IO;

namespace NeuroBureau.Experiment;

public struct GsrData
{
    // Vala/C ABI на x64: 6*8 + 1 + 32 + padding до кратности 8 = 88
    public const int Size = 88;

    public double time;          // seconds (monotonic)
    public double heart_rate;
    public double gsr_sr;        // skin resistance
    public double gsr_sc;        // skin conductance
    public double gsr_range;
    public double ppg_a13;

    public byte battery;         // если нет — 0

    public void WriteTo(Stream s)
    {
        Span<byte> buf = stackalloc byte[Size];
        int o = 0;

        WriteD(buf, ref o, time);
        WriteD(buf, ref o, heart_rate);
        WriteD(buf, ref o, gsr_sr);
        WriteD(buf, ref o, gsr_sc);
        WriteD(buf, ref o, gsr_range);
        WriteD(buf, ref o, ppg_a13);

        buf[o++] = battery;

        // reserved[32]
        buf.Slice(o, 32).Clear(); o += 32;

        // padding до 88
        buf.Slice(o).Clear();

        s.Write(buf);
    }

    private static void WriteD(Span<byte> buf, ref int o, double v)
    {
        BinaryPrimitives.WriteInt64LittleEndian(buf.Slice(o, 8), BitConverter.DoubleToInt64Bits(v));
        o += 8;
    }
}
