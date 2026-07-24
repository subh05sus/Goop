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

            var materialInstance = new Material(_renderer.sharedMaterial)
            {
                mainTexture = _paintTexture
            };
            _renderer.material = materialInstance;
        }

        private void SetupCollider()
        {
            var bakedMesh = new Mesh();
            _renderer.BakeMesh(bakedMesh);
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

        /// <summary>3D eyedropper: sample the color of whatever world surface is under the cursor
        /// (own body included — its live paint texture is sampled at the hit UV).</summary>
        public bool TrySampleWorldColor(Vector2 screenPos, out Color32 sampled)
        {
            sampled = default;
            Camera cam = OwnerCamera;
            if (cam == null) return false;

            Ray ray = cam.ScreenPointToRay(screenPos);
            if (!Physics.Raycast(ray, out RaycastHit hit, 100f, ~0, QueryTriggerInteraction.Ignore)) return false;

            if (hit.collider == _collider)
            {
                sampled = _paintTexture.GetPixelBilinear(hit.textureCoord.x, hit.textureCoord.y);
                return true;
            }

            var rend = hit.collider.GetComponent<Renderer>();
            if (rend != null && rend.sharedMaterial != null)
            {
                var mat = rend.sharedMaterial;
                if (mat.HasProperty("_BaseColor")) { sampled = mat.GetColor("_BaseColor"); return true; }
                if (mat.HasProperty("_Color")) { sampled = mat.color; return true; }
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
