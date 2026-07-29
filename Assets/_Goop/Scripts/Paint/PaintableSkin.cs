using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Goop.Paint
{
    /// <summary>
    /// Per-player paintable skin — high-res TEXTURE painting on a clean, runtime-unwrapped mesh
    /// (Assets/_Goop/Resources/GoopChar_PaintMesh). The original model's UVs were a stretched strip, so
    /// texture paint was faded; the paint mesh replaces them with an even, non-overlapping unwrap, making
    /// texture painting sharp everywhere (that's why it was always crisp on the head — good UVs). Color goes
    /// into a base texture, metallic/roughness into a metallic-gloss texture; both sampled by URP Lit.
    ///
    /// Local prediction, server-validated stroke relay, list-driven replication + late-joiner history,
    /// Undo/Clear rebuild, cast-shadow toggle.
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class PaintableSkin : NetworkBehaviour
    {
        private const float MaxBrushSize = 0.12f;   // UV-space radius
        private const float DefaultSmoothness = 0.15f;
        private const string PaintMeshResource = "GoopChar_PaintMesh";

        [SerializeField] private int textureSize = 1024;
        [SerializeField] private float brushSize = 0.02f;
        [SerializeField] private InputActionAsset inputActions;

        public readonly NetworkList<PaintStroke> Strokes = new();

        public NetworkVariable<bool> CastShadows = new(
            true,
            writePerm: NetworkVariableWritePermission.Server);

        public Color32 CurrentColor = new(200, 40, 40, 255);
        public float CurrentMetallic = 0f;
        public float CurrentRoughness = 0.65f;

        public bool PaintingEnabled { get; set; }

        public float BrushSize
        {
            get => brushSize;
            set => brushSize = Mathf.Clamp(value, 0.004f, MaxBrushSize);
        }

        public float MaxBrush => MaxBrushSize;
        public float WorldBrushRadius => brushSize * _worldBoundsDiag;

        private SkinnedMeshRenderer _renderer;
        private MeshCollider _collider;
        private Goop.Gameplay.PoseController _poseController;
        private Texture2D _paintTexture;
        private Texture2D _metallicTexture;
        private Color32[] _colorPixels;
        private Color32[] _metalPixels;
        private bool _textureDirty;
        private float _worldBoundsDiag = 1f;

        private Camera OwnerCamera => Camera.main;

        public override void OnNetworkSpawn()
        {
            _renderer = GetComponentInChildren<SkinnedMeshRenderer>();
            SetupPaintMeshAndTexture();
            SetupCollider();

            RebuildTexturesFromList();

            Strokes.OnListChanged += OnStrokesChanged;
            CastShadows.OnValueChanged += OnCastShadowsChanged;
            OnCastShadowsChanged(true, CastShadows.Value);

            _poseController = GetComponent<Goop.Gameplay.PoseController>();
            if (_poseController != null)
                _poseController.PoseIndex.OnValueChanged += OnPoseChangedRebake;

            if (IsOwner)
            {
                var palette = GetComponent<Goop.UI.PaletteUI>();
                if (palette != null) palette.Initialize(this);
            }
        }

        public override void OnNetworkDespawn()
        {
            Strokes.OnListChanged -= OnStrokesChanged;
            CastShadows.OnValueChanged -= OnCastShadowsChanged;
            if (_poseController != null)
                _poseController.PoseIndex.OnValueChanged -= OnPoseChangedRebake;
        }

        private void OnCastShadowsChanged(bool previous, bool current)
        {
            if (_renderer == null) return;
            _renderer.shadowCastingMode = current
                ? UnityEngine.Rendering.ShadowCastingMode.On
                : UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        private void OnPoseChangedRebake(int previous, int current)
        {
            StopAllCoroutines();
            StartCoroutine(RebakeAfterTransition());
        }

        private System.Collections.IEnumerator RebakeAfterTransition()
        {
            yield return new WaitForSeconds(0.35f);
            RebakeCollider();
        }

        public void RebakeCollider()
        {
            if (_renderer == null || _collider == null) return;
            var baked = new Mesh();
            _renderer.BakeMesh(baked, true);
            var old = _collider.sharedMesh;
            _collider.sharedMesh = baked;
            if (old != null) Destroy(old);
        }

        private void SetupPaintMeshAndTexture()
        {
            // Swap the skinned mesh for the clean-unwrap paint mesh (same bones/bindposes/topology, just an
            // even UV set). Skinning is unaffected; the raycast textureCoord + material now use clean UVs.
            var paintMesh = Resources.Load<Mesh>(PaintMeshResource);
            if (paintMesh != null) _renderer.sharedMesh = paintMesh;
            else Debug.LogWarning("[PaintableSkin] GoopChar_PaintMesh not found in Resources — painting will use the model's original UVs.");

            _paintTexture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            _metallicTexture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            _colorPixels = new Color32[textureSize * textureSize];
            _metalPixels = new Color32[textureSize * textureSize];
            ResetPixelBuffers();
            UploadTextures();

            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            Material mat = lit != null ? new Material(lit) : new Material(_renderer.sharedMaterial);
            mat.color = Color.white;
            if (mat.HasProperty("_BaseMap")) mat.SetTexture("_BaseMap", _paintTexture);
            else mat.mainTexture = _paintTexture;
            if (mat.HasProperty("_MetallicGlossMap"))
            {
                mat.SetTexture("_MetallicGlossMap", _metallicTexture);
                mat.EnableKeyword("_METALLICSPECGLOSSMAP");
                if (mat.HasProperty("_Smoothness")) mat.SetFloat("_Smoothness", 1f);
            }
            _renderer.material = mat;

            _worldBoundsDiag = Mathf.Max(0.0001f, _renderer.bounds.size.magnitude);
        }

        private void SetupCollider()
        {
            var bakedMesh = new Mesh();
            _renderer.BakeMesh(bakedMesh, true);
            _collider = _renderer.gameObject.AddComponent<MeshCollider>();
            _collider.sharedMesh = bakedMesh;
        }

        private void ResetPixelBuffers()
        {
            Color32 white = new(255, 255, 255, 255);
            Color32 metal = new(0, 0, 0, (byte)(DefaultSmoothness * 255f));
            for (int i = 0; i < _colorPixels.Length; i++)
            {
                _colorPixels[i] = white;
                _metalPixels[i] = metal;
            }
        }

        private void UploadTextures()
        {
            _paintTexture.SetPixels32(_colorPixels);
            _paintTexture.Apply();
            _metallicTexture.SetPixels32(_metalPixels);
            _metallicTexture.Apply();
        }

        private void Update()
        {
            if (_textureDirty)
            {
                UploadTextures();
                _textureDirty = false;
            }

            if (!IsOwner || !PaintingEnabled) return;
            if (Goop.UI.PaletteUI.PointerOverPaintUI) return;
            if (Mouse.current == null) return;
            if (!Mouse.current.leftButton.isPressed)
            {
                _lastDabUV = new Vector2(-10f, -10f);
                return;
            }
            TryPaintAtPointer();
        }

        private Vector2 _lastDabUV = new(-10f, -10f);

        public bool TryGetBrushPoint(Vector2 screenPos, out RaycastHit hit)
        {
            hit = default;
            Camera cam = OwnerCamera;
            if (cam == null || _collider == null) return false;
            Ray ray = cam.ScreenPointToRay(screenPos);
            return _collider.Raycast(ray, out hit, 50f);
        }

        private void TryPaintAtPointer()
        {
            if (Mouse.current == null) return;
            Vector2 screenPos = Mouse.current.position.ReadValue();
            if (!TryGetBrushPoint(screenPos, out RaycastHit hit)) return;

            Vector2 uv = hit.textureCoord;
            if (Vector2.Distance(uv, _lastDabUV) < brushSize * 0.3f) return;
            _lastDabUV = uv;

            var stroke = new PaintStroke
            {
                U = uv.x,
                V = uv.y,
                BrushSize = brushSize,
                R = CurrentColor.r,
                G = CurrentColor.g,
                B = CurrentColor.b,
                Metallic = (byte)Mathf.RoundToInt(Mathf.Clamp01(CurrentMetallic) * 255f),
                Roughness = (byte)Mathf.RoundToInt(Mathf.Clamp01(CurrentRoughness) * 255f)
            };

            ApplyStroke(stroke);
            _textureDirty = true;
            SubmitStrokeServerRpc(stroke);
        }

        [ServerRpc]
        private void SubmitStrokeServerRpc(PaintStroke stroke)
        {
            if (stroke.U < 0f || stroke.U > 1f || stroke.V < 0f || stroke.V > 1f) return;
            stroke.BrushSize = Mathf.Clamp(stroke.BrushSize, 0.004f, MaxBrushSize);
            Strokes.Add(stroke);
        }

        public void RequestUndo() { if (IsOwner) UndoStrokeServerRpc(); }
        public void RequestClear() { if (IsOwner) ClearStrokesServerRpc(); }
        public void RequestSetShadow(bool cast) { if (IsOwner) SetShadowServerRpc(cast); }

        [ServerRpc]
        private void UndoStrokeServerRpc()
        {
            for (int i = 0; i < 15 && Strokes.Count > 0; i++)
                Strokes.RemoveAt(Strokes.Count - 1);
        }

        [ServerRpc]
        private void ClearStrokesServerRpc() => Strokes.Clear();

        [ServerRpc]
        private void SetShadowServerRpc(bool cast) => CastShadows.Value = cast;

        public Color32 SampleTexture(Vector2 uv) => _paintTexture.GetPixelBilinear(uv.x, uv.y);

        /// <summary>3D eyedropper: sample the surface under the cursor. Paintable bodies return their live
        /// texture; world geometry returns its material color.</summary>
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
                var paintable = hit.collider.GetComponentInParent<PaintableSkin>();
                if (paintable != null)
                {
                    if (hit.collider is MeshCollider) { sampled = paintable.SampleTexture(hit.textureCoord); return true; }
                    continue;
                }
                if (hit.collider.transform.root == transform.root) continue;
                var rend = hit.collider.GetComponent<Renderer>();
                if (rend != null && rend.sharedMaterial != null)
                {
                    var mat = rend.sharedMaterial;
                    if (mat.HasProperty("_BaseColor")) { sampled = mat.GetColor("_BaseColor"); return true; }
                    if (mat.HasProperty("_Color")) { sampled = mat.color; return true; }
                }
                return false;
            }
            return false;
        }

        private void OnStrokesChanged(NetworkListEvent<PaintStroke> change)
        {
            if (change.Type == NetworkListEvent<PaintStroke>.EventType.Add)
            {
                if (IsOwner) return; // owner already predicted this stroke locally
                ApplyStroke(change.Value);
                _textureDirty = true;
                return;
            }
            RebuildTexturesFromList();
        }

        private void RebuildTexturesFromList()
        {
            ResetPixelBuffers();
            foreach (var stroke in Strokes) ApplyStroke(stroke);
            _textureDirty = true;
        }

        /// <summary>Paint a circular disc in UV space. With the clean, even unwrap this maps to a compact,
        /// sharp patch on the surface everywhere (not just the head).</summary>
        private void ApplyStroke(PaintStroke stroke)
        {
            float brush = Mathf.Clamp(stroke.BrushSize, 0.004f, MaxBrushSize);
            int cx = Mathf.RoundToInt(Mathf.Clamp01(stroke.U) * (textureSize - 1));
            int cy = Mathf.RoundToInt(Mathf.Clamp01(stroke.V) * (textureSize - 1));
            int radius = Mathf.Max(1, Mathf.RoundToInt(brush * textureSize));
            int r2 = radius * radius;
            Color32 color = new(stroke.R, stroke.G, stroke.B, 255);
            Color32 metal = new(stroke.Metallic, 0, 0, (byte)(255 - stroke.Roughness));

            int minY = Mathf.Max(0, cy - radius), maxY = Mathf.Min(textureSize - 1, cy + radius);
            int minX = Mathf.Max(0, cx - radius), maxX = Mathf.Min(textureSize - 1, cx + radius);
            for (int y = minY; y <= maxY; y++)
            {
                int dy = y - cy; int row = y * textureSize;
                for (int x = minX; x <= maxX; x++)
                {
                    int dx = x - cx;
                    if (dx * dx + dy * dy > r2) continue;
                    int idx = row + x;
                    _colorPixels[idx] = color;
                    _metalPixels[idx] = metal;
                }
            }
        }
    }
}
