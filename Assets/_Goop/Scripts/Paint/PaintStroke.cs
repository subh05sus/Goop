using System;
using Unity.Netcode;

namespace Goop.Paint
{
    /// <summary>
    /// A single brush dab on a player's paintable UV texture (PRD 7.1: compact stroke list, not pixels).
    /// Carries material params alongside color — metallic/roughness are as load-bearing for a disguise as
    /// hue (Paint doc §2/§6), so they replicate per-stroke and paint into a metallic/gloss map.
    /// </summary>
    public struct PaintStroke : INetworkSerializable, IEquatable<PaintStroke>
    {
        public float U;
        public float V;
        public float BrushSize;
        public byte R, G, B;
        public byte Metallic;   // 0..255 -> 0..1
        public byte Roughness;  // 0..255 -> 0..1 (smoothness = 1 - roughness)

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref U);
            serializer.SerializeValue(ref V);
            serializer.SerializeValue(ref BrushSize);
            serializer.SerializeValue(ref R);
            serializer.SerializeValue(ref G);
            serializer.SerializeValue(ref B);
            serializer.SerializeValue(ref Metallic);
            serializer.SerializeValue(ref Roughness);
        }

        public bool Equals(PaintStroke other)
        {
            return U.Equals(other.U) && V.Equals(other.V) && BrushSize.Equals(other.BrushSize)
                   && R == other.R && G == other.G && B == other.B
                   && Metallic == other.Metallic && Roughness == other.Roughness;
        }

        public override bool Equals(object obj) => obj is PaintStroke other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(U, V, BrushSize, R, G, B, Metallic, Roughness);
    }
}
