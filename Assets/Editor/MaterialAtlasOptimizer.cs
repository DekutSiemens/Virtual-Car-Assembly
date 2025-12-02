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
    /// Production-ready Unity Editor tool for material optimization and automatic texture atlasing.
    /// Designed for large VR factory scenes targeting Meta Quest 3.
    /// Reduces draw calls through material consolidation and texture atlas generation.
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
            public Color mainColor;
            public float metallic;
            public float smoothness;
            public string[] shaderKeywords;
            public int renderQueue;

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

                    // Cache common properties
                    if (mat.HasProperty("_Color")) mainColor = mat.GetColor("_Color");
                    if (mat.HasProperty("_Metallic")) metallic = mat.GetFloat("_Metallic");
                    if (mat.HasProperty("_Glossiness")) smoothness = mat.GetFloat("_Glossiness");
                    else if (mat.HasProperty("_Smoothness")) smoothness = mat.GetFloat("_Smoothness");

                    shaderKeywords = mat.shaderKeywords ?? new string[0];
                    renderQueue = mat.renderQueue;
                }
            }
        }

        /// <summary>
        /// Represents a group of materials that can be atlased together.
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

        #endregion

        #region Menu Item

        [MenuItem("Tools/VR Optimization/Material & Atlas Optimizer")]
        public static void ShowWindow()
        {
            var window = GetWindow<MaterialAtlasOptimizer>("Material Atlas Optimizer");
            window.minSize = new Vector2(600, 400);
            window.Show();
        }

        #endregion

        #region GUI

        private void OnGUI()
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Header
            EditorGUILayout.LabelField("Material Atlas Optimizer", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("VR Optimization Tool for Meta Quest 3", EditorStyles.miniLabel);
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

            EditorGUILayout.Space();

            if (GUILayout.Button("Scan Active Scene", GUILayout.Height(40)))
            {
                ScanScene();
            }

            EditorGUILayout.Space();

            if (foundMaterials.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    $"Found {foundMaterials.Count} materials in scene.\n" +
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
            EditorGUILayout.HelpBox("Materials that can be combined into texture atlases to reduce draw calls.", MessageType.Info);
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
            if (GUILayout.Button($"Generate Atlas for Group {groupIndex + 1}", GUILayout.Height(30)))
            {
                GenerateAtlas(group, groupIndex);
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
            if (GUILayout.Button($"Generate All Atlases ({atlasGroups.Count})", GUILayout.Height(35)))
            {
                if (EditorUtility.DisplayDialog("Generate All Atlases",
                    $"This will generate {atlasGroups.Count} texture atlases.\n\nThis may take several minutes.\n\nContinue?",
                    "Yes", "Cancel"))
                {
                    GenerateAllAtlases();
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
        /// Scans the active scene for all materials and analyzes them.
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

            foreach (var root in rootObjects)
            {
                if (!includeInactive && !root.activeInHierarchy) continue;

                var renderers = root.GetComponentsInChildren<Renderer>(includeInactive);
                foreach (var renderer in renderers)
                {
                    ProcessRenderer(renderer, materialDict);
                }
            }

            foundMaterials = materialDict.Values.ToList();

            Debug.Log($"[MaterialAtlasOptimizer] Scan complete: {foundMaterials.Count} materials, {instanceMaterials.Count} instances");

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

        private void ProcessRenderer(Renderer renderer, Dictionary<string, MaterialInfo> materialDict)
        {
            if (renderer == null) return;

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
                .Where(m => m.material != null && m.shader != null)
                .GroupBy(m => new
                {
                    shaderName = m.shader.name,
                    textureName = m.mainTexture != null ? m.mainTexture.name : "null",
                    color = m.mainColor,
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
        /// Identifies groups of materials that can be safely atlased together.
        /// </summary>
        private void IdentifyAtlasCandidates()
        {
            atlasGroups.Clear();

            var groups = foundMaterials
                .Where(m => m.material != null &&
                           m.shader != null &&
                           m.mainTexture != null &&
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
            // Calculate required atlas dimensions
            float singleTexArea = textureSize.x * textureSize.y;
            float totalArea = singleTexArea * textureCount;
            int estimatedSize = Mathf.NextPowerOfTwo(Mathf.CeilToInt(Mathf.Sqrt(totalArea)));

            // Clamp to valid range
            estimatedSize = Mathf.Clamp(estimatedSize, 512, maxAtlasSize);

            return estimatedSize;
        }

        #endregion

        #region Duplicate Merging

        /// <summary>
        /// Merges duplicate materials by replacing all references with a single master material.
        /// </summary>
        private void MergeDuplicateMaterials(List<MaterialInfo> group)
        {
            if (group == null || group.Count < 2)
            {
                Debug.LogWarning("[MaterialAtlasOptimizer] Cannot merge: group has fewer than 2 materials");
                return;
            }

            // Choose the master material (prefer one with most usage)
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

                // Rescan to update UI
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

        #endregion

        #region Atlas Generation

        /// <summary>
        /// Generates a texture atlas for a group of compatible materials.
        /// </summary>
        private void GenerateAtlas(AtlasGroup group, int groupIndex)
        {
            if (group == null || group.materials.Count < 2)
            {
                Debug.LogWarning("[MaterialAtlasOptimizer] Cannot generate atlas: insufficient materials");
                return;
            }

            EditorUtility.DisplayProgressBar("Generating Atlas", "Preparing textures...", 0f);

            try
            {
                // Collect and prepare textures - FIXED: track which materials are actually used
                var textures = new List<Texture2D>();
                var textureImporters = new List<TextureImporter>();
                var usedMaterialInfos = new List<MaterialInfo>(); // Track materials that have valid textures

                foreach (var matInfo in group.materials)
                {
                    if (matInfo.mainTexture == null)
                    {
                        Debug.LogWarning($"[MaterialAtlasOptimizer] Skipping material '{matInfo.material.name}': no main texture");
                        continue;
                    }

                    Texture2D tex = matInfo.mainTexture as Texture2D;
                    if (tex == null)
                    {
                        Debug.LogWarning($"[MaterialAtlasOptimizer] Skipping material '{matInfo.material.name}': texture is not Texture2D");
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
                    usedMaterialInfos.Add(matInfo); // Keep mapping aligned
                }

                if (textures.Count < 2)
                {
                    Debug.LogWarning("[MaterialAtlasOptimizer] Not enough valid textures to create atlas");
                    EditorUtility.ClearProgressBar();
                    return;
                }

                EditorUtility.DisplayProgressBar("Generating Atlas", "Packing textures...", 0.3f);

                // FIXED: Use estimatedAtlasSize instead of ignoring it
                int atlasSize = Mathf.Min(group.estimatedAtlasSize, maxAtlasSize);
                Texture2D atlas = new Texture2D(atlasSize, atlasSize, TextureFormat.RGBA32, true);
                Rect[] uvRects = atlas.PackTextures(textures.ToArray(), atlasPadding, atlasSize, false);

                if (uvRects == null || uvRects.Length != textures.Count)
                {
                    Debug.LogError("[MaterialAtlasOptimizer] PackTextures failed");
                    EditorUtility.ClearProgressBar();
                    return;
                }

                EditorUtility.DisplayProgressBar("Generating Atlas", "Saving atlas texture...", 0.5f);

                // Save atlas texture
                EnsureDirectoryExists(ATLAS_SAVE_PATH);
                string atlasName = $"Atlas_{group.shader.name.Replace("/", "_")}_Group{groupIndex}_{DateTime.Now:yyyyMMdd_HHmmss}.png";
                string atlasPath = Path.Combine(ATLAS_SAVE_PATH, atlasName).Replace("\\", "/");

                byte[] pngData = atlas.EncodeToPNG();
                File.WriteAllBytes(atlasPath, pngData);
                AssetDatabase.ImportAsset(atlasPath);

                // Configure atlas import settings
                ConfigureAtlasImporter(atlasPath);

                EditorUtility.DisplayProgressBar("Generating Atlas", "Creating atlased materials...", 0.7f);

                // Create atlased materials - FIXED: pass usedMaterialInfos instead of group.materials
                Texture2D importedAtlas = AssetDatabase.LoadAssetAtPath<Texture2D>(atlasPath);
                var newMaterials = CreateAtlasedMaterials(usedMaterialInfos, importedAtlas, uvRects, groupIndex);

                EditorUtility.DisplayProgressBar("Generating Atlas", "Replacing scene references...", 0.9f);

                // Replace scene references - FIXED: pass usedMaterialInfos
                ReplaceSceneMaterialReferences(usedMaterialInfos, newMaterials);

                // Revert texture readable settings
                foreach (var importer in textureImporters)
                {
                    importer.isReadable = false;
                    importer.SaveAndReimport();
                }

                EditorUtility.ClearProgressBar();

                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();

                Debug.Log($"[MaterialAtlasOptimizer] Atlas generation complete: {atlasPath}");
                EditorUtility.DisplayDialog("Atlas Generated",
                    $"Successfully created atlas with {textures.Count} textures.\n\nAtlas: {atlasName}\nMaterials created: {newMaterials.Count}",
                    "OK");

                // Rescan scene
                ScanScene();
            }
            catch (Exception e)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"[MaterialAtlasOptimizer] Error generating atlas: {e.Message}\n{e.StackTrace}");
                EditorUtility.DisplayDialog("Error", $"Failed to generate atlas:\n{e.Message}", "OK");
            }
        }

        private void GenerateAllAtlases()
        {
            int successCount = 0;
            int failCount = 0;

            for (int i = 0; i < atlasGroups.Count; i++)
            {
                try
                {
                    GenerateAtlas(atlasGroups[i], i);
                    successCount++;
                }
                catch (Exception e)
                {
                    Debug.LogError($"[MaterialAtlasOptimizer] Failed to generate atlas {i}: {e.Message}");
                    failCount++;
                }
            }

            EditorUtility.DisplayDialog("Bulk Atlas Generation Complete",
                $"Successfully generated: {successCount}\nFailed: {failCount}",
                "OK");
        }

        private List<Material> CreateAtlasedMaterials(List<MaterialInfo> materials, Texture2D atlas, Rect[] uvRects, int groupIndex)
        {
            EnsureDirectoryExists(MATERIAL_SAVE_PATH);

            var newMaterials = new List<Material>();
            AssetDatabase.StartAssetEditing();

            try
            {
                // FIXED: Use minimum of both counts to prevent index out of range
                int count = Mathf.Min(materials.Count, uvRects.Length);

                for (int i = 0; i < count; i++)
                {
                    var originalMatInfo = materials[i];
                    var originalMat = originalMatInfo.material;

                    if (originalMat == null) continue;

                    // Create new material
                    Material newMat = new Material(originalMat.shader);
                    newMat.name = $"{originalMat.name}_Atlased";

                    // Copy all properties from original
                    CopyMaterialProperties(originalMat, newMat);

                    // Set atlas texture
                    newMat.mainTexture = atlas;

                    // Set UV offset and scale from PackTextures result
                    Rect uvRect = uvRects[i];
                    newMat.mainTextureScale = new Vector2(uvRect.width, uvRect.height);
                    newMat.mainTextureOffset = new Vector2(uvRect.x, uvRect.y);

                    // Save material asset
                    string matPath = Path.Combine(MATERIAL_SAVE_PATH, $"{newMat.name}_Group{groupIndex}_{i}.mat").Replace("\\", "/");
                    AssetDatabase.CreateAsset(newMat, matPath);

                    newMaterials.Add(newMat);
                }

                AssetDatabase.StopAssetEditing();
                return newMaterials;
            }
            catch (Exception e)
            {
                AssetDatabase.StopAssetEditing();
                throw new Exception($"Failed to create atlased materials: {e.Message}", e);
            }
        }

        private void CopyMaterialProperties(Material source, Material destination)
        {
            // Copy common properties that exist on both materials
            if (source.HasProperty("_Color") && destination.HasProperty("_Color"))
                destination.SetColor("_Color", source.GetColor("_Color"));

            if (source.HasProperty("_Metallic") && destination.HasProperty("_Metallic"))
                destination.SetFloat("_Metallic", source.GetFloat("_Metallic"));

            if (source.HasProperty("_Glossiness") && destination.HasProperty("_Glossiness"))
                destination.SetFloat("_Glossiness", source.GetFloat("_Glossiness"));

            if (source.HasProperty("_Smoothness") && destination.HasProperty("_Smoothness"))
                destination.SetFloat("_Smoothness", source.GetFloat("_Smoothness"));

            if (source.HasProperty("_BumpMap") && destination.HasProperty("_BumpMap"))
                destination.SetTexture("_BumpMap", source.GetTexture("_BumpMap"));

            if (source.HasProperty("_EmissionColor") && destination.HasProperty("_EmissionColor"))
                destination.SetColor("_EmissionColor", source.GetColor("_EmissionColor"));

            // Copy render queue and keywords
            destination.renderQueue = source.renderQueue;
            destination.shaderKeywords = source.shaderKeywords;

            // Copy global illumination flags
            destination.globalIlluminationFlags = source.globalIlluminationFlags;
        }

        private void ReplaceSceneMaterialReferences(List<MaterialInfo> originalInfos, List<Material> newMaterials)
        {
            int count = Mathf.Min(originalInfos.Count, newMaterials.Count);
            if (count == 0)
            {
                Debug.LogWarning("[MaterialAtlasOptimizer] No materials to replace");
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
                        var currentMat = sharedMaterials[i];
                        if (currentMat == null) continue;

                        // Find if this material is in our original list
                        for (int j = 0; j < count; j++)
                        {
                            if (originalInfos[j].material == currentMat)
                            {
                                if (!modified)
                                {
                                    Undo.RecordObject(renderer, "Replace material with atlased version");
                                    modified = true;
                                }

                                sharedMaterials[i] = newMaterials[j];
                                replacementCount++;
                                break;
                            }
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
            }

            Debug.Log($"[MaterialAtlasOptimizer] Replaced {replacementCount} material references with atlased versions");
        }

        #endregion

        #region Material Replacement

        /// <summary>
        /// Replaces all scene references from oldMaterial to newMaterial.
        /// </summary>
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
                importer.isReadable = false;
                importer.sRGBTexture = true;
                importer.mipmapEnabled = true;
                importer.textureType = TextureImporterType.Default;
                importer.maxTextureSize = maxAtlasSize;
                importer.textureCompression = TextureImporterCompression.Compressed;
                importer.SaveAndReimport();
            }
        }

        private void EnsureDirectoryExists(string path)
        {
            if (string.IsNullOrEmpty(path)) return;

            // Normalize path
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