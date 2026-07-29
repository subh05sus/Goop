using System;
using Unity.Netcode;

namespace Goop.Paint
{
    /// <summary>
    /// One brush dab for VERTEX-COLOR painting: a center point in the skin's baked-local space, a brush
    /// radius, an RGB color, and material params (metallic/roughness). Every client colors the vertices
    /// within the radius of the center — no UVs, no texels. Compact + replicates via the stroke list.
    /// </summary>
    public struct PaintStroke : INetworkSerializable, IEquatable<PaintStroke>
    {
        public float Cx, Cy, Cz;   // center in baked-local space
        public float BrushSize;
        public byte R, G, B;
        public byte Metallic;
        public byte Roughness;

        public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
        {
            serializer.SerializeValue(ref Cx);
            serializer.SerializeValue(ref Cy);
            serializer.SerializeValue(ref Cz);
            serializer.SerializeValue(ref BrushSize);
            serializer.SerializeValue(ref R);
            serializer.SerializeValue(ref G);
            serializer.SerializeValue(ref B);
            serializer.SerializeValue(ref Metallic);
            serializer.SerializeValue(ref Roughness);
        }

        public bool Equals(PaintStroke other)
        {
            return Cx.Equals(other.Cx) && Cy.Equals(other.Cy) && Cz.Equals(other.Cz)
                   && BrushSize.Equals(other.BrushSize)
                   && R == other.R && G == other.G && B == other.B
                   && Metallic == other.Metallic && Roughness == other.Roughness;
        }

        public override bool Equals(object obj) => obj is PaintStroke other && Equals(other);
        public override int GetHashCode() => HashCode.Combine(Cx, Cy, Cz, BrushSize, R, G, B, Metallic);
    }
}
