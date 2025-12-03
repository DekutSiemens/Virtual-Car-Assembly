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

            EditorGUILayout.LabelField($"Atlas Group {groupIndex + 1}: {group.materials.Count} materials", EditorStyles.boldLabel);

            if (group.shader != null)
            {
                EditorGUILayout.LabelField($"Shader: {group.shader.name}", EditorStyles.miniLabel);
            }
            EditorGUILayout.LabelField($"Texture Size: {group.textureSize.x}×{group.textureSize.y}", EditorStyles.miniLabel);
            EditorGUILayout.LabelField($"Estimated Atlas Size: {group.estimatedAtlasSize}×{group.estimatedAtlasSize}", EditorStyles.miniLabel);

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
                    GenerateUVRemappedAtlas(group, groupIndex);
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
        /// </summary>
        private void IdentifyAtlasCandidates()
        {
            atlasGroups.Clear();

            var groups = foundMaterials
                .Where(m => m.material != null &&
                           m.shader != null &&
                           m.isURPCompatible &&
                           m.mainTexture != null &&
                           m.mainTexture is Texture2D &&
                           m.mainTextureScale == Vector2.one &&
                           m.mainTextureOffset == Vector2.zero)
                .GroupBy(m => new
                {
                    shaderName = m.shader.name,
                    textureWidth = (int)m.textureSize.x,
                    textureHeight = (int)m.textureSize.y,
                    keywords = string.Join(",", m.shaderKeywords.OrderBy(k => k))
                })
                .Where(g => g.Count() >= 2)
                .OrderByDescending(g => g.Count());

            foreach (var group in groups)
            {
                var materials = group.ToList();
                var first = materials[0];

                var atlasGroup = new AtlasGroup
                {
                    materials = materials,
                    shader = first.shader,
                    textureSize = first.textureSize,
                    sharedKeywords = first.shaderKeywords,
                    estimatedAtlasSize = CalculateRequiredAtlasSize(materials.Count, first.textureSize)
                };

                atlasGroups.Add(atlasGroup);
            }
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

        #region UV-Remapped Atlas Generation

        /// <summary>
        /// Generates a texture atlas with UV remapping for true draw call batching.
        /// Creates ONE shared material per group with remapped mesh UVs.
        /// Returns true on success, false on failure or early exit.
        /// </summary>
        private bool GenerateUVRemappedAtlas(AtlasGroup group, int groupIndex)
        {
            if (group == null || group.materials.Count < 2)
            {
                Debug.LogWarning("[MaterialAtlasOptimizer] Cannot generate atlas: insufficient materials");
                return false;
            }

            EditorUtility.DisplayProgressBar("Generating UV-Remapped Atlas", "Preparing textures...", 0f);

            try
            {
                // Step 1: Collect and prepare textures
                var textures = new List<Texture2D>();
                var textureImporters = new List<TextureImporter>();
                var usedMaterialInfos = new List<MaterialInfo>();

                foreach (var matInfo in group.materials)
                {
                    if (matInfo.mainTexture == null)
                    {
                        Debug.LogWarning($"[MaterialAtlasOptimizer] Skipping '{matInfo.material.name}': no main texture");
                        continue;
                    }

                    Texture2D tex = matInfo.mainTexture as Texture2D;
                    if (tex == null)
                    {
                        Debug.LogWarning($"[MaterialAtlasOptimizer] Skipping '{matInfo.material.name}': texture is not Texture2D");
                        continue;
                    }

                    // Make texture readable
                    string texPath = AssetDatabase.GetAssetPath(tex);
                    if (!string.IsNullOrEmpty(texPath))
                    {
                        TextureImporter importer = AssetImporter.GetAtPath(texPath) as TextureImporter;
                        if (importer != null && !importer.isReadable)
                        {
                            importer.isReadable = true;
                            importer.SaveAndReimport();
                            textureImporters.Add(importer);
                        }
                    }

                    textures.Add(tex);
                    usedMaterialInfos.Add(matInfo);
                }

                if (textures.Count < 2)
                {
                    Debug.LogWarning("[MaterialAtlasOptimizer] Not enough valid textures to create atlas");
                    EditorUtility.ClearProgressBar();
                    return false;
                }

                EditorUtility.DisplayProgressBar("Generating UV-Remapped Atlas", "Packing textures...", 0.2f);

                // Step 2: Create atlas texture
                int atlasSize = Mathf.Min(group.estimatedAtlasSize, maxAtlasSize);
                Texture2D atlas = new Texture2D(atlasSize, atlasSize, TextureFormat.RGBA32, true);
                Rect[] uvRects = atlas.PackTextures(textures.ToArray(), atlasPadding, atlasSize, false);

                if (uvRects == null || uvRects.Length != textures.Count)
                {
                    Debug.LogError("[MaterialAtlasOptimizer] PackTextures failed");
                    DestroyImmediate(atlas); // FIXED: Clean up temporary atlas texture
                    EditorUtility.ClearProgressBar();
                    return false;
                }

                EditorUtility.DisplayProgressBar("Generating UV-Remapped Atlas", "Saving atlas texture...", 0.4f);

                // Step 3: Save atlas texture
                EnsureDirectoryExists(ATLAS_SAVE_PATH);
                string atlasName = $"URP_Atlas_{group.shader.name.Replace("/", "_").Replace(" ", "")}_Group{groupIndex}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string atlasPath = Path.Combine(ATLAS_SAVE_PATH, atlasName).Replace("\\", "/");

                byte[] pngData = atlas.EncodeToPNG();
                File.WriteAllBytes(atlasPath, pngData);

                // FIXED: Clean up temporary atlas texture to avoid editor memory buildup
                DestroyImmediate(atlas);

                AssetDatabase.ImportAsset(atlasPath);

                ConfigureAtlasImporter(atlasPath);

                EditorUtility.DisplayProgressBar("Generating UV-Remapped Atlas", "Creating shared URP material...", 0.5f);

                // Step 4: Create ONE shared material for the entire group
                Texture2D importedAtlas = AssetDatabase.LoadAssetAtPath<Texture2D>(atlasPath);
                Material sharedAtlasMaterial = CreateSharedURPMaterial(group, importedAtlas, groupIndex);

                EditorUtility.DisplayProgressBar("Generating UV-Remapped Atlas", "Remapping mesh UVs...", 0.6f);

                // Step 5: Remap mesh UVs for all objects in the group
                int meshesRemapped = RemapMeshUVsForGroup(usedMaterialInfos, uvRects, groupIndex);

                EditorUtility.DisplayProgressBar("Generating UV-Remapped Atlas", "Applying shared material...", 0.9f);

                // Step 6: Apply the single shared material to all renderers
                int referencesReplaced = ApplySharedMaterialToGroup(usedMaterialInfos, sharedAtlasMaterial);

                // Revert texture readable settings
                foreach (var importer in textureImporters)
                {
                    importer.isReadable = false;
                    importer.SaveAndReimport();
                }

                EditorUtility.ClearProgressBar();

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log($"[MaterialAtlasOptimizer] UV-Remapped Atlas Generation Complete:\n" +
                          $"  Atlas: {atlasName}\n" +
                          $"  Materials merged: {usedMaterialInfos.Count} → 1\n" +
                          $"  Meshes remapped: {meshesRemapped}\n" +
                          $"  References replaced: {referencesReplaced}\n" +
                          $"  Draw call savings: {usedMaterialInfos.Count - 1}");

                EditorUtility.DisplayDialog("UV-Remapped Atlas Generated",
                    $"Successfully created UV-remapped atlas!\n\n" +
                    $"Materials merged: {usedMaterialInfos.Count} → 1\n" +
                    $"Meshes remapped: {meshesRemapped}\n" +
                    $"Draw calls saved: {usedMaterialInfos.Count - 1}\n\n" +
                    $"Atlas: {atlasName}",
                    "OK");

                ScanScene();
                return true; // FIXED: Return success
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"[MaterialAtlasOptimizer] Error generating UV-remapped atlas: {e.Message}\n{e.StackTrace}");
                EditorUtility.DisplayDialog("Error", $"Failed to generate atlas:\n{e.Message}", "OK");
                return false; // FIXED: Return failure
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
        /// Remaps mesh UVs for all objects using materials in this group.
        /// Each unique (mesh, material) combination is cloned ONCE and UVs are transformed to match atlas layout.
        /// The cloned mesh is then assigned to ALL objects that share the same (mesh, material) combination.
        /// 
        /// CRITICAL: Different materials on the same mesh require different UV mappings,
        /// so we key by BOTH mesh and material, not just mesh.
        /// 
        /// LIMITATION: Multi-material meshes are NOT supported and will be skipped entirely.
        /// UV remapping affects the entire mesh, not per-submesh.
        /// </summary>
        private int RemapMeshUVsForGroup(List<MaterialInfo> materialInfos, Rect[] uvRects, int groupIndex)
        {
            EnsureDirectoryExists(MESH_SAVE_PATH);

            int meshesRemapped = 0;

            // FIXED: Key by (mesh, material) combination, not just mesh
            // Same mesh with different materials needs different UV remapping
            var meshMatToCloned = new Dictionary<string, Mesh>();

            // Build set of all materials that belong to this atlas group
            var groupMaterials = new HashSet<Material>(
                materialInfos.Where(mi => mi.material != null).Select(mi => mi.material)
            );

            for (int matIndex = 0; matIndex < materialInfos.Count && matIndex < uvRects.Length; matIndex++)
            {
                var matInfo = materialInfos[matIndex];
                var atlasRect = uvRects[matIndex];
                var sourceMat = matInfo.material;

                if (sourceMat == null) continue;

                foreach (var gameObject in matInfo.usedByObjects)
                {
                    if (gameObject == null) continue;

                    var meshFilter = gameObject.GetComponent<MeshFilter>();
                    var meshRenderer = gameObject.GetComponent<MeshRenderer>();

                    if (meshFilter == null || meshRenderer == null)
                    {
                        Debug.LogWarning($"[MaterialAtlasOptimizer] Skipping '{gameObject.name}': missing MeshFilter or MeshRenderer");
                        continue;
                    }

                    var originalMesh = meshFilter.sharedMesh;
                    if (originalMesh == null)
                    {
                        Debug.LogWarning($"[MaterialAtlasOptimizer] Skipping '{gameObject.name}': no mesh");
                        continue;
                    }

                    // FIXED: Hard-skip ALL multi-material meshes (not just partial coverage)
                    // UV remapping affects entire mesh, we can't safely handle per-submesh yet
                    if (meshRenderer.sharedMaterials.Length > 1)
                    {
                        Debug.LogWarning(
                            $"[MaterialAtlasOptimizer] Skipping '{gameObject.name}': multi-material mesh. " +
                            "Current UV remapper only supports single-material meshes safely."
                        );
                        continue;
                    }

                    // FIXED: Create composite key per (mesh, material) combination
                    string key = originalMesh.GetInstanceID() + "_" + sourceMat.GetInstanceID();

                    // Get or create the cloned mesh for this (mesh, material) pair
                    if (!meshMatToCloned.TryGetValue(key, out var clonedMesh))
                    {
                        // First time seeing this (mesh, material) pair - clone and remap it
                        clonedMesh = UnityEngine.Object.Instantiate(originalMesh);
                        clonedMesh.name = $"{originalMesh.name}_Atlased_G{groupIndex}_M{matIndex}";

                        // Remap UVs to atlas rect for THIS material's texture
                        Vector2[] uvs = clonedMesh.uv;
                        for (int i = 0; i < uvs.Length; i++)
                        {
                            Vector2 uv = uvs[i];
                            // Transform UV from [0,1] space to atlas rect space
                            uv.x = atlasRect.x + uv.x * atlasRect.width;
                            uv.y = atlasRect.y + uv.y * atlasRect.height;
                            uvs[i] = uv;
                        }
                        clonedMesh.uv = uvs;

                        // FIXED: Include timestamp to avoid asset name collisions on re-run
                        string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                        string meshPath = Path.Combine(MESH_SAVE_PATH, $"{clonedMesh.name}_{timestamp}.asset").Replace("\\", "/");
                        AssetDatabase.CreateAsset(clonedMesh, meshPath);

                        // Store mapping for future objects that share this (mesh, material) combo
                        meshMatToCloned[key] = clonedMesh;
                        meshesRemapped++;
                    }

                    // FIXED: Assign cloned mesh to EVERY object that uses this (mesh, material) combo
                    Undo.RecordObject(meshFilter, "Remap mesh UVs for atlas");
                    meshFilter.sharedMesh = clonedMesh;
                    EditorUtility.SetDirty(meshFilter);
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
        private int ApplySharedMaterialToGroup(List<MaterialInfo> materialInfos, Material sharedMaterial)
        {
            int referencesReplaced = 0;
            var scene = SceneManager.GetActiveScene();

            // Build a set of all original materials in this group
            var originalMaterials = new HashSet<Material>();
            foreach (var matInfo in materialInfos)
            {
                if (matInfo.material != null)
                {
                    originalMaterials.Add(matInfo.material);
                }
            }

            // Find all objects that used any of these materials
            var allObjects = new HashSet<GameObject>();
            foreach (var matInfo in materialInfos)
            {
                foreach (var obj in matInfo.usedByObjects)
                {
                    if (obj != null) allObjects.Add(obj);
                }
            }

            // Replace materials on all affected renderers
            foreach (var obj in allObjects)
            {
                var renderer = obj.GetComponent<MeshRenderer>();
                if (renderer == null) continue;

                // CRITICAL: Skip multi-material meshes - we never remapped their UVs
                // Applying atlas material without UV remap would cause wrong texture sampling
                if (renderer.sharedMaterials.Length > 1)
                {
                    continue;
                }

                bool modified = false;
                var sharedMaterials = renderer.sharedMaterials;

                for (int i = 0; i < sharedMaterials.Length; i++)
                {
                    if (originalMaterials.Contains(sharedMaterials[i]))
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

            if (referencesReplaced > 0)
            {
                EditorSceneManager.MarkSceneDirty(scene);
            }

            return referencesReplaced;
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
                    bool success = GenerateUVRemappedAtlas(atlasGroups[i], i);
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

        private void ConfigureAtlasImporter(string atlasPath)
        {
            TextureImporter importer = AssetImporter.GetAtPath(atlasPath) as TextureImporter;
            if (importer != null)
            {
                // URP-friendly texture import settings
                importer.isReadable = false;
                importer.sRGBTexture = true;
                importer.mipmapEnabled = true;
                importer.textureType = TextureImporterType.Default;
                importer.maxTextureSize = maxAtlasSize;
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.wrapMode = TextureWrapMode.Clamp; // Reduce atlas edge bleeding
                importer.SaveAndReimport();
            }
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