using System;
using Unity.Netcode;

namespace Goop.Paint
{
    /// <summary>
    /// One brush dab: a UV-space center on the character's CLEAN unwrap, a brush radius (UV), an RGB
    /// color, and material params (metallic/roughness). Painted into a high-res texture — sharp, because
    /// the paint mesh's UVs are an even, non-overlapping runtime-generated unwrap. Replicates via the list.
    /// </summary>
    public struct PaintStroke : INetworkSerializable, IEquatable<PaintStroke>
    {
        public float U, V;
        public float BrushSize;
        public byte R, G, B;
        public byte Metallic;
        public byte Roughness;

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
        public override int GetHashCode() => HashCode.Combine(U, V, BrushSize, R, G, B, Metallic);
    }
}
