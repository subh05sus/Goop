using Goop.UI;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Goop.Paint
{
    /// <summary>
    /// Per-player paintable skin (PRD 7.1). Local input paints instantly on the owner's own texture copy;
    /// the resulting stroke is appended to a NetworkList so every other client (including late joiners, via
    /// NGO's normal full-state sync) reconstructs the same texture from the same compact stroke list —
    /// never a full-texture sync.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class PaintableSkin : NetworkBehaviour
    {
        public const int MaxStrokesPerRound = 400;
        private const float MaxBrushSize = 0.15f;

        [SerializeField] private int textureSize = 256;
        [SerializeField] private float brushSize = 0.045f;
        [SerializeField] private InputActionAsset inputActions;

        public readonly NetworkList<PaintStroke> Strokes = new();
        public Color32 CurrentColor = new(200, 40, 40, 255);

        /// <summary>Gated by PaintModeController (F key). Painting only happens while in paint mode.</summary>
        public bool PaintingEnabled { get; set; }

        public float BrushSize
        {
            get => brushSize;
            set => brushSize = Mathf.Clamp(value, 0.005f, MaxBrushSize);
        }

        public float MaxBrush => MaxBrushSize;

        private SkinnedMeshRenderer _renderer;
        private Collider _collider;
        private Texture2D _paintTexture;
        private bool _textureDirty;

        private Camera OwnerCamera => Camera.main;

        public override void OnNetworkSpawn()
        {
            _renderer = GetComponentInChildren<SkinnedMeshRenderer>();
            SetupPaintTexture();
            SetupCollider();

            // Apply strokes already present at spawn time (late joiners get the full NetworkList state).
            foreach (var stroke in Strokes)
            {
                ApplyStrokeToTexture(stroke);
            }
            _paintTexture.Apply();

            Strokes.OnListChanged += OnStrokesChanged;

            if (IsOwner)
            {
                var palette = GetComponent<PaletteUI>();
                if (palette != null) palette.Initialize(this);
            }
        }

        public override void OnNetworkDespawn()
        {
            Strokes.OnListChanged -= OnStrokesChanged;
        }

        private void SetupPaintTexture()
        {
            _paintTexture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp
            };

            var basePixels = new Color32[textureSize * textureSize];
            Color32 white = new(255, 255, 255, 255);
            for (int i = 0; i < basePixels.Length; i++) basePixels[i] = white;
            _paintTexture.SetPixels32(basePixels);
            _paintTexture.Apply();

            // Build the material from the URP Lit shader explicitly instead of cloning the FBX's imported
            // material — the imported one can reference a non-URP shader on some clients (MPPM virtual
            // players saw solid magenta/pink because of exactly that).
            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            Material materialInstance = lit != null ? new Material(lit) : new Material(_renderer.sharedMaterial);
            materialInstance.color = Color.white;
            if (materialInstance.HasProperty("_BaseMap")) materialInstance.SetTexture("_BaseMap", _paintTexture);
            else materialInstance.mainTexture = _paintTexture;
            if (materialInstance.HasProperty("_Smoothness")) materialInstance.SetFloat("_Smoothness", 0.35f);
            _renderer.material = materialInstance;
        }

        private void SetupCollider()
        {
            // useScale:true bakes vertices WITHOUT the transform scale applied. The goop_guy import has a
            // x100 transform scale — the legacy BakeMesh() applies that scale into the vertices AND the
            // MeshCollider then inherits the x100 transform on top, producing a collider 100x too big
            // (measured: 116x57x210 meters). Every paint/eyedropper ray missed it. This was THE
            // "paint mode doesn't work" bug.
            var bakedMesh = new Mesh();
            _renderer.BakeMesh(bakedMesh, true);
            var meshCollider = _renderer.gameObject.AddComponent<MeshCollider>();
            meshCollider.sharedMesh = bakedMesh;
            _collider = meshCollider;
        }

        private void Update()
        {
            if (_textureDirty)
            {
                _paintTexture.Apply();
                _textureDirty = false;
            }

            if (!IsOwner || !PaintingEnabled) return;
            if (Goop.UI.PaletteUI.PointerOverPaintUI) return; // clicking the palette must not also paint
            if (Mouse.current == null || !Mouse.current.leftButton.isPressed) return;
            if (Strokes.Count >= MaxStrokesPerRound) return;

            TryPaintAtPointer();
        }

        private void TryPaintAtPointer()
        {
            Camera cam = OwnerCamera;
            if (Mouse.current == null || cam == null) return;

            Vector2 screenPos = Mouse.current.position.ReadValue();
            Ray ray = cam.ScreenPointToRay(screenPos);
            if (!_collider.Raycast(ray, out RaycastHit hit, 50f)) return;
            if (hit.collider != _collider) return;

            var stroke = new PaintStroke
            {
                U = hit.textureCoord.x,
                V = hit.textureCoord.y,
                BrushSize = brushSize,
                R = CurrentColor.r,
                G = CurrentColor.g,
                B = CurrentColor.b
            };

            ApplyStrokeToTexture(stroke);
            _textureDirty = true;
            Strokes.Add(stroke);
        }

        /// <summary>Live paint texture sample (used by the eyedropper against any player's body).</summary>
        public Color32 SampleTexture(Vector2 uv) => _paintTexture.GetPixelBilinear(uv.x, uv.y);

        /// <summary>3D eyedropper: sample whatever surface is under the cursor. Walks ALL hits sorted by
        /// distance and skips this player's own non-paintable colliders (the camera sits behind the player,
        /// so the first hit is very often our own CharacterController capsule — the old single-raycast
        /// version died on that every time). Own body and other players sample their live paint texture;
        /// world geometry samples its material color.</summary>
        public bool TrySampleWorldColor(Vector2 screenPos, out Color32 sampled)
        {
            sampled = default;
            Camera cam = OwnerCamera;
            if (cam == null) return false;

            Ray ray = cam.ScreenPointToRay(screenPos);
            RaycastHit[] hits = Physics.RaycastAll(ray, 100f, ~0, QueryTriggerInteraction.Ignore);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));

            foreach (var hit in hits)
            {
                // Any paintable body (own or another player's): sample its live texture at the hit UV.
                // textureCoord is only meaningful for MeshCollider hits — the paintable collider is one.
                var paintable = hit.collider.GetComponentInParent<PaintableSkin>();
                if (paintable != null)
                {
                    if (hit.collider is MeshCollider)
                    {
                        sampled = paintable.SampleTexture(hit.textureCoord);
                        return true;
                    }
                    continue; // a player's capsule/controller collider — skip, keep looking behind it
                }

                if (hit.collider.transform.root == transform.root) continue;

                var rend = hit.collider.GetComponent<Renderer>();
                if (rend != null && rend.sharedMaterial != null)
                {
                    var mat = rend.sharedMaterial;
                    if (mat.HasProperty("_BaseColor")) { sampled = mat.GetColor("_BaseColor"); return true; }
                    if (mat.HasProperty("_Color")) { sampled = mat.color; return true; }
                }
                // Something un-sampleable (no renderer) — it still blocks the ray, stop here.
                return false;
            }
            return false;
        }

        private void OnStrokesChanged(NetworkListEvent<PaintStroke> change)
        {
            if (IsOwner) return; // owner already applied this stroke locally before Add() replicated it
            if (change.Type != NetworkListEvent<PaintStroke>.EventType.Add) return;

            ApplyStrokeToTexture(change.Value);
            _textureDirty = true;
        }

        private void ApplyStrokeToTexture(PaintStroke stroke)
        {
            float clampedBrush = Mathf.Clamp(stroke.BrushSize, 0.005f, MaxBrushSize);
            int cx = Mathf.RoundToInt(Mathf.Clamp01(stroke.U) * textureSize);
            int cy = Mathf.RoundToInt(Mathf.Clamp01(stroke.V) * textureSize);
            int radius = Mathf.Max(1, Mathf.RoundToInt(clampedBrush * textureSize));
            Color32 color = new(stroke.R, stroke.G, stroke.B, 255);

            for (int y = -radius; y <= radius; y++)
            {
                int py = cy + y;
                if (py < 0 || py >= textureSize) continue;
                for (int x = -radius; x <= radius; x++)
                {
                    if (x * x + y * y > radius * radius) continue;
                    int px = cx + x;
                    if (px < 0 || px >= textureSize) continue;
                    _paintTexture.SetPixel(px, py, color);
                }
            }
        }
    }
}
