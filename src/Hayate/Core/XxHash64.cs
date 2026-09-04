using System;
using System.Buffers.Binary;
using System.Numerics;

namespace Hayate.Core;

/// <summary>
/// xxHash64 のストリーミング実装。
/// 検証用のチェックサムに使う。暗号学的用途ではないが、
/// コピー中の化けを検出する目的では十分に強く、SHA-256 より桁違いに速い。
/// 外部パッケージに依存しないよう自前実装している。
/// </summary>
public sealed class XxHash64
{
    private const ulong P1 = 11400714785074694791UL;
    private const ulong P2 = 14029467366897019727UL;
    private const ulong P3 = 1609587929392839161UL;
    private const ulong P4 = 9650029242287828579UL;
    private const ulong P5 = 2870177450012600261UL;

    private readonly ulong _seed;
    private readonly byte[] _tail = new byte[32];
    private ulong _v1, _v2, _v3, _v4;
    private int _tailLen;
    private ulong _total;

    public XxHash64(ulong seed = 0)
    {
        _seed = seed;
        Reset();
    }

    public void Reset()
    {
        _v1 = _seed + P1 + P2;
        _v2 = _seed + P2;
        _v3 = _seed;
        _v4 = _seed - P1;
        _tailLen = 0;
        _total = 0;
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        _total += (ulong)data.Length;

        if (_tailLen > 0)
        {
            int need = 32 - _tailLen;
            if (data.Length < need)
            {
                data.CopyTo(_tail.AsSpan(_tailLen));
                _tailLen += data.Length;
                return;
            }
            data[..need].CopyTo(_tail.AsSpan(_tailLen));
            Stripe(_tail);
            _tailLen = 0;
            data = data[need..];
        }

        while (data.Length >= 32)
        {
            Stripe(data[..32]);
            data = data[32..];
        }

        if (data.Length > 0)
        {
            data.CopyTo(_tail);
            _tailLen = data.Length;
        }
    }

    private void Stripe(ReadOnlySpan<byte> s)
    {
        _v1 = Round(_v1, BinaryPrimitives.ReadUInt64LittleEndian(s));
        _v2 = Round(_v2, BinaryPrimitives.ReadUInt64LittleEndian(s[8..]));
        _v3 = Round(_v3, BinaryPrimitives.ReadUInt64LittleEndian(s[16..]));
        _v4 = Round(_v4, BinaryPrimitives.ReadUInt64LittleEndian(s[24..]));
    }

    private static ulong Round(ulong acc, ulong input)
        => BitOperations.RotateLeft(acc + input * P2, 31) * P1;

    private static ulong MergeRound(ulong acc, ulong val)
    {
        val = Round(0, val);
        acc ^= val;
        return acc * P1 + P4;
    }

    public ulong Finish()
    {
        ulong h;
        if (_total >= 32)
        {
            h = BitOperations.RotateLeft(_v1, 1)
              + BitOperations.RotateLeft(_v2, 7)
              + BitOperations.RotateLeft(_v3, 12)
              + BitOperations.RotateLeft(_v4, 18);
            h = MergeRound(h, _v1);
            h = MergeRound(h, _v2);
            h = MergeRound(h, _v3);
            h = MergeRound(h, _v4);
        }
        else
        {
            h = _seed + P5;
        }

        h += _total;

        ReadOnlySpan<byte> rest = _tail.AsSpan(0, _tailLen);
        while (rest.Length >= 8)
        {
            ulong k = Round(0, BinaryPrimitives.ReadUInt64LittleEndian(rest));
            h ^= k;
            h = BitOperations.RotateLeft(h, 27) * P1 + P4;
            rest = rest[8..];
        }
        if (rest.Length >= 4)
        {
            h ^= BinaryPrimitives.ReadUInt32LittleEndian(rest) * P1;
            h = BitOperations.RotateLeft(h, 23) * P2 + P3;
            rest = rest[4..];
        }
        foreach (byte b in rest)
        {
            h ^= b * P5;
            h = BitOperations.RotateLeft(h, 11) * P1;
        }

        h ^= h >> 33;
        h *= P2;
        h ^= h >> 29;
        h *= P3;
        h ^= h >> 32;
        return h;
    }
}
