using Goop.Paint;
using UnityEngine;

namespace Goop.UI
{
    /// <summary>
    /// Full paint-mode palette: HSV hue wheel (click/drag the ring to pick hue, the inner square to pick
    /// saturation/value), 12 quick swatches, live brush-size slider, current color readout.
    /// PaintModeController toggles Visible; PointerOverPaintUI stops brush strokes from firing while the
    /// cursor is on the panel. Eyedropper (Space) writes back through SetColor so the wheel follows it.
    /// </summary>
    public class PaletteUI : MonoBehaviour
    {
        private static readonly Color32[] PresetColors =
        {
            new(220, 40, 40, 255), new(230, 120, 30, 255), new(230, 200, 30, 255), new(140, 200, 40, 255),
            new(40, 170, 80, 255), new(40, 180, 180, 255), new(40, 110, 220, 255), new(90, 60, 200, 255),
            new(170, 50, 200, 255), new(230, 230, 230, 255), new(120, 120, 120, 255), new(30, 30, 30, 255),
        };

        /// <summary>True while the cursor is over the palette panel — PaintableSkin skips painting then.</summary>
        public static bool PointerOverPaintUI;

        public bool Visible { get; set; }

        private const int WheelSize = 170;
        private const float RingOuter = 82f;
        private const float RingInner = 62f;
        private const int SvSize = 84;

        private PaintableSkin _skin;
        private float _hue, _sat = 0.8f, _val = 0.9f;
        private Texture2D _wheelTex;
        private Texture2D _svTex;
        private float _svTexHue = -1f;
        private bool _draggingRing;
        private bool _draggingSv;

        private Rect PanelRect => new(10, 70, 340, 470);

        public void Initialize(PaintableSkin skin)
        {
            _skin = skin;
            Color.RGBToHSV(skin.CurrentColor, out _hue, out _sat, out _val);
            ApplyHsv();
        }

        /// <summary>External color set (eyedropper) — keeps the wheel in sync.</summary>
        public void SetColor(Color32 color)
        {
            Color.RGBToHSV(color, out _hue, out _sat, out _val);
            ApplyHsv();
        }

        private void ApplyHsv()
        {
            if (_skin != null) _skin.CurrentColor = Color.HSVToRGB(_hue, _sat, _val);
        }

        private void Update()
        {
            if (!Visible)
            {
                PointerOverPaintUI = false;
                _draggingRing = _draggingSv = false;
                return;
            }
            // Input-system mouse is y-up; GUI is y-down.
            var mouse = UnityEngine.InputSystem.Mouse.current;
            if (mouse == null) return;
            Vector2 guiPos = new(mouse.position.ReadValue().x, Screen.height - mouse.position.ReadValue().y);
            PointerOverPaintUI = PanelRect.Contains(guiPos);

            Vector2 wheelCenter = WheelCenterGui();
            bool lmb = mouse.leftButton.isPressed;
            bool lmbDown = mouse.leftButton.wasPressedThisFrame;

            if (!lmb)
            {
                _draggingRing = _draggingSv = false;
                return;
            }

            Vector2 local = guiPos - wheelCenter;
            float dist = local.magnitude;

            if (lmbDown)
            {
                if (dist >= RingInner && dist <= RingOuter) _draggingRing = true;
                else if (Mathf.Abs(local.x) <= SvSize / 2f && Mathf.Abs(local.y) <= SvSize / 2f) _draggingSv = true;
            }

            if (_draggingRing)
            {
                // GUI y-down: negate y so hue runs counter-clockwise from the right, matching the texture.
                _hue = Mathf.Repeat(Mathf.Atan2(-local.y, local.x) / (2f * Mathf.PI), 1f);
                ApplyHsv();
            }
            else if (_draggingSv)
            {
                _sat = Mathf.Clamp01(local.x / SvSize + 0.5f);
                _val = Mathf.Clamp01(-local.y / SvSize + 0.5f); // GUI y-down: top = bright
                ApplyHsv();
            }
        }

        private Vector2 WheelCenterGui()
        {
            return new Vector2(PanelRect.x + PanelRect.width / 2f, PanelRect.y + 46f + WheelSize / 2f);
        }

        private void EnsureTextures()
        {
            if (_wheelTex == null)
            {
                _wheelTex = new Texture2D(WheelSize, WheelSize, TextureFormat.RGBA32, false);
                float c = WheelSize / 2f;
                var px = new Color32[WheelSize * WheelSize];
                for (int y = 0; y < WheelSize; y++)
                {
                    for (int x = 0; x < WheelSize; x++)
                    {
                        float dx = x - c, dy = y - c;
                        float d = Mathf.Sqrt(dx * dx + dy * dy);
                        if (d >= RingInner && d <= RingOuter)
                        {
                            float hue = Mathf.Repeat(Mathf.Atan2(dy, dx) / (2f * Mathf.PI), 1f);
                            px[y * WheelSize + x] = Color.HSVToRGB(hue, 1f, 1f);
                        }
                        else
                        {
                            px[y * WheelSize + x] = new Color32(0, 0, 0, 0);
                        }
                    }
                }
                _wheelTex.SetPixels32(px);
                _wheelTex.Apply();
            }

            if (_svTex == null || !Mathf.Approximately(_svTexHue, _hue))
            {
                if (_svTex == null) _svTex = new Texture2D(SvSize, SvSize, TextureFormat.RGBA32, false);
                var px = new Color32[SvSize * SvSize];
                for (int y = 0; y < SvSize; y++)
                {
                    for (int x = 0; x < SvSize; x++)
                    {
                        // Texture rows are bottom-up; drawn upright so top row = value 1.
                        px[y * SvSize + x] = Color.HSVToRGB(_hue, (float)x / (SvSize - 1), (float)y / (SvSize - 1));
                    }
                }
                _svTex.SetPixels32(px);
                _svTex.Apply();
                _svTexHue = _hue;
            }
        }

        private void OnDestroy()
        {
            if (_wheelTex != null) Destroy(_wheelTex);
            if (_svTex != null) Destroy(_svTex);
            PointerOverPaintUI = false;
        }

        private void OnGUI()
        {
            if (!Visible || _skin == null) return;
            EnsureTextures();

            Rect panel = PanelRect;
            GUI.Box(panel, "");
            GUILayout.BeginArea(panel);
            GUILayout.Label("PAINT MODE  (F to exit)");
            GUILayout.Label("LMB paint · MMB drag orbit · Space eyedropper");
            GUILayout.EndArea();

            // Hue wheel + SV square (drawn manually, centered)
            Vector2 center = WheelCenterGui();
            GUI.DrawTexture(new Rect(center.x - WheelSize / 2f, center.y - WheelSize / 2f, WheelSize, WheelSize), _wheelTex);
            GUI.DrawTexture(new Rect(center.x - SvSize / 2f, center.y - SvSize / 2f, SvSize, SvSize), _svTex,
                ScaleMode.ScaleToFit, false);

            // Selection markers
            var prev = GUI.color;
            float hueAngle = _hue * 2f * Mathf.PI;
            Vector2 ringPos = center + new Vector2(Mathf.Cos(hueAngle), -Mathf.Sin(hueAngle)) * ((RingInner + RingOuter) / 2f);
            GUI.color = Color.black;
            GUI.Box(new Rect(ringPos.x - 5, ringPos.y - 5, 10, 10), "");
            Vector2 svPos = center + new Vector2((_sat - 0.5f) * SvSize, (0.5f - _val) * SvSize);
            GUI.Box(new Rect(svPos.x - 4, svPos.y - 4, 8, 8), "");
            GUI.color = prev;

            // Below the wheel: swatches, brush, current color
            float y = center.y + WheelSize / 2f + 8f;
            GUILayout.BeginArea(new Rect(panel.x + 8, y, panel.width - 16, panel.yMax - y - 8));

            GUILayout.BeginHorizontal();
            for (int i = 0; i < PresetColors.Length; i++)
            {
                Color32 c = PresetColors[i];
                var pc = GUI.backgroundColor;
                GUI.backgroundColor = c;
                if (GUILayout.Button("", GUILayout.Width(20), GUILayout.Height(20)))
                {
                    SetColor(c);
                }
                GUI.backgroundColor = pc;
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label($"Brush size: {Mathf.RoundToInt(_skin.BrushSize / _skin.MaxBrush * 100f)}%  (or hold RMB + drag)");
            _skin.BrushSize = GUILayout.HorizontalSlider(_skin.BrushSize, 0.005f, _skin.MaxBrush);

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = _skin.CurrentColor;
            GUILayout.Box("", GUILayout.Width(34), GUILayout.Height(34));
            GUI.backgroundColor = prevBg;
            Color32 cur = _skin.CurrentColor;
            GUILayout.Label($"#{cur.r:X2}{cur.g:X2}{cur.b:X2}\nStrokes: {_skin.Strokes.Count}/{PaintableSkin.MaxStrokesPerRound}");
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
        }
    }
}
