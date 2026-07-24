namespace Goop.Gameplay
{
    /// <summary>
    /// Canonical pose order for goop_character.fbx. Wheel/PoseIndex 1..18 maps to PoseNames[index-1];
    /// index 0 is locomotion (idle/walk/run). The animator states, the preview thumbnails
    /// (Resources/PosePreviews/pose_NN) and the wheel labels all follow this same order.
    /// </summary>
    public static class PoseCatalog
    {
        public static readonly string[] PoseNames =
        {
            "A", "BackBend", "Bridge", "CrossLegged", "CrouchedFetal", "CurledUpSit",
            "FetalPose", "HandOnHip", "LayDown", "LeftHandUp", "MermaidSit", "OpenWide",
            "SideLying", "Sit", "Straight", "T", "Tree", "WideSquat"
        };

        public const string ClipPrefix = "ChameleonMan|Pose_";
    }
}
