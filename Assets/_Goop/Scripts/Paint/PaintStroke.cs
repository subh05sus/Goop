using System;
using Unity.Netcode;

namespace Goop.Paint
{
    /// <summary>A single brush dab on a player's paintable UV texture (PRD 7.1: compact stroke list, not pixels).</summary>
    public struct PaintStroke : INetworkSerializable, IEquatable<PaintStroke>
    {
        public float U;
        public float V;
        public float BrushSize;
        public byte R, G, B;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref U);
            serializer.SerializeValue(ref V);
            serializer.SerializeValue(ref BrushSize);
            serializer.SerializeValue(ref R);
            serializer.SerializeValue(ref G);
            serializer.SerializeValue(ref B);
        }

        public bool Equals(PaintStroke other)
        {
            return U.Equals(other.U) && V.Equals(other.V) && BrushSize.Equals(other.BrushSize)
                   && R == other.R && G == other.G && B == other.B;
        }

        public override bool Equals(object obj) => obj is PaintStroke other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(U, V, BrushSize, R, G, B);
    }
}
