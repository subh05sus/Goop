using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Goop.Paint
{
    /// <summary>
    /// Per-player paintable skin — VERTEX-COLOR painting (PRD 7.1 + Paint doc), chosen because the model's
    /// body UVs are a thin, stretched strip that made texture painting read faded/streaked. Vertex painting
    /// ignores UVs entirely: a dab colors every vertex within the brush's 3D radius of the hit point, and a
    /// custom URP shader (Goop/VertexPaintLit) renders per-vertex color + metallic/smoothness. Resolution is
    /// the mesh's vertex density, so every body region paints evenly.
    ///
    /// Painting predicts locally, then the server validates + appends each stroke to a server-write
    /// NetworkList that replicates to everyone (incl. late joiners). Undo/Clear rebuild from the list.
    /// CastShadows is a server-validated toggle (only ever On/Off).
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class PaintableSkin : NetworkBehaviour
    {
        private const float MaxBrushSize = 0.15f;   // fraction of the mesh bounds diagonal
        private const float DefaultSmoothness = 0.15f; // matte body by default (paint sets its own sheen)

        [SerializeField] private float brushSize = 0.03f;
        [SerializeField] private InputActionAsset inputActions;
        // Subdivision levels for the paint mesh: each level ×4 triangles, making vertex paint sharp and
        // cursor-accurate (the base mesh's triangles are too big — paint interpolates = blur).
        // 3 = ×64 (~2.5k -> ~100k verts): crisp. Dial down if perf suffers with many players.
        [SerializeField] private int paintSubdivisions = 3;

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

        public float WorldBrushRadius => brushSize * _worldBoundsDiag;

        private SkinnedMeshRenderer _renderer;
        private MeshCollider _collider;
        private Goop.Gameplay.PoseController _poseController;

        private Mesh _renderMesh;            // per-instance copy we write vertex colors into
        private Color32[] _colors;           // per-vertex albedo
        private Vector2[] _mr;               // per-vertex (metallic, smoothness)
        private Vector3[] _bakedVerts;       // current-pose vertex positions (baked-local), for proximity
        private bool _meshDirty;

        private float _localBoundsDiag = 1f;
        private float _worldBoundsDiag = 1f;

        private Camera OwnerCamera => Camera.main;

        public override void OnNetworkSpawn()
        {
            _renderer = GetComponentInChildren<SkinnedMeshRenderer>();
            SetupRenderMesh();
            SetupCollider();
            RefreshBakedVerts();

            RebuildFromList();

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
            RefreshBakedVerts();
        }

        private void SetupRenderMesh()
        {
            // Per-instance, SUBDIVIDED mesh copy: dense verts = sharp vertex paint. Collider + baked verts
            // downstream all derive from this same mesh, so raycasts and paint stay aligned.
            _renderMesh = Subdivide(_renderer.sharedMesh, Mathf.Clamp(paintSubdivisions, 0, 4));
            _renderer.sharedMesh = _renderMesh;

            int n = _renderMesh.vertexCount;
            _colors = new Color32[n];
            _mr = new Vector2[n];
            ResetVertexData();

            Shader sh = Shader.Find("Goop/VertexPaintLit");
            Material mat = sh != null ? new Material(sh) : new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _renderer.material = mat;

            _localBoundsDiag = Mathf.Max(0.0001f, _renderMesh.bounds.size.magnitude);
            _worldBoundsDiag = Mathf.Max(0.0001f, _renderer.bounds.size.magnitude);
        }

        /// <summary>Midpoint-subdivide a skinned mesh N times (each level ×4 triangles), interpolating
        /// position/normal/uv AND bone weights so the result still skins correctly. Bind poses and bones
        /// are unchanged. Used only for the paint mesh, to make vertex painting sharp.</summary>
        private static Mesh Subdivide(Mesh src, int levels)
        {
            var mesh = Instantiate(src);
            if (levels <= 0) return mesh;

            for (int lvl = 0; lvl < levels; lvl++)
            {
                var verts = new System.Collections.Generic.List<Vector3>(mesh.vertices);
                var norms = new System.Collections.Generic.List<Vector3>(mesh.normals);
                var uvs = new System.Collections.Generic.List<Vector2>();
                mesh.GetUVs(0, uvs);
                bool hasUV = uvs.Count == verts.Count;
                var bw = mesh.boneWeights;
                bool hasBW = bw != null && bw.Length == verts.Count;
                var bwList = new System.Collections.Generic.List<BoneWeight>(hasBW ? bw : new BoneWeight[0]);

                int[] tris = mesh.triangles;
                var newTris = new System.Collections.Generic.List<int>(tris.Length * 4);
                var midCache = new System.Collections.Generic.Dictionary<long, int>();

                int Mid(int a, int b)
                {
                    long key = a < b ? ((long)a << 32) | (uint)b : ((long)b << 32) | (uint)a;
                    if (midCache.TryGetValue(key, out int idx)) return idx;
                    idx = verts.Count;
                    verts.Add((verts[a] + verts[b]) * 0.5f);
                    if (norms.Count == idx) norms.Add((norms[a] + norms[b]).normalized);
                    if (hasUV) uvs.Add((uvs[a] + uvs[b]) * 0.5f);
                    if (hasBW) bwList.Add(BlendWeights(bwList[a], bwList[b]));
                    midCache[key] = idx;
                    return idx;
                }

                for (int t = 0; t < tris.Length; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];
                    int ab = Mid(a, b), bc = Mid(b, c), ca = Mid(c, a);
                    newTris.Add(a); newTris.Add(ab); newTris.Add(ca);
                    newTris.Add(ab); newTris.Add(b); newTris.Add(bc);
                    newTris.Add(ca); newTris.Add(bc); newTris.Add(c);
                    newTris.Add(ab); newTris.Add(bc); newTris.Add(ca);
                }

                var bindposes = mesh.bindposes;
                mesh.Clear();
                mesh.indexFormat = verts.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
                mesh.SetVertices(verts);
                if (norms.Count == verts.Count) mesh.SetNormals(norms);
                if (hasUV) mesh.SetUVs(0, uvs);
                if (hasBW)
                {
                    mesh.boneWeights = bwList.ToArray();
                    mesh.bindposes = bindposes;
                }
                mesh.SetTriangles(newTris, 0);
                mesh.RecalculateBounds();
            }
            return mesh;
        }

        /// <summary>Blend two BoneWeights 50/50: merge shared bone indices, keep the top 4, renormalize.</summary>
        private static BoneWeight BlendWeights(BoneWeight w0, BoneWeight w1)
        {
            var acc = new System.Collections.Generic.Dictionary<int, float>();
            void Add(int idx, float wt) { if (wt <= 0f) return; acc.TryGetValue(idx, out float cur); acc[idx] = cur + wt; }
            Add(w0.boneIndex0, w0.weight0 * 0.5f); Add(w0.boneIndex1, w0.weight1 * 0.5f);
            Add(w0.boneIndex2, w0.weight2 * 0.5f); Add(w0.boneIndex3, w0.weight3 * 0.5f);
            Add(w1.boneIndex0, w1.weight0 * 0.5f); Add(w1.boneIndex1, w1.weight1 * 0.5f);
            Add(w1.boneIndex2, w1.weight2 * 0.5f); Add(w1.boneIndex3, w1.weight3 * 0.5f);

            // top 4 by weight
            var top = new (int idx, float wt)[4];
            foreach (var kv in acc)
            {
                for (int i = 0; i < 4; i++)
                {
                    if (kv.Value > top[i].wt)
                    {
                        for (int j = 3; j > i; j--) top[j] = top[j - 1];
                        top[i] = (kv.Key, kv.Value);
                        break;
                    }
                }
            }
            float sum = top[0].wt + top[1].wt + top[2].wt + top[3].wt;
            if (sum <= 0f) return w0;
            return new BoneWeight
            {
                boneIndex0 = top[0].idx, weight0 = top[0].wt / sum,
                boneIndex1 = top[1].idx, weight1 = top[1].wt / sum,
                boneIndex2 = top[2].idx, weight2 = top[2].wt / sum,
                boneIndex3 = top[3].idx, weight3 = top[3].wt / sum,
            };
        }

        private void ResetVertexData()
        {
            Color32 white = new(255, 255, 255, 255);
            for (int i = 0; i < _colors.Length; i++)
            {
                _colors[i] = white;
                _mr[i] = new Vector2(0f, DefaultSmoothness);
            }
            UploadVertexData();
        }

        private void UploadVertexData()
        {
            if (_renderMesh == null || !_renderMesh.isReadable) return;
            _renderMesh.colors32 = _colors;
            _renderMesh.SetUVs(1, new System.Collections.Generic.List<Vector2>(_mr));
        }

        private void SetupCollider()
        {
            // useScale:true — without it the x100 FBX import scale is applied twice (collider 100x too big).
            var bakedMesh = new Mesh();
            _renderer.BakeMesh(bakedMesh, true);
            _collider = _renderer.gameObject.AddComponent<MeshCollider>();
            _collider.sharedMesh = bakedMesh;
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

        /// <summary>Snapshot the current-pose vertex positions (baked-local, unscaled) so painting finds
        /// vertices by their real 3D location in the pose the player is actually in.</summary>
        private void RefreshBakedVerts()
        {
            if (_collider == null || _collider.sharedMesh == null) return;
            _bakedVerts = _collider.sharedMesh.vertices;
            _localBoundsDiag = Mathf.Max(0.0001f, _collider.sharedMesh.bounds.size.magnitude);
            _worldBoundsDiag = Mathf.Max(0.0001f, _renderer.bounds.size.magnitude);
        }

        private void Update()
        {
            if (_meshDirty)
            {
                UploadVertexData();
                _meshDirty = false;
            }

            if (!IsOwner || !PaintingEnabled) return;
            if (Goop.UI.PaletteUI.PointerOverPaintUI) return;
            if (Mouse.current == null) return;
            if (!Mouse.current.leftButton.isPressed)
            {
                _lastCenter = new Vector3(-999, -999, -999);
                return;
            }
            TryPaintAtPointer();
        }

        private Vector3 _lastCenter = new(-999, -999, -999);

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

            // Convert the world hit point into baked-local space (the space _bakedVerts live in).
            Vector3 centerLocal = _renderer.transform.InverseTransformPoint(hit.point);

            // Distance-space dabs so a held drag paints a line without spamming strokes.
            float spacing = brushSize * _localBoundsDiag * 0.4f;
            if ((centerLocal - _lastCenter).sqrMagnitude < spacing * spacing) return;
            _lastCenter = centerLocal;

            var stroke = new PaintStroke
            {
                Cx = centerLocal.x,
                Cy = centerLocal.y,
                Cz = centerLocal.z,
                BrushSize = brushSize,
                R = CurrentColor.r,
                G = CurrentColor.g,
                B = CurrentColor.b,
                Metallic = (byte)Mathf.RoundToInt(Mathf.Clamp01(CurrentMetallic) * 255f),
                Roughness = (byte)Mathf.RoundToInt(Mathf.Clamp01(CurrentRoughness) * 255f)
            };

            ApplyStroke(stroke);   // local prediction
            _meshDirty = true;
            SubmitStrokeServerRpc(stroke);
        }

        [ServerRpc]
        private void SubmitStrokeServerRpc(PaintStroke stroke)
        {
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

        /// <summary>Sample the painted color at the vertex nearest a surface hit (eyedropper on players).</summary>
        public Color32 SampleAtLocalPoint(Vector3 localPoint)
        {
            if (_bakedVerts == null) return CurrentColor;
            int best = -1; float bd = float.MaxValue;
            for (int i = 0; i < _bakedVerts.Length; i++)
            {
                float d = (_bakedVerts[i] - localPoint).sqrMagnitude;
                if (d < bd) { bd = d; best = i; }
            }
            return best >= 0 ? _colors[best] : (Color32)CurrentColor;
        }

        /// <summary>3D eyedropper: sample the surface under the cursor. Paintable bodies return the painted
        /// vertex color; world geometry returns its material color.</summary>
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
                        Vector3 lp = paintable._renderer.transform.InverseTransformPoint(hit.point);
                        sampled = paintable.SampleAtLocalPoint(lp);
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
                ApplyStroke(change.Value);
                _meshDirty = true;
                return;
            }
            RebuildFromList();
        }

        private void RebuildFromList()
        {
            ResetVertexDataArraysOnly();
            foreach (var stroke in Strokes) ApplyStroke(stroke);
            _meshDirty = true;
        }

        private void ResetVertexDataArraysOnly()
        {
            Color32 white = new(255, 255, 255, 255);
            for (int i = 0; i < _colors.Length; i++)
            {
                _colors[i] = white;
                _mr[i] = new Vector2(0f, DefaultSmoothness);
            }
        }

        private int _dabDebugCount;

        /// <summary>Color every vertex within the brush's 3D radius of the stroke center. UV layout is
        /// irrelevant — this is pure geometry.</summary>
        private void ApplyStroke(PaintStroke stroke)
        {
            if (_bakedVerts == null || _colors == null) return;

            Vector3 center = new(stroke.Cx, stroke.Cy, stroke.Cz);
            float radius = Mathf.Clamp(stroke.BrushSize, 0.005f, MaxBrushSize) * _localBoundsDiag;
            float rs = radius * radius;
            Color32 color = new(stroke.R, stroke.G, stroke.B, 255);
            Vector2 mr = new(stroke.Metallic / 255f, 1f - stroke.Roughness / 255f);

            int painted = 0;
            for (int i = 0; i < _bakedVerts.Length; i++)
            {
                if ((_bakedVerts[i] - center).sqrMagnitude > rs) continue;
                _colors[i] = color;
                _mr[i] = mr;
                painted++;
            }

            if (IsOwner && _dabDebugCount < 4)
            {
                _dabDebugCount++;
                Debug.Log($"[PaintDab-vtx] painted {painted}/{_bakedVerts.Length} verts, brush={stroke.BrushSize:F3} radiusLocal={radius:F5}");
            }
        }
    }
}
