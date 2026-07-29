namespace Goop.Gameplay
{
    /// <summary>
    /// Canonical pose order for goop_character.fbx. Wheel/PoseIndex 1..18 maps to PoseNames[index-1];
    /// index 0 is locomotion (idle/walk/run). The animator states, the preview thumbnails
    /// (Resources/PosePreviews/pose_NN) and the wheel labels all follow this same order.
    ///
    /// PoseNames are the INTERNAL keys (animator states, clip names, previews) — never change these.
    /// DisplayNames are the user-facing labels shown in the pose wheel / UI, and can be reskinned freely.
    /// </summary>
    public static class PoseCatalog
    {
        public static readonly string[] PoseNames =
        {
            "A", "BackBend", "Bridge", "CrossLegged", "CrouchedFetal", "CurledUpSit",
            "FetalPose", "HandOnHip", "LayDown", "LeftHandUp", "MermaidSit", "OpenWide",
            "SideLying", "Sit", "Straight", "T", "Tree", "WideSquat"
        };

        /// <summary>User-facing labels, index-aligned with PoseNames.</summary>
        public static readonly string[] DisplayNames =
        {
            "Stand Awakening",        // A
            "Limbo Champion",         // BackBend
            "Crab Walk",              // Bridge
            "Criss-Cross Applesauce", // CrossLegged
            "Turtle Mode",            // CrouchedFetal
            "Potato Sack",            // CurledUpSit
            "Starfish (curled)",      // FetalPose
            "Menacing... (ゴゴゴゴ)",   // HandOnHip
            "Starfish",               // LayDown
            "Statue of Liberty",      // LeftHandUp
            "Little Mermaid",         // MermaidSit
            "Ta-Da!",                 // OpenWide
            "Nap Time",               // SideLying
            "Bench Warmer",           // Sit
            "Idle Statue",            // Straight
            "Airplane Mode",          // T
            "Flamingo",               // Tree
            "Wide Squat"              // WideSquat
        };

        public const string ClipPrefix = "ChameleonMan|Pose_";

        /// <summary>Display label for a wheel/PoseIndex (0 = no pose / locomotion).</summary>
        public static string Display(int poseIndex)
        {
            if (poseIndex <= 0) return "Idle";
            int i = poseIndex - 1;
            return (i >= 0 && i < DisplayNames.Length) ? DisplayNames[i] : $"Pose {poseIndex}";
        }
    }
}
