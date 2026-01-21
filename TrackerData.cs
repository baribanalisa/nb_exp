using System;
using System.Buffers.Binary;
using System.IO;

namespace NeuroBureau.Experiment;

[Flags]
public enum TrackerDataValidity : int
{
    COORD_VALID = 1,
    LEFT_PUPIL_3D_COORD_VALID = 2,
    RIGHT_PUPIL_3D_COORD_VALID = 4,
    LEFT_PUPIL_SIZE_VALID = 8,
    RIGHT_PUPIL_SIZE_VALID = 16,
    LEFT_PUPIL_COORD_VALID = 32,
    RIGHT_PUPIL_COORD_VALID = 64,
    LEFT_OPEN_VALID = 128,
    RIGHT_OPEN_VALID = 256,
}

public struct TrackerData
{
    public const int Size = 84;

    public int valid;
    public float time;
    public float x, y, z;

    public float lp, rp;

    public float leyex, leyey, leyez;
    public float reyex, reyey, reyez;

    public float rx, ry;
    public float lx, ly;

    public float lopen, ropen;

    // В примере бинарников последние 8 байт всегда == два float(-1)
    private static readonly byte[] Reserved8 = new byte[] { 0x00, 0x00, 0x80, 0xBF, 0x00, 0x00, 0x80, 0xBF };

    public void WriteTo(Stream s)
    {
        Span<byte> buf = stackalloc byte[Size];
        int o = 0;

        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(o, 4), valid); o += 4;

        WriteF(buf, ref o, time);
        WriteF(buf, ref o, x);
        WriteF(buf, ref o, y);
        WriteF(buf, ref o, z);

        WriteF(buf, ref o, lp);
        WriteF(buf, ref o, rp);

        WriteF(buf, ref o, leyex);
        WriteF(buf, ref o, leyey);
        WriteF(buf, ref o, leyez);

        WriteF(buf, ref o, reyex);
        WriteF(buf, ref o, reyey);
        WriteF(buf, ref o, reyez);

        WriteF(buf, ref o, rx);
        WriteF(buf, ref o, ry);

        WriteF(buf, ref o, lx);
        WriteF(buf, ref o, ly);

        WriteF(buf, ref o, lopen);
        WriteF(buf, ref o, ropen);

        Reserved8.CopyTo(buf.Slice(o, 8)); o += 8;

        s.Write(buf);
    }

    private static void WriteF(Span<byte> buf, ref int o, float v)
    {
        BinaryPrimitives.WriteInt32LittleEndian(buf.Slice(o, 4), BitConverter.SingleToInt32Bits(v));
        o += 4;
    }
}
