#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace VROptimization
{
    /// <summary>
    /// URP-focused Material Atlas Optimizer for Unity 6 / Unity 2022+.
    /// Creates ONE shared material per atlas group with UV-remapped meshes for true draw call batching.
    /// TARGET: Static meshes only (MeshRenderer + MeshFilter), URP/Lit and URP/SimpleLit shaders.
    /// </summary>
    public class MaterialAtlasOptimizer : EditorWindow
    {
        #region Data Models

        /// <summary>
        /// Contains all relevant information about a material found in the scene.
        /// </summary>
        [Serializable]
        private class MaterialInfo
        {
            public Material material;
            public string assetPath;
            public string guid;
            public Shader shader;
            public Texture mainTexture;
            public Vector2 textureSize;
            public Vector2 mainTextureScale;
            public Vector2 mainTextureOffset;
            public int usedByCount;
            public List<GameObject> usedByObjects = new List<GameObject>();
            public List<MeshRenderer> usedByRenderers = new List<MeshRenderer>(); // Track which renderers use this material
            public Color baseColor;
            public float metallic;
            public float smoothness;
            public string[] shaderKeywords;
            public int renderQueue;
            public bool isURPCompatible;

            public MaterialInfo(Material mat, string path, string guid)
            {
                this.material = mat;
                this.assetPath = path;
                this.guid = guid;

                if (mat != null)
                {
                    shader = mat.shader;
                    mainTexture = mat.mainTexture;

                    if (mainTexture != null)
                    {
                        textureSize = new Vector2(mainTexture.width, mainTexture.height);
                    }

                    mainTextureScale = mat.mainTextureScale;
                    mainTextureOffset = mat.mainTextureOffset;

                    // Check URP compatibility
                    isURPCompatible = IsURPShader(shader);

                    // Cache URP properties
                    if (mat.HasProperty("_BaseColor")) baseColor = mat.GetColor("_BaseColor");
                    else if (mat.HasProperty("_Color")) baseColor = mat.GetColor("_Color");

                    if (mat.HasProperty("_Metallic")) metallic = mat.GetFloat("_Metallic");
                    if (mat.HasProperty("_Smoothness")) smoothness = mat.GetFloat("_Smoothness");

                    shaderKeywords = mat.shaderKeywords ?? new string[0];
                    renderQueue = mat.renderQueue;
                }
            }

            private static bool IsURPShader(Shader shader)
            {
                if (shader == null) return false;
                string shaderName = shader.name;
                return shaderName.Contains("Universal Render Pipeline/Lit") ||
                       shaderName.Contains("Universal Render Pipeline/Simple Lit") ||
                       shaderName.Contains("URP/Lit") ||
                       shaderName.Contains("URP/SimpleLit");
            }
        }

        /// <summary>
        /// Represents a group of materials that can be atlased together with UV remapping.
        /// </summary>
        private class AtlasGroup
        {
            public List<MaterialInfo> materials = new List<MaterialInfo>();
            public Shader shader;
            public Vector2 textureSize;
            public string[] sharedKeywords;
            public int estimatedAtlasSize;
            public int priorityScore; // Higher = more important (materials sharing same mesh)
            public int sharedRendererCount; // How many renderers have multiple materials from this group
        }

        #endregion
        #region Data Models

        /// <summary>
        /// Contains all texture maps for a material that need to be atlased
        /// </summary>
        [Serializable]
        private class MaterialTextureSet
        {
            public Material material;
            public Texture2D baseMap;
            public Texture2D normalMap;
            public Texture2D metallicMap;  // May contain smoothness in alpha
            public Texture2D occlusionMap;
            public Texture2D emissionMap;

            // Material properties
            public Color baseColor = Color.white;
            public Color emissionColor = Color.black;
            public float metallic;
            public float smoothness;
            public float normalScale = 1f;

            // Track which maps this material actually uses
            public bool hasNormalMap;
            public bool hasMetallicMap;
            public bool hasOcclusionMap;
            public bool hasEmissionMap;
        }

        /// <summary>
        /// Tracks submesh-specific atlas mapping for multi-material meshes
        /// </summary>
        [Serializable]
        private class SubmeshAtlasMapping
        {
            public int submeshIndex;
            public Material originalMaterial;
            public Rect baseMapRect;
            public Rect normalMapRect;
            public Rect metallicMapRect;
            public Rect occlusionMapRect;
            public Rect emissionMapRect;
        }

        /// <summary>
        /// Complete atlas set including all texture types
        /// </summary>
        [Serializable]
        private class AtlasTextureSet
        {
            public Texture2D baseAtlas;
            public Texture2D normalAtlas;
            public Texture2D metallicAtlas;
            public Texture2D occlusionAtlas;
            public Texture2D emissionAtlas;

            public Rect[] baseMapRects;
            public Rect[] normalMapRects;
            public Rect[] metallicMapRects;
            public Rect[] occlusionMapRects;
            public Rect[] emissionMapRects;

            public bool hasNormals;
            public bool hasMetallic;
            public bool hasOcclusion;
            public bool hasEmission;
        }

        #endregion



        #region Fields

        // UI State
        private Vector2 scrollPosition;
        private Vector2 duplicateScrollPosition;
        private Vector2 atlasScrollPosition;
        private int selectedTab = 0;
        private readonly string[] tabNames = { "Scan", "Duplicates", "Atlas Candidates", "Actions" };

        // Scan Options
        private bool includeInactive = false;
        private bool identifyDuplicates = true;
        private bool identifyAtlasCandidates = true;
        private bool skipTiledMaterials = true; // NEW: Skip materials with tiling/offset

        // Scanned Data
        private List<MaterialInfo> foundMaterials = new List<MaterialInfo>();
        private List<MaterialInfo> instanceMaterials = new List<MaterialInfo>();
        private List<List<MaterialInfo>> duplicateGroups = new List<List<MaterialInfo>>();
        private List<AtlasGroup> atlasGroups = new List<AtlasGroup>();

        // Atlas Generation Options
        private int maxAtlasSize = 2048;
        private int atlasPadding = 4;
        private readonly int[] atlasSizeOptions = { 1024, 2048, 4096 };

        // Foldout states
        private Dictionary<string, bool> foldoutStates = new Dictionary<string, bool>();

        // Constants
        private const string ATLAS_SAVE_PATH = "Assets/SceneMaterials/Atlases";
        private const string MATERIAL_SAVE_PATH = "Assets/SceneMaterials/AtlasedMaterials";
        private const string MESH_SAVE_PATH = "Assets/SceneMaterials/Atlases/Meshes";

        #endregion

        #region Menu Item

        [MenuItem("Tools/VR Optimization/Material & Atlas Optimizer (URP)")]
        public static void ShowWindow()
        {
            var window = GetWindow<MaterialAtlasOptimizer>("Material Atlas Optimizer (URP)");
            window.minSize = new Vector2(600, 400);
            window.Show();
        }

        #endregion

        #region GUI

        private void OnGUI()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Header
            EditorGUILayout.LabelField("Material Atlas Optimizer (URP)", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("UV-Based Atlasing | Static Meshes Only | Meta Quest 3", EditorStyles.miniLabel);
            EditorGUILayout.Space();

            // Tab Selection
            selectedTab = GUILayout.Toolbar(selectedTab, tabNames);
            EditorGUILayout.Space();

            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

            switch (selectedTab)
            {
                case 0: DrawScanTab(); break;
                case 1: DrawDuplicatesTab(); break;
                case 2: DrawAtlasCandidatesTab(); break;
                case 3: DrawActionsTab(); break;
            }

            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        private void DrawScanTab()
        {
            EditorGUILayout.LabelField("Scene Scan Options", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            includeInactive = EditorGUILayout.Toggle("Include Inactive Objects", includeInactive);
            identifyDuplicates = EditorGUILayout.Toggle("Identify Duplicate Materials", identifyDuplicates);
            identifyAtlasCandidates = EditorGUILayout.Toggle("Identify Atlas Candidates", identifyAtlasCandidates);
            skipTiledMaterials = EditorGUILayout.Toggle("Skip Tiled Materials", skipTiledMaterials);

            EditorGUILayout.Space();
            EditorGUILayout.HelpBox(
                "URP ONLY: Scans static MeshRenderers with URP/Lit or URP/SimpleLit materials.\n" +
                "Skips: SkinnedMeshRenderer, non-URP shaders, tiled materials (if enabled).",
                MessageType.Info
            );
            EditorGUILayout.Space();

            if (GUILayout.Button("Scan Active Scene", GUILayout.Height(40)))
            {
                ScanScene();
            }

            EditorGUILayout.Space();

            if (foundMaterials.Count > 0)
            {
                int urpCount = foundMaterials.Count(m => m.isURPCompatible);
                int nonURPCount = foundMaterials.Count - urpCount;

                EditorGUILayout.HelpBox(
                    $"Found {foundMaterials.Count} materials in scene.\n" +
                    $"URP-compatible: {urpCount}\n" +
                    $"Non-URP (skipped): {nonURPCount}\n" +
                    $"Instance materials: {instanceMaterials.Count}\n" +
                    $"Duplicate groups: {duplicateGroups.Count}\n" +
                    $"Atlas candidate groups: {atlasGroups.Count}",
                    MessageType.Info
                );
            }
        }

        private void DrawDuplicatesTab()
        {
            if (duplicateGroups.Count == 0)
            {
                EditorGUILayout.HelpBox("No duplicate materials found. Run a scan with 'Identify Duplicate Materials' enabled.", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField($"Duplicate Material Groups: {duplicateGroups.Count}", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Materials with identical properties that can be consolidated.", MessageType.Info);
            EditorGUILayout.Space();

            duplicateScrollPosition = EditorGUILayout.BeginScrollView(duplicateScrollPosition);

            for (int i = 0; i < duplicateGroups.Count; i++)
            {
                var group = duplicateGroups[i];
                if (group == null || group.Count < 2) continue;

                DrawDuplicateGroup(group, i);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawDuplicateGroup(List<MaterialInfo> group, int groupIndex)
        {
            EditorGUILayout.BeginVertical("box");

            var first = group[0];
            EditorGUILayout.LabelField($"Group {groupIndex + 1}: {group.Count} duplicates", EditorStyles.boldLabel);

            if (first.shader != null)
            {
                EditorGUILayout.LabelField($"Shader: {first.shader.name}", EditorStyles.miniLabel);
            }
            if (first.mainTexture != null)
            {
                EditorGUILayout.LabelField($"Texture: {first.mainTexture.name} ({first.textureSize.x}×{first.textureSize.y})", EditorStyles.miniLabel);
            }

            EditorGUILayout.Space(5);

            // Material list
            foreach (var matInfo in group)
            {
                if (matInfo.material == null) continue;

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"  • {matInfo.material.name}", GUILayout.Width(250));
                EditorGUILayout.LabelField($"({matInfo.usedByCount} objects)", GUILayout.Width(80));

                if (GUILayout.Button("Select", GUILayout.Width(60)))
                {
                    EditorGUIUtility.PingObject(matInfo.material);
                }
                if (GUILayout.Button("Objects", GUILayout.Width(60)))
                {
                    Selection.objects = matInfo.usedByObjects.ToArray();
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = Color.yellow;
            if (GUILayout.Button($"Merge Duplicates in Group {groupIndex + 1}", GUILayout.Height(25)))
            {
                MergeDuplicateMaterials(group);
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(5);
        }

        private void DrawAtlasCandidatesTab()
        {
            if (atlasGroups.Count == 0)
            {
                EditorGUILayout.HelpBox("No atlas candidates found. Run a scan with 'Identify Atlas Candidates' enabled.", MessageType.Info);
                return;
            }

            EditorGUILayout.LabelField($"Atlas Candidate Groups: {atlasGroups.Count}", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "UV-BASED ATLASING: Creates ONE shared URP material per group.\n" +
                "Clones and remaps mesh UVs for true draw call batching.",
                MessageType.Info
            );
            EditorGUILayout.Space();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Max Atlas Size:", GUILayout.Width(120));
            int sizeIndex = Array.IndexOf(atlasSizeOptions, maxAtlasSize);
            sizeIndex = EditorGUILayout.Popup(sizeIndex, atlasSizeOptions.Select(s => s.ToString()).ToArray());
            maxAtlasSize = atlasSizeOptions[Mathf.Clamp(sizeIndex, 0, atlasSizeOptions.Length - 1)];
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Atlas Padding:", GUILayout.Width(120));
            atlasPadding = EditorGUILayout.IntSlider(atlasPadding, 0, 16);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            atlasScrollPosition = EditorGUILayout.BeginScrollView(atlasScrollPosition);

            for (int i = 0; i < atlasGroups.Count; i++)
            {
                var group = atlasGroups[i];
                if (group == null || group.materials.Count < 2) continue;

                DrawAtlasGroup(group, i);
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawAtlasGroup(AtlasGroup group, int groupIndex)
        {
            EditorGUILayout.BeginVertical("box");

            // Show priority badge for high-priority groups
            EditorGUILayout.BeginHorizontal();
            if (group.sharedRendererCount > 0)
            {
                GUI.backgroundColor = new Color(1f, 0.8f, 0f); // Gold/orange for high priority
                GUILayout.Label("★ HIGH PRIORITY", EditorStyles.miniButtonMid, GUILayout.Width(120));
                GUI.backgroundColor = Color.white;
                GUILayout.Space(5);
            }
            EditorGUILayout.LabelField($"Atlas Group {groupIndex + 1}: {group.materials.Count} materials", EditorStyles.boldLabel);
            EditorGUILayout.EndHorizontal();

            if (group.shader != null)
            {
                EditorGUILayout.LabelField($"Shader: {group.shader.name}", EditorStyles.miniLabel);
            }
            EditorGUILayout.LabelField($"Texture Size: {group.textureSize.x}×{group.textureSize.y}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Estimated Atlas Size: {group.estimatedAtlasSize}×{group.estimatedAtlasSize}", EditorStyles.miniLabel);

            // Show priority details
            if (group.sharedRendererCount > 0)
            {
                EditorGUILayout.LabelField($"Shared Renderers: {group.sharedRendererCount} meshes use multiple materials from this group",
                    new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(1f, 0.6f, 0f) } });
            }

            // Count total objects that will be affected
            int totalObjects = group.materials.Sum(m => m.usedByCount);
            EditorGUILayout.LabelField($"Objects to Remap: {totalObjects}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Draw Call Reduction: {group.materials.Count} → 1 (save {group.materials.Count - 1})",
                new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.green } });

            EditorGUILayout.Space(5);

            // Expandable material list
            string foldoutKey = $"atlas_group_{groupIndex}";
            if (!foldoutStates.ContainsKey(foldoutKey))
                foldoutStates[foldoutKey] = false;

            foldoutStates[foldoutKey] = EditorGUILayout.Foldout(foldoutStates[foldoutKey], $"Materials ({group.materials.Count})");

            if (foldoutStates[foldoutKey])
            {
                foreach (var matInfo in group.materials)
                {
                    if (matInfo.material == null) continue;

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"  • {matInfo.material.name}", GUILayout.Width(250));
                    EditorGUILayout.LabelField($"({matInfo.usedByCount} objects)", GUILayout.Width(80));

                    if (GUILayout.Button("Select", GUILayout.Width(60)))
                    {
                        EditorGUIUtility.PingObject(matInfo.material);
                    }
                    EditorGUILayout.EndHorizontal();
                }
            }

            EditorGUILayout.Space(5);

            GUI.backgroundColor = Color.green;
            if (GUILayout.Button($"Generate Atlas & Remap UVs for Group {groupIndex + 1}", GUILayout.Height(35)))
            {
                if (EditorUtility.DisplayDialog("Generate UV-Remapped Atlas",
                    $"This will:\n" +
                    $"• Create 1 atlas texture\n" +
                    $"• Create 1 shared URP material\n" +
                    $"• Clone and remap {totalObjects} meshes\n" +
                    $"• Replace scene references\n\n" +
                    $"Original assets will not be modified.\n\n" +
                    $"Continue?",
                    "Yes", "Cancel"))
                {
                    GenerateUVRemappedAtlas_Enhanced(group, groupIndex);
                }
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(5);
        }

        private void DrawActionsTab()
        {
            EditorGUILayout.LabelField("Bulk Actions", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            EditorGUILayout.HelpBox("Perform bulk operations on all identified materials.", MessageType.Info);
            EditorGUILayout.Space();

            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = Color.yellow;
            if (GUILayout.Button($"Merge All Duplicate Groups ({duplicateGroups.Count})", GUILayout.Height(35)))
            {
                if (EditorUtility.DisplayDialog("Merge All Duplicates",
                    $"This will merge {duplicateGroups.Count} duplicate groups.\n\nThis operation can be undone with Ctrl+Z.\n\nContinue?",
                    "Yes", "Cancel"))
                {
                    MergeAllDuplicates();
                }
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space();

            EditorGUILayout.BeginHorizontal();
            GUI.backgroundColor = Color.green;
            if (GUILayout.Button($"Generate All UV-Remapped Atlases ({atlasGroups.Count})", GUILayout.Height(35)))
            {
                if (EditorUtility.DisplayDialog("Generate All UV-Remapped Atlases",
                    $"This will generate {atlasGroups.Count} atlases with UV remapping.\n\n" +
                    $"This may take several minutes and will clone many meshes.\n\n" +
                    $"Continue?",
                    "Yes", "Cancel"))
                {
                    GenerateAllUVRemappedAtlases();
                }
            }
            GUI.backgroundColor = Color.white;
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);

            if (foundMaterials.Count > 0)
            {
                EditorGUILayout.LabelField("Statistics", EditorStyles.boldLabel);
                EditorGUILayout.LabelField($"Total materials: {foundMaterials.Count}");
                EditorGUILayout.LabelField($"Potential draw call savings: {CalculateTotalDrawCallSavings()}");
            }
        }

        #endregion

        #region Scene Scanning

        /// <summary>
        /// Scans the active scene for URP-compatible static meshes and materials.
        /// </summary>
        private void ScanScene()
        {
            foundMaterials.Clear();
            instanceMaterials.Clear();
            duplicateGroups.Clear();
            atlasGroups.Clear();

            var materialDict = new Dictionary<string, MaterialInfo>();
            var scene = SceneManager.GetActiveScene();
            var rootObjects = scene.GetRootGameObjects();

            int skippedSkinned = 0;
            int skippedNonURP = 0;
            int skippedTiled = 0;

            foreach (var root in rootObjects)
            {
                if (!includeInactive && !root.activeInHierarchy) continue;

                // Only process MeshRenderers (static meshes)
                var renderers = root.GetComponentsInChildren<MeshRenderer>(includeInactive);
                foreach (var renderer in renderers)
                {
                    ProcessRenderer(renderer, materialDict, ref skippedNonURP, ref skippedTiled);
                }

                // Count skipped SkinnedMeshRenderers
                skippedSkinned += root.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive).Length;
            }

            foundMaterials = materialDict.Values.ToList();

            Debug.Log($"[MaterialAtlasOptimizer] Scan complete: {foundMaterials.Count} URP materials, {instanceMaterials.Count} instances");
            if (skippedSkinned > 0)
                Debug.LogWarning($"[MaterialAtlasOptimizer] Skipped {skippedSkinned} SkinnedMeshRenderers (not supported)");
            if (skippedNonURP > 0)
                Debug.LogWarning($"[MaterialAtlasOptimizer] Skipped {skippedNonURP} non-URP materials");
            if (skippedTiled > 0)
                Debug.LogWarning($"[MaterialAtlasOptimizer] Skipped {skippedTiled} tiled materials (scale/offset != identity)");

            if (identifyDuplicates)
            {
                IdentifyDuplicates();
                Debug.Log($"[MaterialAtlasOptimizer] Found {duplicateGroups.Count} duplicate groups");
            }

            if (identifyAtlasCandidates)
            {
                IdentifyAtlasCandidates();
                Debug.Log($"[MaterialAtlasOptimizer] Found {atlasGroups.Count} atlas candidate groups");
            }

            Repaint();
        }

        private void ProcessRenderer(MeshRenderer renderer, Dictionary<string, MaterialInfo> materialDict,
            ref int skippedNonURP, ref int skippedTiled)
        {
            if (renderer == null) return;

            // Check for MeshFilter (required for UV remapping)
            var meshFilter = renderer.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null)
            {
                return; // Skip renderers without valid meshes
            }

            var sharedMaterials = renderer.sharedMaterials;
            for (int i = 0; i < sharedMaterials.Length; i++)
            {
                var mat = sharedMaterials[i];
                if (mat == null) continue;

                string assetPath = AssetDatabase.GetAssetPath(mat);
                string guid = AssetDatabase.AssetPathToGUID(assetPath);

                // Check if it's an instance material
                if (string.IsNullOrEmpty(assetPath))
                {
                    guid = mat.GetInstanceID().ToString();
                    var instanceInfo = new MaterialInfo(mat, null, guid);
                    instanceInfo.usedByObjects.Add(renderer.gameObject);
                    instanceInfo.usedByCount = 1;
                    instanceMaterials.Add(instanceInfo);
                    continue;
                }

                // Check URP compatibility
                bool isURP = mat.shader != null &&
                    (mat.shader.name.Contains("Universal Render Pipeline/Lit") ||
                     mat.shader.name.Contains("Universal Render Pipeline/Simple Lit") ||
                     mat.shader.name.Contains("URP/Lit") ||
                     mat.shader.name.Contains("URP/SimpleLit"));

                if (!isURP)
                {
                    skippedNonURP++;
                    continue;
                }

                // Check for tiling/offset (skip if enabled)
                if (skipTiledMaterials)
                {
                    if (mat.mainTextureScale != Vector2.one || mat.mainTextureOffset != Vector2.zero)
                    {
                        skippedTiled++;
                        continue;
                    }
                }

                // Check for valid Texture2D
                if (mat.mainTexture != null && !(mat.mainTexture is Texture2D))
                {
                    continue; // Skip non-Texture2D
                }

                if (!materialDict.ContainsKey(guid))
                {
                    materialDict[guid] = new MaterialInfo(mat, assetPath, guid);
                }

                var matInfo = materialDict[guid];
                if (!matInfo.usedByObjects.Contains(renderer.gameObject))
                {
                    matInfo.usedByObjects.Add(renderer.gameObject);
                    matInfo.usedByCount++;
                }

                // Track which renderers use this material (for atlas prioritization)
                if (!matInfo.usedByRenderers.Contains(renderer))
                {
                    matInfo.usedByRenderers.Add(renderer);
                }
            }
        }

        #endregion

        #region Duplicate Detection

        /// <summary>
        /// Identifies groups of materials that are functionally identical.
        /// </summary>
        private void IdentifyDuplicates()
        {
            duplicateGroups.Clear();

            var groups = foundMaterials
                .Where(m => m.material != null && m.shader != null && m.isURPCompatible)
                .GroupBy(m => new
                {
                    shaderName = m.shader.name,
                    textureName = m.mainTexture != null ? m.mainTexture.name : "null",
                    color = m.baseColor,
                    metallic = m.metallic,
                    smoothness = m.smoothness,
                    keywords = string.Join(",", m.shaderKeywords.OrderBy(k => k))
                })
                .Where(g => g.Count() >= 2)
                .OrderByDescending(g => g.Count());

            foreach (var group in groups)
            {
                duplicateGroups.Add(group.ToList());
            }
        }

        #endregion

        #region Atlas Candidate Detection

        /// <summary>
        /// Identifies groups of URP materials that can be atlased with UV remapping.
        /// PRIORITIZES materials that share the same MeshRenderer for maximum draw call reduction.
        /// </summary>
        private void IdentifyAtlasCandidates()
        {
            atlasGroups.Clear();

            // Step 1: Get all materials eligible for atlasing
            var eligibleMaterials = foundMaterials
                .Where(m => m.material != null &&
                           m.shader != null &&
                           m.isURPCompatible &&
                           m.mainTexture != null &&
                           m.mainTexture is Texture2D &&
                           m.mainTextureScale == Vector2.one &&
                           m.mainTextureOffset == Vector2.zero)
                .ToList();

            if (eligibleMaterials.Count < 2)
            {
                return; // Need at least 2 materials to atlas
            }

            // Step 2: Build renderer -> materials mapping to identify multi-material meshes
            var rendererMaterialMap = new Dictionary<MeshRenderer, List<MaterialInfo>>();
            foreach (var matInfo in eligibleMaterials)
            {
                foreach (var renderer in matInfo.usedByRenderers)
                {
                    if (!rendererMaterialMap.ContainsKey(renderer))
                    {
                        rendererMaterialMap[renderer] = new List<MaterialInfo>();
                    }
                    rendererMaterialMap[renderer].Add(matInfo);
                }
            }

            // Step 3: Find renderers with multiple compatible materials (high-priority candidates)
            var processedMaterials = new HashSet<Material>();

            // PRIORITY PASS: Create atlas groups for materials sharing renderers
            foreach (var kvp in rendererMaterialMap.Where(r => r.Value.Count >= 2))
            {
                var renderer = kvp.Key;
                var sharedMaterials = kvp.Value;

                // Group by compatibility key (shader, size, keywords)
                var compatibleGroups = sharedMaterials
                    .GroupBy(m => new
                    {
                        shaderName = m.shader.name,
                        textureWidth = (int)m.textureSize.x,
                        textureHeight = (int)m.textureSize.y,
                        keywords = string.Join(",", m.shaderKeywords.OrderBy(k => k))
                    })
                    .Where(g => g.Count() >= 2); // Need at least 2 compatible materials on same mesh

                foreach (var group in compatibleGroups)
                {
                    var materials = group.ToList();

                    // Check if we already have a group with these materials
                    var existingGroup = atlasGroups.FirstOrDefault(ag =>
                        materials.All(m => ag.materials.Contains(m)));

                    if (existingGroup != null)
                    {
                        // Increment shared renderer count for existing group
                        existingGroup.sharedRendererCount++;
                        existingGroup.priorityScore += materials.Count * 10; // Bonus for each shared renderer
                    }
                    else
                    {
                        // Create new high-priority group
                        var first = materials[0];
                        var atlasGroup = new AtlasGroup
                        {
                            materials = materials,
                            shader = first.shader,
                            textureSize = first.textureSize,
                            sharedKeywords = first.shaderKeywords,
                            estimatedAtlasSize = CalculateRequiredAtlasSize(materials.Count, first.textureSize),
                            priorityScore = materials.Count * 100, // High base score for shared renderer materials
                            sharedRendererCount = 1
                        };

                        atlasGroups.Add(atlasGroup);

                        // Mark these materials as processed
                        foreach (var mat in materials)
                        {
                            processedMaterials.Add(mat.material);
                        }
                    }
                }
            }

            // Step 4: STANDARD PASS: Create atlas groups for remaining compatible materials
            var remainingMaterials = eligibleMaterials
                .Where(m => !processedMaterials.Contains(m.material))
                .ToList();

            var standardGroups = remainingMaterials
                .GroupBy(m => new
                {
                    shaderName = m.shader.name,
                    textureWidth = (int)m.textureSize.x,
                    textureHeight = (int)m.textureSize.y,
                    keywords = string.Join(",", m.shaderKeywords.OrderBy(k => k))
                })
                .Where(g => g.Count() >= 2);

            foreach (var group in standardGroups)
            {
                var materials = group.ToList();
                var first = materials[0];

                var atlasGroup = new AtlasGroup
                {
                    materials = materials,
                    shader = first.shader,
                    textureSize = first.textureSize,
                    sharedKeywords = first.shaderKeywords,
                    estimatedAtlasSize = CalculateRequiredAtlasSize(materials.Count, first.textureSize),
                    priorityScore = materials.Count, // Lower score for non-shared materials
                    sharedRendererCount = 0
                };

                atlasGroups.Add(atlasGroup);
            }

            // Step 5: Sort by priority (highest priority first)
            atlasGroups = atlasGroups
                .OrderByDescending(g => g.priorityScore)
                .ThenByDescending(g => g.materials.Count)
                .ToList();

            // Debug output
            int highPriority = atlasGroups.Count(g => g.sharedRendererCount > 0);
            int lowPriority = atlasGroups.Count - highPriority;
            Debug.Log($"[MaterialAtlasOptimizer] Atlas groups: {highPriority} high-priority (shared renderers), {lowPriority} standard");
        }

        private int CalculateRequiredAtlasSize(int textureCount, Vector2 textureSize)
        {
            float singleTexArea = textureSize.x * textureSize.y;
            float totalArea = singleTexArea * textureCount;
            int estimatedSize = Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(totalArea)));
            estimatedSize = Mathf.Clamp(estimatedSize, 512, maxAtlasSize);
            return estimatedSize;
        }

        #endregion


        #region Padding Utility

        /// <summary>
        /// Extends texture edges into padding area to prevent mipmap bleeding.
        /// CRITICAL for VR with foveated rendering.
        /// </summary>
        private Texture2D CreatePaddedTexture(Texture2D source, int padding)
        {
            if (source == null || padding <= 0)
                return source;

            int newWidth = source.width + padding * 2;
            int newHeight = source.height + padding * 2;

            Texture2D padded = new Texture2D(newWidth, newHeight, source.format, true);

            // Copy main texture to center
            Color[] sourcePixels = source.GetPixels();
            Color[] paddedPixels = new Color[newWidth * newHeight];

            // Fill center with source texture
            for (int y = 0; y < source.height; y++)
            {
                for (int x = 0; x < source.width; x++)
                {
                    paddedPixels[(y + padding) * newWidth + (x + padding)] = sourcePixels[y * source.width + x];
                }
            }

            // Extend edges - TOP and BOTTOM
            for (int x = padding; x < source.width + padding; x++)
            {
                Color topEdge = sourcePixels[x - padding];
                Color bottomEdge = sourcePixels[(source.height - 1) * source.width + (x - padding)];

                for (int p = 0; p < padding; p++)
                {
                    paddedPixels[p * newWidth + x] = topEdge;  // Top padding
                    paddedPixels[(newHeight - 1 - p) * newWidth + x] = bottomEdge;  // Bottom padding
                }
            }

            // Extend edges - LEFT and RIGHT
            for (int y = padding; y < source.height + padding; y++)
            {
                Color leftEdge = sourcePixels[(y - padding) * source.width];
                Color rightEdge = sourcePixels[(y - padding) * source.width + (source.width - 1)];

                for (int p = 0; p < padding; p++)
                {
                    paddedPixels[y * newWidth + p] = leftEdge;  // Left padding
                    paddedPixels[y * newWidth + (newWidth - 1 - p)] = rightEdge;  // Right padding
                }
            }

            // Fill corners with nearest corner pixel
            Color topLeft = sourcePixels[0];
            Color topRight = sourcePixels[source.width - 1];
            Color bottomLeft = sourcePixels[(source.height - 1) * source.width];
            Color bottomRight = sourcePixels[source.height * source.width - 1];

            for (int y = 0; y < padding; y++)
            {
                for (int x = 0; x < padding; x++)
                {
                    paddedPixels[y * newWidth + x] = topLeft;
                    paddedPixels[y * newWidth + (newWidth - 1 - x)] = topRight;
                    paddedPixels[(newHeight - 1 - y) * newWidth + x] = bottomLeft;
                    paddedPixels[(newHeight - 1 - y) * newWidth + (newWidth - 1 - x)] = bottomRight;
                }
            }

            padded.SetPixels(paddedPixels);
            padded.Apply();

            return padded;
        }

        #endregion

        #region Multi-Texture Atlas Generation

        /// <summary>
        /// Creates atlas for ALL texture types (Base, Normal, Metallic, Occlusion, Emission).
        /// Each texture type gets its own atlas with the same UV layout.
        /// </summary>
        private AtlasTextureSet CreateMultiTextureAtlas(List<MaterialTextureSet> materialTextures, int atlasSize, int padding)
        {
            var atlasSet = new AtlasTextureSet();

            // Determine which texture types we need to atlas
            atlasSet.hasNormals = materialTextures.Any(m => m.hasNormalMap);
            atlasSet.hasMetallic = materialTextures.Any(m => m.hasMetallicMap);
            atlasSet.hasOcclusion = materialTextures.Any(m => m.hasOcclusionMap);
            atlasSet.hasEmission = materialTextures.Any(m => m.hasEmissionMap);

            // Prepare base map textures (REQUIRED)
            var baseMaps = new List<Texture2D>();
            foreach (var matTex in materialTextures)
            {
                Texture2D baseTex = matTex.baseMap;
                if (baseTex == null)
                {
                    // Create solid color texture
                    baseTex = CreateSolidColorTexture(matTex.baseColor, 64);
                }
                baseMaps.Add(CreatePaddedTexture(baseTex, padding));
            }

            // Create base atlas
            atlasSet.baseAtlas = new Texture2D(atlasSize, atlasSize, TextureFormat.RGBA32, true);
            atlasSet.baseMapRects = atlasSet.baseAtlas.PackTextures(baseMaps.ToArray(), padding, atlasSize, false);

            // Clean up temporary padded textures
            foreach (var tex in baseMaps)
            {
                if (tex != null) DestroyImmediate(tex);
            }

            // Create normal atlas if needed
            if (atlasSet.hasNormals)
            {
                var normalMaps = new List<Texture2D>();
                foreach (var matTex in materialTextures)
                {
                    Texture2D normalTex = matTex.normalMap;
                    if (normalTex == null)
                    {
                        // Flat normal map (128, 128, 255) = pointing straight up
                        normalTex = CreateSolidColorTexture(new Color(0.5f, 0.5f, 1f, 1f), 64);
                    }
                    normalMaps.Add(CreatePaddedTexture(normalTex, padding));
                }

                atlasSet.normalAtlas = new Texture2D(atlasSize, atlasSize, TextureFormat.RGBA32, true);
                atlasSet.normalMapRects = atlasSet.normalAtlas.PackTextures(normalMaps.ToArray(), padding, atlasSize, false);

                foreach (var tex in normalMaps)
                {
                    if (tex != null) DestroyImmediate(tex);
                }
            }

            // Create metallic/smoothness atlas if needed
            if (atlasSet.hasMetallic)
            {
                var metallicMaps = new List<Texture2D>();
                foreach (var matTex in materialTextures)
                {
                    Texture2D metallicTex = matTex.metallicMap;
                    if (metallicTex == null)
                    {
                        // Create metallic map from material properties
                        // R = Metallic, A = Smoothness (URP convention)
                        Color metallicColor = new Color(matTex.metallic, matTex.metallic, matTex.metallic, matTex.smoothness);
                        metallicTex = CreateSolidColorTexture(metallicColor, 64);
                    }
                    metallicMaps.Add(CreatePaddedTexture(metallicTex, padding));
                }

                atlasSet.metallicAtlas = new Texture2D(atlasSize, atlasSize, TextureFormat.RGBA32, true);
                atlasSet.metallicMapRects = atlasSet.metallicAtlas.PackTextures(metallicMaps.ToArray(), padding, atlasSize, false);

                foreach (var tex in metallicMaps)
                {
                    if (tex != null) DestroyImmediate(tex);
                }
            }

            // Create occlusion atlas if needed
            if (atlasSet.hasOcclusion)
            {
                var occlusionMaps = new List<Texture2D>();
                foreach (var matTex in materialTextures)
                {
                    Texture2D occlusionTex = matTex.occlusionMap;
                    if (occlusionTex == null)
                    {
                        // White = no occlusion
                        occlusionTex = CreateSolidColorTexture(Color.white, 64);
                    }
                    occlusionMaps.Add(CreatePaddedTexture(occlusionTex, padding));
                }

                atlasSet.occlusionAtlas = new Texture2D(atlasSize, atlasSize, TextureFormat.RGBA32, true);
                atlasSet.occlusionMapRects = atlasSet.occlusionAtlas.PackTextures(occlusionMaps.ToArray(), padding, atlasSize, false);

                foreach (var tex in occlusionMaps)
                {
                    if (tex != null) DestroyImmediate(tex);
                }
            }

            // Create emission atlas if needed
            if (atlasSet.hasEmission)
            {
                var emissionMaps = new List<Texture2D>();
                foreach (var matTex in materialTextures)
                {
                    Texture2D emissionTex = matTex.emissionMap;
                    if (emissionTex == null)
                    {
                        // Use emission color or black
                        emissionTex = CreateSolidColorTexture(matTex.emissionColor, 64);
                    }
                    emissionMaps.Add(CreatePaddedTexture(emissionTex, padding));
                }

                atlasSet.emissionAtlas = new Texture2D(atlasSize, atlasSize, TextureFormat.RGBA32, true);
                atlasSet.emissionMapRects = atlasSet.emissionAtlas.PackTextures(emissionMaps.ToArray(), padding, atlasSize, false);

                foreach (var tex in emissionMaps)
                {
                    if (tex != null) DestroyImmediate(tex);
                }
            }

            return atlasSet;
        }

        /// <summary>
        /// Creates a simple solid color texture for materials without texture maps
        /// </summary>
        private Texture2D CreateSolidColorTexture(Color color, int size)
        {
            Texture2D tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            Color[] pixels = new Color[size * size];
            for (int i = 0; i < pixels.Length; i++)
            {
                pixels[i] = color;
            }
            tex.SetPixels(pixels);
            tex.Apply();
            return tex;
        }

        #endregion


        #region Multi-Material Mesh UV Remapping

        /// <summary>
        /// Remaps UVs for multi-material meshes by processing each submesh independently.
        /// Each submesh gets remapped to its material's atlas region.
        /// CRITICAL: This fixes the multi-material mesh limitation!
        /// </summary>
        private Mesh RemapMultiMaterialMeshUVs(Mesh originalMesh, List<SubmeshAtlasMapping> submeshMappings)
        {
            Mesh clonedMesh = UnityEngine.Object.Instantiate(originalMesh);
            Vector2[] uvs = clonedMesh.uv;

            if (uvs == null || uvs.Length == 0)
            {
                Debug.LogWarning($"[MaterialAtlasOptimizer] Mesh '{originalMesh.name}' has no UVs");
                return clonedMesh;
            }

            // Create a copy of UVs to modify
            Vector2[] newUVs = new Vector2[uvs.Length];
            Array.Copy(uvs, newUVs, uvs.Length);

            // Process each submesh independently
            for (int submeshIndex = 0; submeshIndex < clonedMesh.subMeshCount; submeshIndex++)
            {
                // Find the mapping for this submesh
                var mapping = submeshMappings.FirstOrDefault(m => m.submeshIndex == submeshIndex);
                if (mapping == null)
                {
                    Debug.LogWarning($"[MaterialAtlasOptimizer] No atlas mapping for submesh {submeshIndex}");
                    continue;
                }

                // Get triangles for this submesh to know which vertices to remap
                int[] triangles = clonedMesh.GetTriangles(submeshIndex);
                HashSet<int> vertexIndices = new HashSet<int>(triangles);

                // Remap UVs for vertices used by this submesh
                Rect atlasRect = mapping.baseMapRect;
                foreach (int vertexIndex in vertexIndices)
                {
                    if (vertexIndex < newUVs.Length)
                    {
                        Vector2 uv = uvs[vertexIndex];
                        // Transform UV from [0,1] space to atlas rect space
                        newUVs[vertexIndex] = new Vector2(
                            atlasRect.x + uv.x * atlasRect.width,
                            atlasRect.y + uv.y * atlasRect.height
                        );
                    }
                }
            }

            // Apply remapped UVs
            clonedMesh.uv = newUVs;

            return clonedMesh;
        }

        #endregion

        /// <summary>
        /// Extracts all texture maps from a URP material
        /// </summary>
        private MaterialTextureSet ExtractMaterialTextureSet(Material mat)
        {
            var texSet = new MaterialTextureSet();
            texSet.material = mat;

            // Base Map (Albedo)
            if (mat.HasProperty("_BaseMap"))
            {
                texSet.baseMap = mat.GetTexture("_BaseMap") as Texture2D;
            }
            if (mat.HasProperty("_BaseColor"))
            {
                texSet.baseColor = mat.GetColor("_BaseColor");
            }

            // Normal Map
            if (mat.HasProperty("_BumpMap"))
            {
                texSet.normalMap = mat.GetTexture("_BumpMap") as Texture2D;
                texSet.hasNormalMap = texSet.normalMap != null;
                if (mat.HasProperty("_BumpScale"))
                {
                    texSet.normalScale = mat.GetFloat("_BumpScale");
                }
            }

            // Metallic/Smoothness Map
            if (mat.HasProperty("_MetallicGlossMap"))
            {
                texSet.metallicMap = mat.GetTexture("_MetallicGlossMap") as Texture2D;
                texSet.hasMetallicMap = texSet.metallicMap != null;
            }
            if (mat.HasProperty("_Metallic"))
            {
                texSet.metallic = mat.GetFloat("_Metallic");
            }
            if (mat.HasProperty("_Smoothness"))
            {
                texSet.smoothness = mat.GetFloat("_Smoothness");
            }

            // Occlusion Map
            if (mat.HasProperty("_OcclusionMap"))
            {
                texSet.occlusionMap = mat.GetTexture("_OcclusionMap") as Texture2D;
                texSet.hasOcclusionMap = texSet.occlusionMap != null;
            }

            // Emission Map
            if (mat.HasProperty("_EmissionMap"))
            {
                texSet.emissionMap = mat.GetTexture("_EmissionMap") as Texture2D;
                texSet.hasEmissionMap = texSet.emissionMap != null;
            }
            if (mat.HasProperty("_EmissionColor"))
            {
                texSet.emissionColor = mat.GetColor("_EmissionColor");
            }

            return texSet;
        }

        /// <summary>
        /// Saves atlas texture to disk and returns the asset path
        /// </summary>
        private string SaveAtlasTexture(Texture2D atlas, string basePrefix, string mapType)
        {
            string atlasName = $"{basePrefix}_{mapType}.png";
            string atlasPath = Path.Combine(ATLAS_SAVE_PATH, atlasName).Replace("\\", "/");

            byte[] pngData = atlas.EncodeToPNG();
            File.WriteAllBytes(atlasPath, pngData);

            AssetDatabase.ImportAsset(atlasPath);

            return atlasPath;
        }


        #region SRP Batcher Validation

        /// <summary>
        /// Validates that materials are SRP Batcher compatible.
        /// SRP Batcher is CRITICAL for URP performance on Quest.
        /// </summary>
        private bool ValidateSRPBatcherCompatibility(List<Material> materials, out string errorMessage)
        {
            errorMessage = null;

            if (materials == null || materials.Count == 0)
            {
                errorMessage = "No materials to validate";
                return false;
            }

            // Check if all materials use the same shader
            Shader firstShader = materials[0].shader;
            foreach (var mat in materials)
            {
                if (mat.shader != firstShader)
                {
                    errorMessage = $"Materials use different shaders: {firstShader.name} vs {mat.shader.name}";
                    return false;
                }
            }

            // Check for incompatible shader keywords
            var firstKeywords = new HashSet<string>(materials[0].shaderKeywords);
            foreach (var mat in materials)
            {
                var keywords = new HashSet<string>(mat.shaderKeywords);
                if (!keywords.SetEquals(firstKeywords))
                {
                    var diff = keywords.Except(firstKeywords).Union(firstKeywords.Except(keywords));
                    errorMessage = $"Incompatible shader keywords: {string.Join(", ", diff)}";
                    return false;
                }
            }

            // Warn about Material Property Blocks (can't check directly, but can warn)
            Debug.Log("[MaterialAtlasOptimizer] SRP Batcher validation passed. " +
                     "Note: Material Property Blocks will break batching if used at runtime.");

            return true;
        }

        #endregion








        #region UV-Remapped Atlas Generation

        /// <summary>
        /// ENHANCED: Generates a complete atlas set with multi-material mesh support.
        /// Now atlases ALL texture types (Base, Normal, Metallic, Occlusion, Emission).
        /// Handles multi-material meshes by remapping UVs per submesh.
        /// </summary>
        private bool GenerateUVRemappedAtlas_Enhanced(AtlasGroup group, int groupIndex)
        {
            if (group == null || group.materials.Count < 2)
            {
                Debug.LogWarning("[MaterialAtlasOptimizer] Cannot generate atlas: insufficient materials");
                return false;
            }

            EditorUtility.DisplayProgressBar("Generating Multi-Texture Atlas", "Preparing textures...", 0f);

            try
            {
                // Step 1: Extract all texture sets from materials
                var materialTextureSets = new List<MaterialTextureSet>();

                foreach (var matInfo in group.materials)
                {
                    if (matInfo.material == null) continue;

                    var texSet = ExtractMaterialTextureSet(matInfo.material);
                    if (texSet != null)
                    {
                        materialTextureSets.Add(texSet);
                    }
                }

                if (materialTextureSets.Count < 2)
                {
                    Debug.LogWarning("[MaterialAtlasOptimizer] Not enough valid material texture sets");
                    EditorUtility.ClearProgressBar();
                    return false;
                }

                EditorUtility.DisplayProgressBar("Generating Multi-Texture Atlas", "Creating atlases...", 0.2f);

                // Step 2: Create multi-texture atlas set with proper padding
                int atlasSize = Mathf.Min(group.estimatedAtlasSize, maxAtlasSize);
                int padding = Mathf.Max(atlasPadding, 6); // Minimum 6 pixels for VR

                var atlasSet = CreateMultiTextureAtlas(materialTextureSets, atlasSize, padding);

                EditorUtility.DisplayProgressBar("Generating Multi-Texture Atlas", "Saving atlas textures...", 0.4f);

                // Step 3: Save all atlas textures to disk
                EnsureDirectoryExists(ATLAS_SAVE_PATH);
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string basePrefix = $"URP_Atlas_{group.shader.name.Replace("/", "_").Replace(" ", "")}_Group{groupIndex}_{timestamp}";

                // Save base map atlas
                string baseAtlasPath = SaveAtlasTexture(atlasSet.baseAtlas, basePrefix, "BaseMap");
                ConfigureAtlasImporter(baseAtlasPath, false, true); // Color data, sRGB

                // Save normal map atlas if present
                string normalAtlasPath = null;
                if (atlasSet.hasNormals && atlasSet.normalAtlas != null)
                {
                    normalAtlasPath = SaveAtlasTexture(atlasSet.normalAtlas, basePrefix, "NormalMap");
                    ConfigureAtlasImporter(normalAtlasPath, true, false); // Normal map, linear
                }

                // Save metallic/smoothness atlas if present
                string metallicAtlasPath = null;
                if (atlasSet.hasMetallic && atlasSet.metallicAtlas != null)
                {
                    metallicAtlasPath = SaveAtlasTexture(atlasSet.metallicAtlas, basePrefix, "MetallicSmoothness");
                    ConfigureAtlasImporter(metallicAtlasPath, false, false); // Linear data
                }

                // Save occlusion atlas if present
                string occlusionAtlasPath = null;
                if (atlasSet.hasOcclusion && atlasSet.occlusionAtlas != null)
                {
                    occlusionAtlasPath = SaveAtlasTexture(atlasSet.occlusionAtlas, basePrefix, "Occlusion");
                    ConfigureAtlasImporter(occlusionAtlasPath, false, false); // Linear data
                }

                // Save emission atlas if present
                string emissionAtlasPath = null;
                if (atlasSet.hasEmission && atlasSet.emissionAtlas != null)
                {
                    emissionAtlasPath = SaveAtlasTexture(atlasSet.emissionAtlas, basePrefix, "Emission");
                    ConfigureAtlasImporter(emissionAtlasPath, false, true); // Color data, sRGB
                }

                EditorUtility.DisplayProgressBar("Generating Multi-Texture Atlas", "Creating shared material...", 0.6f);

                // Step 4: Create shared material with all atlas textures
                Material sharedMaterial = CreateSharedURPMaterial_Enhanced(
                    group,
                    baseAtlasPath,
                    normalAtlasPath,
                    metallicAtlasPath,
                    occlusionAtlasPath,
                    emissionAtlasPath,
                    groupIndex,
                    timestamp
                );

                EditorUtility.DisplayProgressBar("Generating Multi-Texture Atlas", "Remapping mesh UVs...", 0.7f);

                // Step 5: Build material → atlas rect mappings
                var materialToRects = new Dictionary<Material, SubmeshAtlasMapping>();
                for (int i = 0; i < group.materials.Count && i < atlasSet.baseMapRects.Length; i++)
                {
                    var mat = group.materials[i].material;
                    if (mat != null)
                    {
                        materialToRects[mat] = new SubmeshAtlasMapping
                        {
                            submeshIndex = -1, // Will be set per renderer
                            originalMaterial = mat,
                            baseMapRect = atlasSet.baseMapRects[i],
                            normalMapRect = atlasSet.hasNormals ? atlasSet.normalMapRects[i] : new Rect(0, 0, 1, 1),
                            metallicMapRect = atlasSet.hasMetallic ? atlasSet.metallicMapRects[i] : new Rect(0, 0, 1, 1),
                            occlusionMapRect = atlasSet.hasOcclusion ? atlasSet.occlusionMapRects[i] : new Rect(0, 0, 1, 1),
                            emissionMapRect = atlasSet.hasEmission ? atlasSet.emissionMapRects[i] : new Rect(0, 0, 1, 1)
                        };
                    }
                }

                // Step 6: Remap UVs for all meshes (including multi-material meshes!)
                int meshesRemapped = RemapMeshUVsForGroup_Enhanced(group.materials, materialToRects, groupIndex, timestamp);

                EditorUtility.DisplayProgressBar("Generating Multi-Texture Atlas", "Applying shared material...", 0.9f);

                // Step 7: Apply shared material to all renderers
                int referencesReplaced = ApplySharedMaterialToGroup_Enhanced(group.materials, sharedMaterial, materialToRects.Keys.ToList());

                // Clean up temporary atlas textures
                if (atlasSet.baseAtlas != null) DestroyImmediate(atlasSet.baseAtlas);
                if (atlasSet.normalAtlas != null) DestroyImmediate(atlasSet.normalAtlas);
                if (atlasSet.metallicAtlas != null) DestroyImmediate(atlasSet.metallicAtlas);
                if (atlasSet.occlusionAtlas != null) DestroyImmediate(atlasSet.occlusionAtlas);
                if (atlasSet.emissionAtlas != null) DestroyImmediate(atlasSet.emissionAtlas);

                EditorUtility.ClearProgressBar();

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log($"[MaterialAtlasOptimizer] ENHANCED Atlas Generation Complete:\n" +
                          $"  Base Atlas: {baseAtlasPath}\n" +
                          $"  Normal Atlas: {(normalAtlasPath ?? "None")}\n" +
                          $"  Metallic Atlas: {(metallicAtlasPath ?? "None")}\n" +
                          $"  Occlusion Atlas: {(occlusionAtlasPath ?? "None")}\n" +
                          $"  Emission Atlas: {(emissionAtlasPath ?? "None")}\n" +
                          $"  Materials merged: {group.materials.Count} → 1\n" +
                          $"  Meshes remapped: {meshesRemapped}\n" +
                          $"  Multi-material meshes: SUPPORTED ✓\n" +
                          $"  References replaced: {referencesReplaced}");

                EditorUtility.DisplayDialog("Enhanced Atlas Generated",
                    $"Successfully created multi-texture atlas!\n\n" +
                    $"Materials merged: {group.materials.Count} → 1\n" +
                    $"Meshes remapped: {meshesRemapped}\n" +
                    $"Multi-material support: YES ✓\n" +
                    $"Texture maps atlased: {1 + (atlasSet.hasNormals ? 1 : 0) + (atlasSet.hasMetallic ? 1 : 0) + (atlasSet.hasOcclusion ? 1 : 0) + (atlasSet.hasEmission ? 1 : 0)}\n\n" +
                    $"Draw calls saved: {group.materials.Count - 1}",
                    "OK");

                ScanScene();
                return true;
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"[MaterialAtlasOptimizer] Error generating enhanced atlas: {e.Message}\n{e.StackTrace}");
                EditorUtility.DisplayDialog("Error", $"Failed to generate atlas:\n{e.Message}", "OK");
                return false;
            }
        }
        /// <summary>
        /// Creates ONE shared URP material for the entire atlas group.
        /// All objects will use this same material with remapped UVs.
        /// </summary>
        private Material CreateSharedURPMaterial(AtlasGroup group, Texture2D atlas, int groupIndex)
        {
            EnsureDirectoryExists(MATERIAL_SAVE_PATH);

            // Use a representative material from the group for property copying
            var representativeMat = group.materials[0].material;

            // Create new material with same shader
            Material sharedMat = new Material(group.shader);

            // FIXED: Add timestamp to avoid asset name collisions on re-run
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            sharedMat.name = $"URP_Atlas_{group.shader.name.Replace("/", "_").Replace(" ", "")}_Group{groupIndex}_{timestamp}";

            // Set atlas texture as _BaseMap (URP property)
            sharedMat.mainTexture = atlas;

            // CRITICAL: Set scale/offset to identity - UVs are now baked into meshes
            sharedMat.mainTextureScale = Vector2.one;
            sharedMat.mainTextureOffset = Vector2.zero;

            // Copy URP properties from representative material
            CopyURPMaterialProperties(representativeMat, sharedMat);

            // Save material asset
            string matPath = Path.Combine(MATERIAL_SAVE_PATH, $"{sharedMat.name}.mat").Replace("\\", "/");
            AssetDatabase.CreateAsset(sharedMat, matPath);

            Debug.Log($"[MaterialAtlasOptimizer] Created shared URP material: {matPath}");

            return sharedMat;
        }

        /// <summary>
        /// ENHANCED: Remaps mesh UVs with full multi-material support.
        /// Each submesh is remapped to its material's specific atlas region.
        /// </summary>
        private int RemapMeshUVsForGroup_Enhanced(
            List<MaterialInfo> materialInfos,
            Dictionary<Material, SubmeshAtlasMapping> materialToRects,
            int groupIndex,
            string timestamp)
        {
            EnsureDirectoryExists(MESH_SAVE_PATH);

            int meshesRemapped = 0;
            var processedMeshes = new Dictionary<int, Mesh>(); // Original mesh ID → Remapped mesh

            // Process each unique renderer
            var processedRenderers = new HashSet<MeshRenderer>();

            foreach (var matInfo in materialInfos)
            {
                if (matInfo.material == null) continue;

                foreach (var renderer in matInfo.usedByRenderers)
                {
                    if (renderer == null || processedRenderers.Contains(renderer))
                        continue;

                    processedRenderers.Add(renderer);

                    var meshFilter = renderer.GetComponent<MeshFilter>();
                    if (meshFilter == null || meshFilter.sharedMesh == null)
                        continue;

                    var originalMesh = meshFilter.sharedMesh;
                    int meshID = originalMesh.GetInstanceID();

                    // Check if we already processed this mesh
                    if (processedMeshes.ContainsKey(meshID))
                    {
                        meshFilter.sharedMesh = processedMeshes[meshID];
                        continue;
                    }

                    // Build submesh mappings for this renderer
                    var submeshMappings = new List<SubmeshAtlasMapping>();
                    var sharedMaterials = renderer.sharedMaterials;

                    for (int submeshIndex = 0; submeshIndex < sharedMaterials.Length; submeshIndex++)
                    {
                        var mat = sharedMaterials[submeshIndex];
                        if (mat != null && materialToRects.ContainsKey(mat))
                        {
                            var mapping = materialToRects[mat];
                            var submeshMapping = new SubmeshAtlasMapping
                            {
                                submeshIndex = submeshIndex,
                                originalMaterial = mat,
                                baseMapRect = mapping.baseMapRect,
                                normalMapRect = mapping.normalMapRect,
                                metallicMapRect = mapping.metallicMapRect,
                                occlusionMapRect = mapping.occlusionMapRect,
                                emissionMapRect = mapping.emissionMapRect
                            };
                            submeshMappings.Add(submeshMapping);
                        }
                    }

                    if (submeshMappings.Count == 0)
                        continue;

                    // Remap mesh UVs (handles multi-material!)
                    Mesh remappedMesh = RemapMultiMaterialMeshUVs(originalMesh, submeshMappings);
                    remappedMesh.name = $"{originalMesh.name}_Atlased_G{groupIndex}_{timestamp}";

                    // Save remapped mesh
                    string meshPath = Path.Combine(MESH_SAVE_PATH, $"{remappedMesh.name}.asset").Replace("\\", "/");
                    AssetDatabase.CreateAsset(remappedMesh, meshPath);

                    // Store and apply
                    processedMeshes[meshID] = remappedMesh;
                    Undo.RecordObject(meshFilter, "Remap mesh UVs for atlas");
                    meshFilter.sharedMesh = remappedMesh;
                    EditorUtility.SetDirty(meshFilter);

                    meshesRemapped++;
                }
            }

            return meshesRemapped;
        }


        /// <summary>
        /// Applies the single shared atlas material to all renderers in the group.
        /// Replaces all original materials with the one shared material.
        /// CRITICAL: Only applies to single-material renderers that had their UVs remapped.
        /// Multi-material renderers are skipped (they weren't UV-remapped).
        /// </summary>

        /// <summary>
        /// ENHANCED: Applies shared material to all renderers (including multi-material)
        /// </summary>
        private int ApplySharedMaterialToGroup_Enhanced(
            List<MaterialInfo> materialInfos,
            Material sharedMaterial,
            List<Material> originalMaterials)
        {
            int referencesReplaced = 0;
            var scene = SceneManager.GetActiveScene();
            var originalMaterialSet = new HashSet<Material>(originalMaterials);

            // Process all renderers
            var processedRenderers = new HashSet<MeshRenderer>();

            foreach (var matInfo in materialInfos)
            {
                foreach (var renderer in matInfo.usedByRenderers)
                {
                    if (renderer == null || processedRenderers.Contains(renderer))
                        continue;

                    processedRenderers.Add(renderer);

                    bool modified = false;
                    var sharedMaterials = renderer.sharedMaterials;

                    // Replace all materials that were in the atlas group
                    for (int i = 0; i < sharedMaterials.Length; i++)
                    {
                        if (originalMaterialSet.Contains(sharedMaterials[i]))
                        {
                            if (!modified)
                            {
                                Undo.RecordObject(renderer, "Apply shared atlas material");
                                modified = true;
                            }

                            sharedMaterials[i] = sharedMaterial;
                            referencesReplaced++;
                        }
                    }

                    if (modified)
                    {
                        renderer.sharedMaterials = sharedMaterials;
                        EditorUtility.SetDirty(renderer);
                    }
                }
            }

            if (referencesReplaced > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }

            return referencesReplaced;
        }





        /// <summary>
        /// Creates shared URP material with all atlas textures assigned
        /// </summary>
        private Material CreateSharedURPMaterial_Enhanced(
            AtlasGroup group,
            string baseAtlasPath,
            string normalAtlasPath,
            string metallicAtlasPath,
            string occlusionAtlasPath,
            string emissionAtlasPath,
            int groupIndex,
            string timestamp)
        {
            EnsureDirectoryExists(MATERIAL_SAVE_PATH);

            var representativeMat = group.materials[0].material;
            Material sharedMat = new Material(group.shader);
            sharedMat.name = $"URP_AtlasMaterial_{group.shader.name.Replace("/", "_").Replace(" ", "")}_Group{groupIndex}_{timestamp}";

            // Load and assign all atlas textures
            if (!string.IsNullOrEmpty(baseAtlasPath))
            {
                Texture2D baseAtlas = AssetDatabase.LoadAssetAtPath<Texture2D>(baseAtlasPath);
                sharedMat.SetTexture("_BaseMap", baseAtlas);
                sharedMat.mainTexture = baseAtlas;
            }

            if (!string.IsNullOrEmpty(normalAtlasPath))
            {
                Texture2D normalAtlas = AssetDatabase.LoadAssetAtPath<Texture2D>(normalAtlasPath);
                sharedMat.SetTexture("_BumpMap", normalAtlas);
                sharedMat.EnableKeyword("_NORMALMAP");
            }

            if (!string.IsNullOrEmpty(metallicAtlasPath))
            {
                Texture2D metallicAtlas = AssetDatabase.LoadAssetAtPath<Texture2D>(metallicAtlasPath);
                sharedMat.SetTexture("_MetallicGlossMap", metallicAtlas);
                sharedMat.EnableKeyword("_METALLICSPECGLOSSMAP");
            }

            if (!string.IsNullOrEmpty(occlusionAtlasPath))
            {
                Texture2D occlusionAtlas = AssetDatabase.LoadAssetAtPath<Texture2D>(occlusionAtlasPath);
                sharedMat.SetTexture("_OcclusionMap", occlusionAtlas);
            }

            if (!string.IsNullOrEmpty(emissionAtlasPath))
            {
                Texture2D emissionAtlas = AssetDatabase.LoadAssetAtPath<Texture2D>(emissionAtlasPath);
                sharedMat.SetTexture("_EmissionMap", emissionAtlas);
                sharedMat.EnableKeyword("_EMISSION");
            }

            // CRITICAL: Set scale/offset to identity - UVs are baked into meshes
            sharedMat.mainTextureScale = Vector2.one;
            sharedMat.mainTextureOffset = Vector2.zero;

            // Copy other material properties
            CopyURPMaterialProperties(representativeMat, sharedMat);

            // Save material asset
            string matPath = Path.Combine(MATERIAL_SAVE_PATH, $"{sharedMat.name}.mat").Replace("\\", "/");
            AssetDatabase.CreateAsset(sharedMat, matPath);

            Debug.Log($"[MaterialAtlasOptimizer] Created enhanced URP material: {matPath}");

            return sharedMat;
        }


        private void GenerateAllUVRemappedAtlases()
        {
            int successCount = 0;
            int failCount = 0;

            for (int i = 0; i < atlasGroups.Count; i++)
            {
                try
                {
                    // FIXED: Track actual success/failure based on return value
                    bool success = GenerateUVRemappedAtlas_Enhanced(atlasGroups[i], i);
                    if (success)
                        successCount++;
                    else
                        failCount++; // Count early returns as failures
                }
                catch (Exception e)
                {
                    Debug.LogError($"[MaterialAtlasOptimizer] Failed to generate atlas {i}: {e.Message}");
                    failCount++;
                }
            }

            EditorUtility.DisplayDialog("Bulk UV-Remapped Atlas Generation Complete",
                $"Successfully generated: {successCount}\nFailed/Skipped: {failCount}",
                "OK");
        }

        #endregion

        #region Material Property Copying

        private void CopyURPMaterialProperties(Material source, Material destination)
        {
            // Copy URP-specific properties
            if (source.HasProperty("_BaseColor") && destination.HasProperty("_BaseColor"))
                destination.SetColor("_BaseColor", source.GetColor("_BaseColor"));
            else if (source.HasProperty("_Color") && destination.HasProperty("_Color"))
                destination.SetColor("_Color", source.GetColor("_Color"));

            if (source.HasProperty("_Metallic") && destination.HasProperty("_Metallic"))
                destination.SetFloat("_Metallic", source.GetFloat("_Metallic"));

            if (source.HasProperty("_Smoothness") && destination.HasProperty("_Smoothness"))
                destination.SetFloat("_Smoothness", source.GetFloat("_Smoothness"));

            if (source.HasProperty("_BumpScale") && destination.HasProperty("_BumpScale"))
                destination.SetFloat("_BumpScale", source.GetFloat("_BumpScale"));

            if (source.HasProperty("_EmissionColor") && destination.HasProperty("_EmissionColor"))
                destination.SetColor("_EmissionColor", source.GetColor("_EmissionColor"));

            if (source.HasProperty("_OcclusionStrength") && destination.HasProperty("_OcclusionStrength"))
                destination.SetFloat("_OcclusionStrength", source.GetFloat("_OcclusionStrength"));

            // Copy render queue and keywords
            destination.renderQueue = source.renderQueue;
            destination.shaderKeywords = source.shaderKeywords;
            destination.globalIlluminationFlags = source.globalIlluminationFlags;

            // Copy render state
            if (source.HasProperty("_Surface") && destination.HasProperty("_Surface"))
                destination.SetFloat("_Surface", source.GetFloat("_Surface"));

            if (source.HasProperty("_Blend") && destination.HasProperty("_Blend"))
                destination.SetFloat("_Blend", source.GetFloat("_Blend"));

            if (source.HasProperty("_Cull") && destination.HasProperty("_Cull"))
                destination.SetFloat("_Cull", source.GetFloat("_Cull"));
        }

        #endregion

        #region Duplicate Merging

        private void MergeDuplicateMaterials(List<MaterialInfo> group)
        {
            if (group == null || group.Count < 2)
            {
                Debug.LogWarning("[MaterialAtlasOptimizer] Cannot merge: group has fewer than 2 materials");
                return;
            }

            var master = group.OrderByDescending(m => m.usedByCount).First();
            var toReplace = group.Where(m => m != master).ToList();

            Debug.Log($"[MaterialAtlasOptimizer] Merging {toReplace.Count} materials into '{master.material.name}'");

            AssetDatabase.StartAssetEditing();
            try
            {
                foreach (var duplicate in toReplace)
                {
                    ReplaceMaterialReferences(duplicate.material, master.material);
                }

                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log($"[MaterialAtlasOptimizer] Merge complete. Replaced {toReplace.Count} materials.");

                ScanScene();
            }
            catch (Exception e)
            {
                AssetDatabase.StopAssetEditing();
                Debug.LogError($"[MaterialAtlasOptimizer] Error during merge: {e.Message}");
            }
        }

        private void MergeAllDuplicates()
        {
            int totalMerged = 0;
            AssetDatabase.StartAssetEditing();

            try
            {
                foreach (var group in duplicateGroups)
                {
                    if (group.Count < 2) continue;

                    var master = group.OrderByDescending(m => m.usedByCount).First();
                    var toReplace = group.Where(m => m != master).ToList();

                    foreach (var duplicate in toReplace)
                    {
                        ReplaceMaterialReferences(duplicate.material, master.material);
                        totalMerged++;
                    }
                }

                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log($"[MaterialAtlasOptimizer] Bulk merge complete. Merged {totalMerged} materials.");

                ScanScene();
            }
            catch (Exception e)
            {
                AssetDatabase.StopAssetEditing();
                Debug.LogError($"[MaterialAtlasOptimizer] Error during bulk merge: {e.Message}");
            }
        }

        private void ReplaceMaterialReferences(Material oldMaterial, Material newMaterial)
        {
            if (oldMaterial == null || newMaterial == null)
            {
                Debug.LogWarning("[MaterialAtlasOptimizer] Cannot replace: null material");
                return;
            }

            var scene = SceneManager.GetActiveScene();
            var rootObjects = scene.GetRootGameObjects();
            int replacementCount = 0;

            foreach (var root in rootObjects)
            {
                var renderers = root.GetComponentsInChildren<Renderer>(true);
                foreach (var renderer in renderers)
                {
                    bool modified = false;
                    var sharedMaterials = renderer.sharedMaterials;

                    for (int i = 0; i < sharedMaterials.Length; i++)
                    {
                        if (sharedMaterials[i] == oldMaterial)
                        {
                            if (!modified)
                            {
                                Undo.RecordObject(renderer, "Replace material");
                                modified = true;
                            }

                            sharedMaterials[i] = newMaterial;
                            replacementCount++;
                        }
                    }

                    if (modified)
                    {
                        renderer.sharedMaterials = sharedMaterials;
                        EditorUtility.SetDirty(renderer);
                    }
                }
            }

            if (replacementCount > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
                Debug.Log($"[MaterialAtlasOptimizer] Replaced {replacementCount} references from '{oldMaterial.name}' to '{newMaterial.name}'");
            }
        }

        #endregion

        #region Utility Methods

        /// <summary>
        /// Configures atlas texture with Quest 3 optimized settings:
        /// - Clamp wrap mode (prevents bleeding between atlas regions)
        /// - ASTC compression (Quest 3 native format)
        /// - Proper sRGB settings per texture type
        /// - Mipmaps enabled with proper filtering
        /// </summary>
        private void ConfigureAtlasImporter(string atlasPath, bool isNormalMap, bool isColorData)
        {
            TextureImporter importer = AssetImporter.GetAtPath(atlasPath) as TextureImporter;
            if (importer == null) return;

            // CRITICAL: Clamp wrap mode prevents sampling adjacent atlas regions
            importer.wrapMode = TextureWrapMode.Clamp;

            // Enable mipmaps (essential for VR performance)
            importer.mipmapEnabled = true;
            importer.mipmapFilter = TextureImporterMipFilter.KaiserFilter;

            // Normal map specific settings
            if (isNormalMap)
            {
                importer.textureType = TextureImporterType.NormalMap;
                importer.sRGBTexture = false;  // Normal maps are linear data
            }
            else if (isColorData)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = true;  // Color data needs gamma correction
            }
            else
            {
                // Metallic, Occlusion maps are linear data
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false;
            }

            // Quest 3 / Android platform settings
            var androidSettings = importer.GetPlatformTextureSettings("Android");
            androidSettings.overridden = true;
            androidSettings.format = TextureImporterFormat.ASTC_6x6;  // Good balance for Quest 3
            androidSettings.maxTextureSize = 2048;  // Quest 3 recommended max
            importer.SetPlatformTextureSettings(androidSettings);

            // Standalone (PC) for testing
            var standaloneSettings = importer.GetPlatformTextureSettings("Standalone");
            standaloneSettings.overridden = true;
            standaloneSettings.format = TextureImporterFormat.DXT5;
            standaloneSettings.maxTextureSize = 2048;
            importer.SetPlatformTextureSettings(standaloneSettings);

            importer.isReadable = false;  // Save memory
            importer.maxTextureSize = 2048;  // Recommended for Quest 3

            importer.SaveAndReimport();

            Debug.Log($"[MaterialAtlasOptimizer] Configured atlas texture: {atlasPath} (isNormal: {isNormalMap}, sRGB: {isColorData})");
        }


        private void EnsureDirectoryExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            path = path.Replace("\\", "/").TrimEnd('/');

            if (AssetDatabase.IsValidFolder(path))
                return;

            string parentPath = Path.GetDirectoryName(path)?.Replace("\\", "/");
            string folderName = Path.GetFileName(path);

            if (string.IsNullOrEmpty(parentPath) || string.IsNullOrEmpty(folderName))
                return;

            if (!AssetDatabase.IsValidFolder(parentPath))
            {
                EnsureDirectoryExists(parentPath);
            }

            AssetDatabase.CreateFolder(parentPath, folderName);
        }

        private int CalculateTotalDrawCallSavings()
        {
            int savings = 0;

            foreach (var group in duplicateGroups)
            {
                if (group.Count >= 2)
                {
                    savings += group.Count - 1;
                }
            }

            foreach (var group in atlasGroups)
            {
                if (group.materials.Count >= 2)
                {
                    savings += group.materials.Count - 1;
                }
            }

            return savings;
        }

        #endregion
    }
}
#endif