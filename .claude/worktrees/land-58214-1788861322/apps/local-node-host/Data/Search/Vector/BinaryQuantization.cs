using System;
using System.Collections.Generic;

namespace Harborline.Api.LocalNodeHost.Data.Search.Vector;

/// <summary>
/// Binary quantization for the KG vector index (ADR 0135 KG-search F3-lift amendment, Slice 1b) — the spike's
/// scale unlock (R-4: <b>24× smaller, 6–14× faster</b>; <c>bit</c> never breaches a 500 ms p95 through 1M
/// vectors). A 1024-dim float32 vector (4096 B) becomes a 1024-bit packed blob (128 B). It is the DEFAULT
/// vector encoding for the index.
/// </summary>
/// <remarks>
/// <para>
/// <b>The encoding</b> is sign-bit quantization: bit <c>i</c> = 1 iff <c>vector[i] &gt; 0</c> — the standard
/// binary-MRL scheme <c>sqlite-vec</c>'s <c>vec_quantize_binary</c> uses, so the same packed bytes feed the
/// real <c>vec0</c> bit-vector column. Distance between two binary codes is the <b>Hamming distance</b> (count
/// of differing bits), which approximates cosine rank for L2-normalized embeddings (BGE-M3 emits unit vectors).
/// </para>
/// <para>
/// Bit packing is LSB-first within each byte (bit <c>i</c> → byte <c>i/8</c>, bit-position <c>i%8</c>), matching
/// <c>sqlite-vec</c>'s layout so a blob produced here is byte-compatible with the native <c>vec0</c> path.
/// </para>
/// </remarks>
public static class BinaryQuantization
{
    /// <summary>
    /// Packs an L2-normalized embedding into its sign-bit binary code (LSB-first per byte). The vector length
    /// need not be a multiple of 8; the final byte is zero-padded in the high bits.
    /// </summary>
    public static byte[] Pack(IReadOnlyList<float> vector)
    {
        ArgumentNullException.ThrowIfNull(vector);
        var byteCount = (vector.Count + 7) / 8;
        var packed = new byte[byteCount];
        for (var i = 0; i < vector.Count; i++)
        {
            if (vector[i] > 0f)
            {
                packed[i >> 3] |= (byte)(1 << (i & 7));
            }
        }
        return packed;
    }

    /// <summary>
    /// The Hamming distance between two packed binary codes (count of differing bits). Both blobs MUST be the
    /// same length (same source dimension); a mismatch is a hard fault (G-5 dimension invariant).
    /// </summary>
    public static int Hamming(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length)
        {
            throw new ArgumentException(
                $"Binary codes differ in length ({a.Length} vs {b.Length}) — a dimension/model mismatch (G-5).");
        }
        var distance = 0;
        for (var i = 0; i < a.Length; i++)
        {
            distance += System.Numerics.BitOperations.PopCount((uint)(byte)(a[i] ^ b[i]));
        }
        return distance;
    }
}
