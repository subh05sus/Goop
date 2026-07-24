using Goop.Paint;
using UnityEngine;

namespace Goop.UI
{
    /// <summary>
    /// Paint-mode palette (PRD 7.1): 12 quick-color presets, current color + brush size readout.
    /// Shown only while paint mode (F) is active — PaintModeController toggles Visible.
    /// Eyedropper is Space (3D world sampling, handled in PaintModeController); brush size is
    /// hold-RMB + horizontal drag. Full color-wheel/HSV picker is a later polish pass.
    /// </summary>
    public class PaletteUI : MonoBehaviour
    {
        private static readonly Color32[] PresetColors =
        {
            new(220, 40, 40, 255), new(230, 120, 30, 255), new(230, 200, 30, 255), new(140, 200, 40, 255),
            new(40, 170, 80, 255), new(40, 180, 180, 255), new(40, 110, 220, 255), new(90, 60, 200, 255),
            new(170, 50, 200, 255), new(230, 230, 230, 255), new(120, 120, 120, 255), new(30, 30, 30, 255),
        };

        public bool Visible { get; set; }

        private PaintableSkin _skin;

        public void Initialize(PaintableSkin skin)
        {
            _skin = skin;
        }

        private void OnGUI()
        {
            if (!Visible || _skin == null) return;

            GUILayout.BeginArea(new Rect(10, 70, 300, 150), GUI.skin.box);
            GUILayout.Label("PAINT MODE  (F to exit)");
            GUILayout.Label("LMB paint · RMB drag = brush size · MMB drag = orbit · Space = eyedropper");

            GUILayout.BeginHorizontal();
            for (int i = 0; i < PresetColors.Length; i++)
            {
                Color32 c = PresetColors[i];
                var prevColor = GUI.backgroundColor;
                GUI.backgroundColor = c;
                if (GUILayout.Button("", GUILayout.Width(18), GUILayout.Height(18)))
                {
                    _skin.CurrentColor = c;
                }
                GUI.backgroundColor = prevColor;
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            var prev = GUI.backgroundColor;
            GUI.backgroundColor = _skin.CurrentColor;
            GUILayout.Box("", GUILayout.Width(28), GUILayout.Height(28));
            GUI.backgroundColor = prev;
            GUILayout.Label($"Brush: {Mathf.RoundToInt(_skin.BrushSize / _skin.MaxBrush * 100f)}%");
            GUILayout.EndHorizontal();

            GUILayout.Label($"Strokes: {_skin.Strokes.Count}/{PaintableSkin.MaxStrokesPerRound}");
            GUILayout.EndArea();
        }
    }
}
