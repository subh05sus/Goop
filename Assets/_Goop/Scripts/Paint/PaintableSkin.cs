using Goop.UI;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Goop.Paint
{
    /// <summary>
    /// Per-player paintable skin (PRD 7.1 + Paint doc). Freehand strokes carry color AND material params
    /// (metallic/roughness -> metallic-gloss map). Painting is done by TRUE 3D SURFACE DISTANCE, not UV
    /// distance: a UV->local-position map is baked from the mesh, and a dab paints every texel whose
    /// surface point is within the brush's world radius of the hit point. This is why one dab no longer
    /// floods a whole UV island (the torso is packed into a small, dense UV region — a UV-space disc there
    /// covered the entire torso; a 3D-distance disc does not).
    ///
    /// Local input paints instantly (prediction); the server validates + appends each stroke to a
    /// server-write NetworkList that replicates to everyone (incl. late joiners). Undo/Clear rebuild from
    /// the list. Cast-shadow is a server-validated toggle (only ever On/Off).
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class PaintableSkin : NetworkBehaviour
    {
        private const float MaxBrushSize = 0.15f;   // fraction of the mesh bounds diagonal
        private const float DefaultSmoothness = 0.35f;

        [SerializeField] private int textureSize = 512;
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
            set => brushSize = Mathf.Clamp(value, 0.005f, MaxBrushSize);
        }

        public float MaxBrush => MaxBrushSize;

        /// <summary>World-space radius of the current brush — used by the preview ring.</summary>
        public float WorldBrushRadius => brushSize * _worldBoundsDiag;

        private SkinnedMeshRenderer _renderer;
        private MeshCollider _collider;
        private Goop.Gameplay.PoseController _poseController;
        private Texture2D _paintTexture;
        private Texture2D _metallicTexture;
        private Color32[] _colorPixels;
        private Color32[] _metalPixels;
        private bool _textureDirty;

        // UV -> baked-local surface position map (built from the baked mesh). Painting tests distance
        // against these, so it's independent of how the UVs are packed.
        private Vector3[] _positionMap;
        private bool[] _covered;
        private float _localBoundsDiag = 1f;  // baked (unscaled) diagonal — brush radius unit
        private float _worldBoundsDiag = 1f;  // rendered world diagonal — for the preview ring

        private Camera OwnerCamera => Camera.main;

        public override void OnNetworkSpawn()
        {
            _renderer = GetComponentInChildren<SkinnedMeshRenderer>();
            SetupPaintTexture();
            SetupCollider();
            BuildPositionMap();

            RebuildTexturesFromList();

            Strokes.OnListChanged += OnStrokesChanged;
            CastShadows.OnValueChanged += OnCastShadowsChanged;
            OnCastShadowsChanged(true, CastShadows.Value);

            _poseController = GetComponent<Goop.Gameplay.PoseController>();
            if (_poseController != null)
            {
                _poseController.PoseIndex.OnValueChanged += OnPoseChangedRebake;
            }

            if (IsOwner)
            {
                var palette = GetComponent<PaletteUI>();
                if (palette != null) palette.Initialize(this);
            }
        }

        public override void OnNetworkDespawn()
        {
            Strokes.OnListChanged -= OnStrokesChanged;
            CastShadows.OnValueChanged -= OnCastShadowsChanged;
            if (_poseController != null)
            {
                _poseController.PoseIndex.OnValueChanged -= OnPoseChangedRebake;
            }
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
            BuildPositionMap();
        }

        /// <summary>Re-snapshot the skinned mesh into the collider so the hitbox matches the CURRENT
        /// silhouette.</summary>
        public void RebakeCollider()
        {
            if (_renderer == null || _collider == null) return;
            var baked = new Mesh();
            _renderer.BakeMesh(baked, true);
            var old = _collider.sharedMesh;
            _collider.sharedMesh = baked;
            if (old != null) Destroy(old);
        }

        private void SetupPaintTexture()
        {
            _paintTexture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp
            };
            _metallicTexture = new Texture2D(textureSize, textureSize, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp
            };
            _colorPixels = new Color32[textureSize * textureSize];
            _metalPixels = new Color32[textureSize * textureSize];
            ResetTexturePixels();

            Shader lit = Shader.Find("Universal Render Pipeline/Lit");
            Material materialInstance = lit != null ? new Material(lit) : new Material(_renderer.sharedMaterial);
            materialInstance.color = Color.white;
            if (materialInstance.HasProperty("_BaseMap")) materialInstance.SetTexture("_BaseMap", _paintTexture);
            else materialInstance.mainTexture = _paintTexture;
            if (materialInstance.HasProperty("_MetallicGlossMap"))
            {
                materialInstance.SetTexture("_MetallicGlossMap", _metallicTexture);
                materialInstance.EnableKeyword("_METALLICSPECGLOSSMAP");
                if (materialInstance.HasProperty("_GlossMapScale")) materialInstance.SetFloat("_GlossMapScale", 1f);
                if (materialInstance.HasProperty("_Smoothness")) materialInstance.SetFloat("_Smoothness", 1f);
            }
            _renderer.material = materialInstance;
        }

        private void ResetTexturePixels()
        {
            Color32 white = new(255, 255, 255, 255);
            Color32 defaultMetal = new(0, 0, 0, (byte)(DefaultSmoothness * 255f));
            for (int i = 0; i < _colorPixels.Length; i++)
            {
                _colorPixels[i] = white;
                _metalPixels[i] = defaultMetal;
            }
            _paintTexture.SetPixels32(_colorPixels);
            _metallicTexture.SetPixels32(_metalPixels);
            _paintTexture.Apply();
            _metallicTexture.Apply();
        }

        private void SetupCollider()
        {
            // useScale:true — without it the x100 FBX import scale is applied twice (collider 100x too big).
            var bakedMesh = new Mesh();
            _renderer.BakeMesh(bakedMesh, true);
            _collider = _renderer.gameObject.AddComponent<MeshCollider>();
            _collider.sharedMesh = bakedMesh;
        }

        /// <summary>Rasterize the baked mesh into UV space, storing each texel's baked-local surface
        /// position. Built identically on every client (same mesh + UVs), so a stroke's UV resolves to the
        /// same surface point everywhere — deterministic replication with no extra network data.</summary>
        private void BuildPositionMap()
        {
            var mesh = _collider != null ? _collider.sharedMesh : null;
            if (mesh == null) return;

            _localBoundsDiag = Mathf.Max(0.0001f, mesh.bounds.size.magnitude);
            _worldBoundsDiag = Mathf.Max(0.0001f, _renderer.bounds.size.magnitude);

            int n = textureSize * textureSize;
            if (_positionMap == null || _positionMap.Length != n)
            {
                _positionMap = new Vector3[n];
                _covered = new bool[n];
            }
            System.Array.Clear(_covered, 0, n);

            Vector3[] verts = mesh.vertices;
            Vector2[] uvs = mesh.uv;
            int[] tris = mesh.triangles;
            if (uvs == null || uvs.Length != verts.Length) return;

            for (int t = 0; t < tris.Length; t += 3)
            {
                RasterizeTriangle(
                    uvs[tris[t]], uvs[tris[t + 1]], uvs[tris[t + 2]],
                    verts[tris[t]], verts[tris[t + 1]], verts[tris[t + 2]]);
            }
        }

        private void RasterizeTriangle(Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector3 p0, Vector3 p1, Vector3 p2)
        {
            // UV -> pixel space
            Vector2 a = uv0 * textureSize, b = uv1 * textureSize, c = uv2 * textureSize;
            int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.x, b.x, c.x)), 0, textureSize - 1);
            int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.x, b.x, c.x)), 0, textureSize - 1);
            int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(a.y, b.y, c.y)), 0, textureSize - 1);
            int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(a.y, b.y, c.y)), 0, textureSize - 1);

            float denom = (b.y - c.y) * (a.x - c.x) + (c.x - b.x) * (a.y - c.y);
            if (Mathf.Abs(denom) < 1e-9f) return;

            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    float px = x + 0.5f, py = y + 0.5f;
                    float w0 = ((b.y - c.y) * (px - c.x) + (c.x - b.x) * (py - c.y)) / denom;
                    float w1 = ((c.y - a.y) * (px - c.x) + (a.x - c.x) * (py - c.y)) / denom;
                    float w2 = 1f - w0 - w1;
                    // Small epsilon so shared triangle edges don't leave seam gaps.
                    if (w0 < -0.01f || w1 < -0.01f || w2 < -0.01f) continue;

                    int idx = y * textureSize + x;
                    _positionMap[idx] = w0 * p0 + w1 * p1 + w2 * p2;
                    _covered[idx] = true;
                }
            }
        }

        private void Update()
        {
            if (_textureDirty)
            {
                _paintTexture.SetPixels32(_colorPixels);
                _paintTexture.Apply();
                _metallicTexture.SetPixels32(_metalPixels);
                _metallicTexture.Apply();
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

        /// <summary>Raycast the cursor against this skin's own collider (painting + preview ring).</summary>
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
            // Distance-space dabs in UV so a held drag draws a line without thousands of dabs.
            if (Vector2.Distance(uv, _lastDabUV) < brushSize * 0.35f) return;
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

            ApplyStrokeToTexture(stroke);   // instant local prediction
            _textureDirty = true;
            SubmitStrokeServerRpc(stroke);
        }

        [ServerRpc]
        private void SubmitStrokeServerRpc(PaintStroke stroke)
        {
            if (stroke.U < 0f || stroke.U > 1f || stroke.V < 0f || stroke.V > 1f) return;
            stroke.BrushSize = Mathf.Clamp(stroke.BrushSize, 0.005f, MaxBrushSize);
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

        /// <summary>3D eyedropper: sample the surface under the cursor. Walks all hits, skips own non-mesh
        /// colliders. Paintable bodies sample their live texture; world geometry samples its material.</summary>
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
                    if (hit.collider is MeshCollider)
                    {
                        sampled = paintable.SampleTexture(hit.textureCoord);
                        return true;
                    }
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
                ApplyStrokeToTexture(change.Value);
                _textureDirty = true;
                return;
            }
            RebuildTexturesFromList();
        }

        private void RebuildTexturesFromList()
        {
            ResetPixelBuffers();
            foreach (var stroke in Strokes) PaintDab(stroke);
            _textureDirty = true;
        }

        private void ResetPixelBuffers()
        {
            Color32 white = new(255, 255, 255, 255);
            Color32 defaultMetal = new(0, 0, 0, (byte)(DefaultSmoothness * 255f));
            for (int i = 0; i < _colorPixels.Length; i++)
            {
                _colorPixels[i] = white;
                _metalPixels[i] = defaultMetal;
            }
        }

        private void ApplyStrokeToTexture(PaintStroke stroke) => PaintDab(stroke);

        /// <summary>Paint one dab by TRUE surface distance: resolve the stroke UV to a baked-local surface
        /// point, then color every covered texel whose surface point is within the brush's local radius.
        /// UV packing is irrelevant — a dense island (torso) and a loose island (head) get the same
        /// physical brush footprint.</summary>
        private void PaintDab(PaintStroke stroke)
        {
            if (_positionMap == null || _covered == null) return;

            int cx = Mathf.Clamp(Mathf.RoundToInt(stroke.U * textureSize), 0, textureSize - 1);
            int cy = Mathf.Clamp(Mathf.RoundToInt(stroke.V * textureSize), 0, textureSize - 1);

            Vector3 centerPos = FindCoveredPosition(cx, cy, out bool ok);
            if (!ok) return;

            float brush = Mathf.Clamp(stroke.BrushSize, 0.005f, MaxBrushSize);
            float radiusLocal = brush * _localBoundsDiag;
            float radiusSqr = radiusLocal * radiusLocal;

            Color32 color = new(stroke.R, stroke.G, stroke.B, 255);
            Color32 metal = new(stroke.Metallic, 0, 0, (byte)(255 - stroke.Roughness));

            // Bound the pixel window by an estimated texel density so we don't scan the whole texture per
            // dab, then reject by true 3D distance inside it. Density is estimated from the neighborhood
            // of the center texel; a generous multiplier plus the 3D test keeps it correct even if the
            // estimate is rough.
            int window = EstimatePixelWindow(cx, cy, centerPos, radiusLocal);
            int minX = Mathf.Max(0, cx - window), maxX = Mathf.Min(textureSize - 1, cx + window);
            int minY = Mathf.Max(0, cy - window), maxY = Mathf.Min(textureSize - 1, cy + window);

            int painted = 0;
            for (int y = minY; y <= maxY; y++)
            {
                int row = y * textureSize;
                for (int x = minX; x <= maxX; x++)
                {
                    int idx = row + x;
                    if (!_covered[idx]) continue;
                    if ((_positionMap[idx] - centerPos).sqrMagnitude > radiusSqr) continue;
                    _colorPixels[idx] = color;
                    _metalPixels[idx] = metal;
                    painted++;
                }
            }

            // Diagnostic (owner, first few dabs): proves the real runtime footprint. If this logs a small
            // number but the whole torso still visibly fills, the instance is running stale code.
            if (IsOwner && _dabDebugCount < 6)
            {
                _dabDebugCount++;
                int coveredTotal = 0;
                for (int i = 0; i < _covered.Length; i++) if (_covered[i]) coveredTotal++;
                Debug.Log($"[PaintDab] painted {painted} texels ({(coveredTotal > 0 ? 100f * painted / coveredTotal : 0):F1}% of body) at uv=({stroke.U:F2},{stroke.V:F2}) brush={brush:F3}");
            }
        }

        private int _dabDebugCount;

        private Vector3 FindCoveredPosition(int cx, int cy, out bool ok)
        {
            int centerIdx = cy * textureSize + cx;
            if (_covered[centerIdx]) { ok = true; return _positionMap[centerIdx]; }

            // UV landed in a tiny gap between islands — search a small ring for the nearest covered texel.
            for (int r = 1; r <= 3; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    int y = cy + dy;
                    if (y < 0 || y >= textureSize) continue;
                    for (int dx = -r; dx <= r; dx++)
                    {
                        int x = cx + dx;
                        if (x < 0 || x >= textureSize) continue;
                        int idx = y * textureSize + x;
                        if (_covered[idx]) { ok = true; return _positionMap[idx]; }
                    }
                }
            }
            ok = false;
            return Vector3.zero;
        }

        /// <summary>Estimate how many texels the brush radius spans, from local texel density near the
        /// center. Capped so a bad estimate can't blow up into a full-texture scan.</summary>
        private int EstimatePixelWindow(int cx, int cy, Vector3 centerPos, float radiusLocal)
        {
            float bestStep = float.MaxValue;
            // Distance in local space to an adjacent covered texel = local units per texel.
            int[] dxs = { 1, -1, 0, 0 };
            int[] dys = { 0, 0, 1, -1 };
            for (int k = 0; k < 4; k++)
            {
                int x = cx + dxs[k], y = cy + dys[k];
                if (x < 0 || x >= textureSize || y < 0 || y >= textureSize) continue;
                int idx = y * textureSize + x;
                if (!_covered[idx]) continue;
                float d = (_positionMap[idx] - centerPos).magnitude;
                if (d > 1e-6f && d < bestStep) bestStep = d;
            }
            if (bestStep >= float.MaxValue) return 24; // no neighbor info — modest default
            int window = Mathf.CeilToInt(radiusLocal / bestStep) + 2;
            return Mathf.Clamp(window, 2, 96);
        }
    }
}
