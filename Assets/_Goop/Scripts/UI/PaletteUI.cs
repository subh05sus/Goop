using Goop.Paint;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Goop.UI
{
    /// <summary>
    /// Full paint-tool palette (Paint doc §2): hue wheel + SV square, RGB sliders, HSV sliders,
    /// metallic + roughness sliders, 12 preset swatches, 6 per-map SAVED swatches (PlayerPrefs, keyed by
    /// scene name), brush size, Undo / Clear, and the cast-shadow toggle. PaintModeController toggles
    /// Visible; PointerOverPaintUI stops brush strokes while the cursor is on the panel. The eyedropper
    /// (Space) writes back through SetColor so the whole panel follows a sample.
    /// </summary>
    public class PaletteUI : MonoBehaviour
    {
        private static readonly Color32[] PresetColors =
        {
            new(220, 40, 40, 255), new(230, 120, 30, 255), new(230, 200, 30, 255), new(140, 200, 40, 255),
            new(40, 170, 80, 255), new(40, 180, 180, 255), new(40, 110, 220, 255), new(90, 60, 200, 255),
            new(170, 50, 200, 255), new(230, 230, 230, 255), new(120, 120, 120, 255), new(30, 30, 30, 255),
        };

        private const int SavedSlots = 6;

        /// <summary>True while the cursor is over the palette panel — PaintableSkin skips painting then.</summary>
        public static bool PointerOverPaintUI;

        public bool Visible { get; set; }

        private const int WheelSize = 150;
        private const float RingOuter = 72f;
        private const float RingInner = 54f;
        private const int SvSize = 72;

        private PaintableSkin _skin;
        private float _hue, _sat = 0.8f, _val = 0.9f;
        private Texture2D _wheelTex;
        private Texture2D _svTex;
        private float _svTexHue = -1f;
        private bool _draggingRing;
        private bool _draggingSv;
        private Color32?[] _saved = new Color32?[SavedSlots];

        private Rect PanelRect => new(10, 40, 350, Mathf.Min(660, Screen.height - 60));

        private string SaveKey => $"GoopPalette_{SceneManager.GetActiveScene().name}";

        public void Initialize(PaintableSkin skin)
        {
            _skin = skin;
            Color.RGBToHSV(skin.CurrentColor, out _hue, out _sat, out _val);
            LoadSavedSwatches();
            ApplyHsv();
        }

        /// <summary>External color set (eyedropper) — keeps the whole panel in sync.</summary>
        public void SetColor(Color32 color)
        {
            Color.RGBToHSV(color, out _hue, out _sat, out _val);
            ApplyHsv();
        }

        private void ApplyHsv()
        {
            if (_skin != null) _skin.CurrentColor = Color.HSVToRGB(_hue, _sat, _val);
        }

        // ---------- per-map saved swatches (Paint doc §5.6) ----------

        private void LoadSavedSwatches()
        {
            string data = PlayerPrefs.GetString(SaveKey, "");
            string[] parts = data.Split(';');
            for (int i = 0; i < SavedSlots; i++)
            {
                _saved[i] = null;
                if (i < parts.Length && parts[i].Length == 6
                    && uint.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out uint v))
                {
                    _saved[i] = new Color32((byte)(v >> 16), (byte)(v >> 8 & 0xFF), (byte)(v & 0xFF), 255);
                }
            }
        }

        private void PersistSavedSwatches()
        {
            var parts = new string[SavedSlots];
            for (int i = 0; i < SavedSlots; i++)
            {
                parts[i] = _saved[i].HasValue
                    ? $"{_saved[i].Value.r:X2}{_saved[i].Value.g:X2}{_saved[i].Value.b:X2}"
                    : "";
            }
            PlayerPrefs.SetString(SaveKey, string.Join(";", parts));
            PlayerPrefs.Save();
        }

        // ---------- wheel interaction ----------

        private void Update()
        {
            if (!Visible)
            {
                PointerOverPaintUI = false;
                _draggingRing = _draggingSv = false;
                return;
            }
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
                _hue = Mathf.Repeat(Mathf.Atan2(-local.y, local.x) / (2f * Mathf.PI), 1f);
                ApplyHsv();
            }
            else if (_draggingSv)
            {
                _sat = Mathf.Clamp01(local.x / SvSize + 0.5f);
                _val = Mathf.Clamp01(-local.y / SvSize + 0.5f);
                ApplyHsv();
            }
        }

        private Vector2 WheelCenterGui()
        {
            return new Vector2(PanelRect.x + 95f, PanelRect.y + 40f + WheelSize / 2f);
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
                        px[y * WheelSize + x] = (d >= RingInner && d <= RingOuter)
                            ? (Color32)Color.HSVToRGB(Mathf.Repeat(Mathf.Atan2(dy, dx) / (2f * Mathf.PI), 1f), 1f, 1f)
                            : new Color32(0, 0, 0, 0);
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
                    for (int x = 0; x < SvSize; x++)
                        px[y * SvSize + x] = Color.HSVToRGB(_hue, (float)x / (SvSize - 1), (float)y / (SvSize - 1));
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

        // ---------- drawing ----------

        private void OnGUI()
        {
            if (!Visible || _skin == null) return;
            EnsureTextures();

            Rect panel = PanelRect;
            GUI.Box(panel, "");
            GUI.Label(new Rect(panel.x + 8, panel.y + 4, panel.width - 16, 20),
                "PAINT MODE (F exit) · LMB paint · MMB orbit · Space eyedropper");

            // Hue wheel + SV square
            Vector2 center = WheelCenterGui();
            GUI.DrawTexture(new Rect(center.x - WheelSize / 2f, center.y - WheelSize / 2f, WheelSize, WheelSize), _wheelTex);
            GUI.DrawTexture(new Rect(center.x - SvSize / 2f, center.y - SvSize / 2f, SvSize, SvSize), _svTex, ScaleMode.ScaleToFit, false);

            var prevColor = GUI.color;
            float hueAngle = _hue * 2f * Mathf.PI;
            Vector2 ringPos = center + new Vector2(Mathf.Cos(hueAngle), -Mathf.Sin(hueAngle)) * ((RingInner + RingOuter) / 2f);
            GUI.color = Color.black;
            GUI.Box(new Rect(ringPos.x - 5, ringPos.y - 5, 10, 10), "");
            Vector2 svPos = center + new Vector2((_sat - 0.5f) * SvSize, (0.5f - _val) * SvSize);
            GUI.Box(new Rect(svPos.x - 4, svPos.y - 4, 8, 8), "");
            GUI.color = prevColor;

            // Current color + hex next to the wheel
            var prevBg = GUI.backgroundColor;
            GUI.backgroundColor = _skin.CurrentColor;
            GUI.Box(new Rect(panel.x + 210, panel.y + 60, 44, 44), "");
            GUI.backgroundColor = prevBg;
            Color32 cur = _skin.CurrentColor;
            GUI.Label(new Rect(panel.x + 205, panel.y + 108, 90, 20), $"#{cur.r:X2}{cur.g:X2}{cur.b:X2}");

            // Slider stack below the wheel
            float y = center.y + WheelSize / 2f + 6f;
            GUILayout.BeginArea(new Rect(panel.x + 10, y, panel.width - 20, panel.yMax - y - 6));

            // RGB sliders (precise numeric control)
            Color32 c0 = _skin.CurrentColor;
            float r = Row("R", c0.r, 0, 255), g = Row("G", c0.g, 0, 255), b = Row("B", c0.b, 0, 255);
            if ((byte)r != c0.r || (byte)g != c0.g || (byte)b != c0.b)
            {
                SetColor(new Color32((byte)r, (byte)g, (byte)b, 255));
            }

            // HSV sliders (fine-tuning — Value especially, for shadowed vs lit surfaces)
            float h2 = Row("H", _hue * 360f, 0f, 360f) / 360f;
            float s2 = Row("S", _sat * 100f, 0f, 100f) / 100f;
            float v2 = Row("V", _val * 100f, 0f, 100f) / 100f;
            if (!Mathf.Approximately(h2, _hue) || !Mathf.Approximately(s2, _sat) || !Mathf.Approximately(v2, _val))
            {
                _hue = h2; _sat = s2; _val = v2;
                ApplyHsv();
            }

            GUILayout.Space(3);
            // Material response (sheen sells the disguise as much as hue — Paint doc §6)
            _skin.CurrentMetallic = Row("Metallic", _skin.CurrentMetallic * 100f, 0f, 100f) / 100f;
            _skin.CurrentRoughness = Row("Roughness", _skin.CurrentRoughness * 100f, 0f, 100f) / 100f;
            _skin.BrushSize = Row("Brush", _skin.BrushSize / _skin.MaxBrush * 100f, 3f, 100f) / 100f * _skin.MaxBrush;

            GUILayout.Space(4);
            // Preset swatches
            GUILayout.BeginHorizontal();
            for (int i = 0; i < PresetColors.Length; i++)
            {
                var pb = GUI.backgroundColor;
                GUI.backgroundColor = PresetColors[i];
                if (GUILayout.Button("", GUILayout.Width(20), GUILayout.Height(20))) SetColor(PresetColors[i]);
                GUI.backgroundColor = pb;
            }
            GUILayout.EndHorizontal();

            // Per-map saved swatches: click empty = save current, click filled = load. "Clear saves" resets.
            GUILayout.BeginHorizontal();
            GUILayout.Label("Saved:", GUILayout.Width(44));
            for (int i = 0; i < SavedSlots; i++)
            {
                var pb = GUI.backgroundColor;
                GUI.backgroundColor = _saved[i] ?? new Color32(60, 60, 60, 255);
                if (GUILayout.Button(_saved[i].HasValue ? "" : "+", GUILayout.Width(24), GUILayout.Height(20)))
                {
                    if (_saved[i].HasValue) SetColor(_saved[i].Value);
                    else { _saved[i] = _skin.CurrentColor; PersistSavedSwatches(); }
                }
                GUI.backgroundColor = pb;
            }
            if (GUILayout.Button("x", GUILayout.Width(22), GUILayout.Height(20)))
            {
                for (int i = 0; i < SavedSlots; i++) _saved[i] = null;
                PersistSavedSwatches();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Undo stroke")) _skin.RequestUndo();
            if (GUILayout.Button("Clear all")) _skin.RequestClear();
            GUILayout.EndHorizontal();

            bool shadow = GUILayout.Toggle(_skin.CastShadows.Value, " Cast shadow (off = hide your shadow shape)");
            if (shadow != _skin.CastShadows.Value) _skin.RequestSetShadow(shadow);

            GUILayout.Label($"Strokes: {_skin.Strokes.Count}");
            GUILayout.EndArea();
        }

        private static float Row(string label, float value, float min, float max)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, GUILayout.Width(70));
            float v = GUILayout.HorizontalSlider(value, min, max, GUILayout.Height(16));
            GUILayout.Label(Mathf.RoundToInt(v).ToString(), GUILayout.Width(34));
            GUILayout.EndHorizontal();
            return v;
        }
    }
}
