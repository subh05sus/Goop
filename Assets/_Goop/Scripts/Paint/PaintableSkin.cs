using Goop.UI;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Goop.Paint
{
    /// <summary>
    /// Per-player paintable skin (PRD 7.1 + Paint doc). Freehand UV strokes carrying color AND material
    /// params (metallic/roughness paint into a metallic-gloss map — sheen sells a disguise as much as
    /// hue). Local input paints instantly (prediction); the server validates and appends every stroke to
    /// a server-write NetworkList, which replicates to everyone including late joiners. Undo/Clear are
    /// server-side list ops followed by a full texture rebuild. Shadow casting is a server-validated
    /// toggle (only ever ShadowCastingMode.On/Off — no invisibility path).
    /// </summary>
    [RequireComponent(typeof(Animator))]
    public class PaintableSkin : NetworkBehaviour
    {
        // No stroke cap (user decision) — dabs are distance-spaced (see TryPaintAtPointer) so counts
        // stay reasonable; texture rebuilds on undo/clear scale linearly with the list.
        private const float MaxBrushSize = 0.15f;
        private const float DefaultSmoothness = 0.35f;

        [SerializeField] private int textureSize = 256;
        [SerializeField] private float brushSize = 0.045f;
        [SerializeField] private InputActionAsset inputActions;

        // Server-write on purpose: all mutations go through ServerRpcs with validation (PRD 9).
        public readonly NetworkList<PaintStroke> Strokes = new();

        /// <summary>Server-validated shadow toggle (Paint doc §2/§5.3): a human-shaped cast shadow gives
        /// away a perfect paint job. Only ever flips shadowCastingMode On/Off.</summary>
        public NetworkVariable<bool> CastShadows = new(
            true,
            writePerm: NetworkVariableWritePermission.Server);

        public Color32 CurrentColor = new(200, 40, 40, 255);
        /// <summary>0..1 — painted into the metallic-gloss map per stroke.</summary>
        public float CurrentMetallic = 0f;
        /// <summary>0..1 — roughness (1 = fully matte). Smoothness = 1 - roughness.</summary>
        public float CurrentRoughness = 0.65f;

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
        private Goop.Gameplay.PoseController _poseController;
        private Texture2D _paintTexture;
        private Texture2D _metallicTexture; // R = metallic, A = smoothness (URP Lit convention)
        private bool _textureDirty;

        private Camera OwnerCamera => Camera.main;

        public override void OnNetworkSpawn()
        {
            _renderer = GetComponentInChildren<SkinnedMeshRenderer>();
            SetupPaintTexture();
            SetupCollider();

            RebuildTexturesFromList();

            Strokes.OnListChanged += OnStrokesChanged;
            CastShadows.OnValueChanged += OnCastShadowsChanged;
            OnCastShadowsChanged(true, CastShadows.Value);

            // Pose-aware hitbox: rebake the mesh collider whenever the pose changes (on every client —
            // the server needs it for shot validation, remotes for aim raycasts). Delayed past the 0.15s
            // animator transition so we bake the settled silhouette, not a blend frame.
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
        }

        /// <summary>Re-snapshot the skinned mesh into the collider so the hitbox matches the CURRENT
        /// silhouette (pose, prone tilt inherits from transform automatically).</summary>
        public void RebakeCollider()
        {
            if (_renderer == null || _collider == null) return;
            var meshCollider = (MeshCollider)_collider;
            var baked = new Mesh();
            _renderer.BakeMesh(baked, true);
            var old = meshCollider.sharedMesh;
            meshCollider.sharedMesh = baked;
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
            ResetTexturePixels();

            // Build the material from the URP Lit shader explicitly instead of cloning the FBX's imported
            // material — the imported one can reference a non-URP shader on some clients (MPPM virtual
            // players saw solid magenta/pink because of exactly that).
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
            var basePixels = new Color32[textureSize * textureSize];
            var metalPixels = new Color32[textureSize * textureSize];
            Color32 white = new(255, 255, 255, 255);
            Color32 defaultMetal = new(0, 0, 0, (byte)(DefaultSmoothness * 255f));
            for (int i = 0; i < basePixels.Length; i++)
            {
                basePixels[i] = white;
                metalPixels[i] = defaultMetal;
            }
            _paintTexture.SetPixels32(basePixels);
            _metallicTexture.SetPixels32(metalPixels);
            _paintTexture.Apply();
            _metallicTexture.Apply();
        }

        private void SetupCollider()
        {
            // useScale:true — without it, the x100 FBX import scale gets applied twice and the collider
            // is 100x too big (the original "paint doesn't work" bug).
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
                _metallicTexture.Apply();
                _textureDirty = false;
            }

            if (!IsOwner || !PaintingEnabled) return;
            if (Goop.UI.PaletteUI.PointerOverPaintUI) return; // clicking the palette must not also paint
            if (Mouse.current == null) return;
            if (!Mouse.current.leftButton.isPressed)
            {
                _lastDabUV = new Vector2(-10f, -10f); // new gesture may start exactly where the last ended
                return;
            }
            TryPaintAtPointer();
        }

        private Vector2 _lastDabUV = new(-10f, -10f);

        /// <summary>Raycast the cursor against this skin's own collider — used for painting and for the
        /// brush-preview ring (returns the surface point + normal under the cursor).</summary>
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

            // Distance-spaced dabs: only add a new stroke once the cursor has moved a fraction of the
            // brush radius in UV space — a held button still draws a continuous line, but at ~1/10th the
            // stroke count of the old add-every-frame behavior.
            Vector2 uv = hit.textureCoord;
            if (Vector2.Distance(uv, _lastDabUV) < brushSize * 0.35f) return;
            _lastDabUV = uv;

            var stroke = new PaintStroke
            {
                U = hit.textureCoord.x,
                V = hit.textureCoord.y,
                BrushSize = brushSize,
                R = CurrentColor.r,
                G = CurrentColor.g,
                B = CurrentColor.b,
                Metallic = (byte)Mathf.RoundToInt(Mathf.Clamp01(CurrentMetallic) * 255f),
                Roughness = (byte)Mathf.RoundToInt(Mathf.Clamp01(CurrentRoughness) * 255f)
            };

            // Instant local feedback (prediction), then the server validates + appends.
            ApplyStrokeToTexture(stroke);
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

        /// <summary>Undo the most recent stroke (server removes; every client rebuilds).</summary>
        public void RequestUndo() { if (IsOwner) UndoStrokeServerRpc(); }

        /// <summary>Wipe the whole paint job (server clears; every client rebuilds).</summary>
        public void RequestClear() { if (IsOwner) ClearStrokesServerRpc(); }

        /// <summary>Toggle cast shadow. Server-validated: the only thing it can ever change is
        /// shadowCastingMode On/Off — there is no path to invisibility.</summary>
        public void RequestSetShadow(bool cast) { if (IsOwner) SetShadowServerRpc(cast); }

        [ServerRpc]
        private void UndoStrokeServerRpc()
        {
            // One "undo" removes the last ~gesture worth of dabs, not a single 1-frame dot.
            for (int i = 0; i < 15 && Strokes.Count > 0; i++)
            {
                Strokes.RemoveAt(Strokes.Count - 1);
            }
        }

        [ServerRpc]
        private void ClearStrokesServerRpc()
        {
            Strokes.Clear();
        }

        [ServerRpc]
        private void SetShadowServerRpc(bool cast)
        {
            CastShadows.Value = cast;
        }

        /// <summary>Live paint texture sample (used by the eyedropper against any player's body).</summary>
        public Color32 SampleTexture(Vector2 uv) => _paintTexture.GetPixelBilinear(uv.x, uv.y);

        /// <summary>3D eyedropper ("spoid"): sample whatever surface is under the cursor. Walks all hits
        /// sorted by distance, skipping this player's own non-paintable colliders (camera sits behind the
        /// player, so the first hit is usually our own capsule). Paintable bodies sample their live paint
        /// texture at the hit UV; world geometry samples its material color. A tiny ±2/255 jitter is added
        /// (Paint doc §5.5) so screen-scraping auto-paint tools can't get pixel-perfect values.</summary>
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
                        sampled = Jitter(paintable.SampleTexture(hit.textureCoord));
                        return true;
                    }
                    continue; // a player's capsule/controller collider — skip, keep looking behind it
                }

                if (hit.collider.transform.root == transform.root) continue;

                var rend = hit.collider.GetComponent<Renderer>();
                if (rend != null && rend.sharedMaterial != null)
                {
                    var mat = rend.sharedMaterial;
                    if (mat.HasProperty("_BaseColor")) { sampled = Jitter(mat.GetColor("_BaseColor")); return true; }
                    if (mat.HasProperty("_Color")) { sampled = Jitter(mat.color); return true; }
                }
                return false; // un-sampleable, but it blocks the ray
            }
            return false;
        }

        private static Color32 Jitter(Color32 c)
        {
            byte J(byte v) => (byte)Mathf.Clamp(v + Random.Range(-2, 3), 0, 255);
            return new Color32(J(c.r), J(c.g), J(c.b), 255);
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

            // Undo / Clear / anything else: rebuild both textures from the authoritative list.
            RebuildTexturesFromList();
        }

        private void RebuildTexturesFromList()
        {
            ResetTexturePixels();
            foreach (var stroke in Strokes)
            {
                ApplyStrokeToTexture(stroke);
            }
            _paintTexture.Apply();
            _metallicTexture.Apply();
        }

        private void ApplyStrokeToTexture(PaintStroke stroke)
        {
            float clampedBrush = Mathf.Clamp(stroke.BrushSize, 0.005f, MaxBrushSize);
            int cx = Mathf.RoundToInt(Mathf.Clamp01(stroke.U) * textureSize);
            int cy = Mathf.RoundToInt(Mathf.Clamp01(stroke.V) * textureSize);
            int radius = Mathf.Max(1, Mathf.RoundToInt(clampedBrush * textureSize));
            Color32 color = new(stroke.R, stroke.G, stroke.B, 255);
            // URP Lit metallic-gloss convention: R = metallic, A = smoothness (1 - roughness).
            Color32 metal = new(stroke.Metallic, 0, 0, (byte)(255 - stroke.Roughness));

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
                    _metallicTexture.SetPixel(px, py, metal);
                }
            }
        }
    }
}
