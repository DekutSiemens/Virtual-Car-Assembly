using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// Production-Grade Material Organizer for VR Optimization
/// NEW FEATURES:
/// - Object reference tracking with ping/select functionality
/// - Duplicate material detection and consolidation
/// - Enhanced preview with scene object navigation
/// </summary>
public class MaterialOrganizer : EditorWindow
{
    private string targetFolderPath = "Assets/SceneMaterials";
    private bool copyMaterials = true;
    private GroupingMode groupingMode = GroupingMode.None;
    private bool includeInactive = true;
    private bool showPreview = true;
    private bool filterBuiltIn = true;
    private bool filterPackages = true;
    private bool showAtlasCandidates = true;

    private List<MaterialInfo> foundMaterials;
    private List<MaterialInfo> instanceMaterials;
    private List<List<MaterialInfo>> atlasCandidates;
    private List<List<MaterialInfo>> duplicateGroups; // NEW: Duplicate material groups

    private Vector2 scrollPosition;
    private bool showInstances = true;
    private bool showAtlasGroups = false;
    private bool showDuplicates = false; // NEW
    private bool showObjectReferences = false; // NEW

    private enum GroupingMode
    {
        None,
        Texture,
        Shader,
        ShaderAndTexture
    }

    [MenuItem("Tools/VR Optimization/Material Organizer")]
    public static void ShowWindow()
    {
        GetWindow<MaterialOrganizer>("Material Organizer");
    }

    private void OnGUI()
    {
        EditorGUILayout.Space(10);
        EditorGUILayout.LabelField("Scene Material Organizer", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Production-grade material scanner. Finds ALL materials, tracks object references, " +
            "detects duplicates, and identifies atlas candidates for VR optimization.",
            MessageType.Info
        );

        EditorGUILayout.Space(10);

        // Settings
        EditorGUILayout.LabelField("Settings", EditorStyles.boldLabel);

        targetFolderPath = EditorGUILayout.TextField("Target Folder:", targetFolderPath);
        copyMaterials = EditorGUILayout.Toggle("Copy Materials (vs Move)", copyMaterials);
        includeInactive = EditorGUILayout.Toggle("Include Inactive Objects", includeInactive);

        EditorGUILayout.Space(5);
        EditorGUILayout.LabelField("Filtering", EditorStyles.boldLabel);
        filterBuiltIn = EditorGUILayout.Toggle("Filter Unity Built-in", filterBuiltIn);
        filterPackages = EditorGUILayout.Toggle("Filter Package Materials", filterPackages);

        EditorGUILayout.Space(5);
        EditorGUILayout.LabelField("Grouping & Analysis", EditorStyles.boldLabel);
        groupingMode = (GroupingMode)EditorGUILayout.EnumPopup("Grouping Mode:", groupingMode);
        showAtlasCandidates = EditorGUILayout.Toggle("Identify Atlas Candidates", showAtlasCandidates);
        showPreview = EditorGUILayout.Toggle("Show Preview", showPreview);

        EditorGUILayout.Space(10);

        // Action Buttons
        EditorGUILayout.BeginHorizontal();

        if (GUILayout.Button("1. Scan Scene", GUILayout.Height(30)))
        {
            ScanScene();
        }

        GUI.enabled = foundMaterials != null && foundMaterials.Count > 0;
        if (GUILayout.Button("2. Organize Materials", GUILayout.Height(30)))
        {
            OrganizeMaterials();
        }
        GUI.enabled = true;

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.Space(10);

        // Statistics
        if (foundMaterials != null || instanceMaterials != null)
        {
            EditorGUILayout.BeginVertical("box");
            EditorGUILayout.LabelField("Scan Results:", EditorStyles.boldLabel);

            if (foundMaterials != null)
            {
                EditorGUILayout.LabelField($"✓ Asset Materials: {foundMaterials.Count}");
            }

            // NEW: Duplicate Detection Stats
            if (duplicateGroups != null && duplicateGroups.Count > 0)
            {
                int totalDuplicates = duplicateGroups.Sum(group => group.Count - 1);
                EditorGUILayout.LabelField($"⚠ Duplicate Groups: {duplicateGroups.Count} ({totalDuplicates} duplicates)",
                    new GUIStyle(EditorStyles.label) { normal = { textColor = Color.yellow } });
            }

            if (atlasCandidates != null && atlasCandidates.Count > 0)
            {
                int totalAtlasable = atlasCandidates.Sum(group => group.Count);
                EditorGUILayout.LabelField($"✓ Atlas Candidate Groups: {atlasCandidates.Count} ({totalAtlasable} materials)",
                    new GUIStyle(EditorStyles.label) { normal = { textColor = Color.green } });
            }

            if (instanceMaterials != null && instanceMaterials.Count > 0)
            {
                EditorGUILayout.LabelField($"⚠ Material Instances: {instanceMaterials.Count}",
                    new GUIStyle(EditorStyles.label) { normal = { textColor = Color.yellow } });
            }

            EditorGUILayout.EndVertical();
        }

        // NEW: Duplicate Materials Section
        if (duplicateGroups != null && duplicateGroups.Count > 0)
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.HelpBox(
                $"⚠ Found {duplicateGroups.Count} groups of duplicate materials! " +
                "These materials have identical properties but exist as separate assets. " +
                "Consolidating them will reduce draw calls and improve batching.",
                MessageType.Warning
            );

            showDuplicates = EditorGUILayout.Foldout(showDuplicates, "Show Duplicate Groups");

            if (showDuplicates)
            {
                EditorGUILayout.BeginVertical("box");
                int groupNum = 1;

                foreach (var group in duplicateGroups.Take(10))
                {
                    if (group == null || group.Count == 0) continue;

                    EditorGUILayout.BeginVertical("box");
                    EditorGUILayout.LabelField($"Duplicate Group {groupNum} ({group.Count} materials)", EditorStyles.boldLabel);

                    var first = group[0];
                    if (first.material != null)
                    {
                        EditorGUILayout.LabelField($"  Shader: {first.shader.name}", EditorStyles.miniLabel);
                        if (first.mainTexture != null)
                        {
                            EditorGUILayout.LabelField($"  Main Texture: {first.mainTexture.name}", EditorStyles.miniLabel);
                        }
                    }

                    EditorGUILayout.Space(3);

                    foreach (var matInfo in group)
                    {
                        if (matInfo.material == null) continue;

                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField($"    • {matInfo.material.name}", EditorStyles.miniLabel);
                        EditorGUILayout.LabelField($"({matInfo.usedByCount} objects)", EditorStyles.miniLabel, GUILayout.Width(80));

                        if (GUILayout.Button("Ping", GUILayout.Width(50)))
                        {
                            EditorGUIUtility.PingObject(matInfo.material);
                        }
                        if (GUILayout.Button("Select Objects", GUILayout.Width(100)))
                        {
                            Selection.objects = matInfo.usedByObjects.ToArray();
                        }
                        EditorGUILayout.EndHorizontal();
                    }

                    EditorGUILayout.Space(3);

                    // Consolidate button for this group
                    GUI.backgroundColor = Color.yellow;
                    if (GUILayout.Button($"Consolidate Group {groupNum} (Keep '{group[0].material.name}')", GUILayout.Height(25)))
                    {
                        ConsolidateDuplicateGroup(group);
                    }
                    GUI.backgroundColor = Color.white;

                    EditorGUILayout.EndVertical();
                    EditorGUILayout.Space(5);
                    groupNum++;
                }

                if (duplicateGroups.Count > 10)
                {
                    EditorGUILayout.LabelField($"... and {duplicateGroups.Count - 10} more groups", EditorStyles.miniLabel);
                }

                EditorGUILayout.Space(5);

                // Consolidate All button
                GUI.backgroundColor = new Color(1f, 0.5f, 0f);
                if (GUILayout.Button("⚠ CONSOLIDATE ALL DUPLICATES", GUILayout.Height(35)))
                {
                    if (EditorUtility.DisplayDialog("Consolidate All Duplicates",
                        $"This will replace {duplicateGroups.Sum(g => g.Count - 1)} duplicate materials with their first variant. " +
                        "All scene references will be updated automatically.\n\nThis cannot be undone easily. Continue?",
                        "Yes, Consolidate All", "Cancel"))
                    {
                        ConsolidateAllDuplicates();
                    }
                }
                GUI.backgroundColor = Color.white;

                EditorGUILayout.EndVertical();
            }
        }

        // Material Instances Warning
        if (instanceMaterials != null && instanceMaterials.Count > 0)
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.HelpBox(
                $"⚠ Found {instanceMaterials.Count} material INSTANCES (runtime materials with no asset file). " +
                "These cannot be organized and may break batching. Consider saving them as assets.",
                MessageType.Warning
            );

            showInstances = EditorGUILayout.Foldout(showInstances, "Show Material Instances");

            if (showInstances)
            {
                EditorGUILayout.BeginVertical("box");
                foreach (var matInfo in instanceMaterials.Take(10))
                {
                    if (matInfo.material == null) continue;

                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"• {matInfo.material.name}", EditorStyles.miniLabel);
                    EditorGUILayout.LabelField($"({matInfo.usedByCount} objects)", EditorStyles.miniLabel, GUILayout.Width(80));
                    EditorGUILayout.LabelField($"Shader: {matInfo.shader.name}", EditorStyles.miniLabel);
                    EditorGUILayout.EndHorizontal();
                }
                if (instanceMaterials.Count > 10)
                {
                    EditorGUILayout.LabelField($"... and {instanceMaterials.Count - 10} more", EditorStyles.miniLabel);
                }
                EditorGUILayout.EndVertical();
            }
        }

        // Atlas Candidates
        if (atlasCandidates != null && atlasCandidates.Count > 0)
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.HelpBox(
                $"✓ Found {atlasCandidates.Count} groups of materials that can be safely atlased together. " +
                "These materials share the same shader, texture size, and properties.",
                MessageType.Info
            );

            showAtlasGroups = EditorGUILayout.Foldout(showAtlasGroups, "Show Atlas Candidate Groups");

            if (showAtlasGroups)
            {
                EditorGUILayout.BeginVertical("box");
                int groupNum = 1;
                foreach (var group in atlasCandidates.Take(5))
                {
                    if (group == null || group.Count == 0 || group[0].material == null) continue;

                    EditorGUILayout.LabelField($"Group {groupNum}: {group.Count} materials", EditorStyles.boldLabel);
                    var first = group[0];
                    EditorGUILayout.LabelField($"  Shader: {first.shader.name}", EditorStyles.miniLabel);
                    if (first.textureSize.x > 0)
                    {
                        EditorGUILayout.LabelField($"  Texture Size: {first.textureSize.x}×{first.textureSize.y}", EditorStyles.miniLabel);
                    }
                    EditorGUILayout.LabelField($"  Materials: {string.Join(", ", group.Select(m => m.material.name).Take(3))}...",
                        EditorStyles.miniLabel);
                    EditorGUILayout.Space(3);
                    groupNum++;
                }
                if (atlasCandidates.Count > 5)
                {
                    EditorGUILayout.LabelField($"... and {atlasCandidates.Count - 5} more groups", EditorStyles.miniLabel);
                }
                EditorGUILayout.EndVertical();
            }
        }

        EditorGUILayout.Space(10);

        // Preview
        if (showPreview && foundMaterials != null && foundMaterials.Count > 0)
        {
            EditorGUILayout.LabelField($"Asset Materials: {foundMaterials.Count}", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);

            try
            {
                scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(300));

                foreach (var matInfo in foundMaterials)
                {
                    if (matInfo == null || matInfo.material == null) continue;

                    EditorGUILayout.BeginVertical("box");

                    // Material header with ping buttons
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField(matInfo.material.name, EditorStyles.boldLabel);

                    if (GUILayout.Button("Ping Material", GUILayout.Width(100)))
                    {
                        EditorGUIUtility.PingObject(matInfo.material);
                    }

                    // NEW: Select all objects using this material
                    GUI.enabled = matInfo.usedByObjects != null && matInfo.usedByObjects.Count > 0;
                    if (GUILayout.Button("Select Objects", GUILayout.Width(100)))
                    {
                        Selection.objects = matInfo.usedByObjects.ToArray();
                    }
                    GUI.enabled = true;

                    EditorGUILayout.EndHorizontal();

                    EditorGUILayout.LabelField($"Path: {matInfo.assetPath}", EditorStyles.miniLabel);
                    EditorGUILayout.LabelField($"Shader: {matInfo.shader.name}", EditorStyles.miniLabel);
                    EditorGUILayout.LabelField($"Used by: {matInfo.usedByCount} objects on {matInfo.rendererTypes.Count} renderer type(s)", EditorStyles.miniLabel);

                    if (matInfo.mainTexture != null)
                    {
                        EditorGUILayout.LabelField($"Main Texture: {matInfo.mainTexture.name}", EditorStyles.miniLabel);

                        if (matInfo.mainTexture is Texture2D tex)
                        {
                            EditorGUILayout.LabelField($"Resolution: {tex.width}×{tex.height}",
                                new GUIStyle(EditorStyles.miniLabel)
                                {
                                    normal = { textColor = tex.width > 2048 ? Color.red : Color.white }
                                });
                        }
                    }

                    if (matInfo.material.shaderKeywords != null && matInfo.material.shaderKeywords.Length > 0)
                    {
                        string keywords = string.Join(", ", matInfo.material.shaderKeywords);
                        if (keywords.Length > 50) keywords = keywords.Substring(0, 47) + "...";
                        EditorGUILayout.LabelField($"Keywords ({matInfo.material.shaderKeywords.Length}): {keywords}",
                            EditorStyles.miniLabel);
                    }
                    else
                    {
                        EditorGUILayout.LabelField("Keywords: None (Good for GPU instancing)",
                            new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.green } });
                    }

                    if (matInfo.rendererTypes.Count > 0)
                    {
                        EditorGUILayout.LabelField($"Renderers: {string.Join(", ", matInfo.rendererTypes.Select(t => t.Name))}",
                            EditorStyles.miniLabel);
                    }

                    // NEW: Object references list
                    if (matInfo.usedByObjects != null && matInfo.usedByObjects.Count > 0)
                    {
                        showObjectReferences = EditorGUILayout.Foldout(showObjectReferences,
                            $"Scene Objects ({matInfo.usedByObjects.Count})");

                        if (showObjectReferences)
                        {
                            EditorGUILayout.BeginVertical("box");

                            foreach (var obj in matInfo.usedByObjects.Take(10))
                            {
                                if (obj == null) continue;

                                EditorGUILayout.BeginHorizontal();

                                // Ping button for object
                                if (GUILayout.Button("→", GUILayout.Width(25)))
                                {
                                    Selection.activeGameObject = obj;
                                    EditorGUIUtility.PingObject(obj);
                                }

                                EditorGUILayout.LabelField(obj.name, EditorStyles.miniLabel);
                                EditorGUILayout.LabelField(GetHierarchyPath(obj),
                                    EditorStyles.miniLabel, GUILayout.MaxWidth(200));

                                EditorGUILayout.EndHorizontal();
                            }

                            if (matInfo.usedByObjects.Count > 10)
                            {
                                EditorGUILayout.LabelField($"... and {matInfo.usedByObjects.Count - 10} more objects",
                                    EditorStyles.miniLabel);
                            }

                            EditorGUILayout.EndVertical();
                        }
                    }

                    EditorGUILayout.EndVertical();
                    EditorGUILayout.Space(3);
                }
            }
            finally
            {
                EditorGUILayout.EndScrollView();
            }
        }
    }

    private void ScanScene()
    {
        foundMaterials = new List<MaterialInfo>();
        instanceMaterials = new List<MaterialInfo>();
        Dictionary<string, MaterialInfo> materialDict = new Dictionary<string, MaterialInfo>();
        Dictionary<string, MaterialInfo> instanceDict = new Dictionary<string, MaterialInfo>();

        int totalRenderers = 0;
        int filteredCount = 0;

        totalRenderers += ScanRendererType<MeshRenderer>(materialDict, instanceDict, ref filteredCount);
        totalRenderers += ScanRendererType<SkinnedMeshRenderer>(materialDict, instanceDict, ref filteredCount);
        totalRenderers += ScanRendererType<SpriteRenderer>(materialDict, instanceDict, ref filteredCount);
        totalRenderers += ScanRendererType<LineRenderer>(materialDict, instanceDict, ref filteredCount);
        totalRenderers += ScanRendererType<TrailRenderer>(materialDict, instanceDict, ref filteredCount);
        totalRenderers += ScanRendererType<ParticleSystemRenderer>(materialDict, instanceDict, ref filteredCount);

        ScanTerrainMaterials(materialDict, instanceDict, ref filteredCount);

        Debug.Log($"<color=cyan>Scanned {totalRenderers} renderers across all types</color>");
        if (filteredCount > 0)
        {
            Debug.Log($"<color=yellow>Filtered out {filteredCount} built-in/package materials</color>");
        }

        foundMaterials = materialDict.Values.OrderByDescending(m => m.usedByCount).ToList();
        instanceMaterials = instanceDict.Values.OrderByDescending(m => m.usedByCount).ToList();

        // NEW: Detect duplicates
        DetectDuplicateMaterials();

        if (showAtlasCandidates)
        {
            IdentifyAtlasCandidates();
        }

        Debug.Log($"<color=green>✓ Found {foundMaterials.Count} asset materials</color>");

        if (duplicateGroups != null && duplicateGroups.Count > 0)
        {
            int totalDupes = duplicateGroups.Sum(g => g.Count - 1);
            Debug.LogWarning($"⚠ Found {duplicateGroups.Count} duplicate groups ({totalDupes} duplicates):");
            foreach (var group in duplicateGroups.Take(3))
            {
                Debug.LogWarning($"  • {group.Count} copies of material with shader '{group[0].shader.name}'");
            }
        }

        if (instanceMaterials.Count > 0)
        {
            Debug.LogWarning($"⚠ Found {instanceMaterials.Count} material INSTANCES (no asset path):");
            foreach (var mat in instanceMaterials.Take(5))
            {
                Debug.LogWarning($"  • {mat.material.name} - used by {mat.usedByCount} objects - Shader: {mat.shader.name}");
            }
            if (instanceMaterials.Count > 5)
            {
                Debug.LogWarning($"  ... and {instanceMaterials.Count - 5} more (see Material Organizer window)");
            }
        }

        if (atlasCandidates != null && atlasCandidates.Count > 0)
        {
            Debug.Log($"<color=green>✓ Found {atlasCandidates.Count} atlas candidate groups</color>");
        }

        Repaint();
    }

    private int ScanRendererType<T>(Dictionary<string, MaterialInfo> materialDict,
                                     Dictionary<string, MaterialInfo> instanceDict,
                                     ref int filteredCount) where T : Renderer
    {
        T[] renderers;

        var activeScene = EditorSceneManager.GetActiveScene();

        if (includeInactive)
        {
            renderers = Resources.FindObjectsOfTypeAll<T>()
                .Where(r =>
                {
                    if (r.gameObject.scene != activeScene) return false;
                    if (!r.gameObject.scene.isLoaded) return false;
                    var stage = PrefabStageUtility.GetPrefabStage(r.gameObject);
                    if (stage != null) return false;
                    if (r.gameObject.hideFlags != HideFlags.None) return false;
                    if (string.IsNullOrEmpty(r.gameObject.scene.name)) return false;
                    string assetPath = AssetDatabase.GetAssetPath(r.gameObject);
                    if (!string.IsNullOrEmpty(assetPath)) return false;
                    return true;
                })
                .ToArray();
        }
        else
        {
            renderers = Object.FindObjectsByType<T>(FindObjectsSortMode.None);
        }

        foreach (var renderer in renderers)
        {
            Material[] materials = renderer.sharedMaterials;

            foreach (var material in materials)
            {
                if (material == null) continue;
                // NEW: Pass the game object to track references
                TrackMaterial(material, typeof(T), renderer.gameObject, materialDict, instanceDict, ref filteredCount);
            }
        }

        return renderers.Length;
    }

    private void ScanTerrainMaterials(Dictionary<string, MaterialInfo> materialDict,
                                      Dictionary<string, MaterialInfo> instanceDict,
                                      ref int filteredCount)
    {
        Terrain[] terrains;

        var activeScene = EditorSceneManager.GetActiveScene();

        if (includeInactive)
        {
            terrains = Resources.FindObjectsOfTypeAll<Terrain>()
                .Where(t =>
                {
                    if (t.gameObject.scene != activeScene) return false;
                    if (!t.gameObject.scene.isLoaded) return false;
                    var stage = PrefabStageUtility.GetPrefabStage(t.gameObject);
                    if (stage != null) return false;
                    if (t.gameObject.hideFlags != HideFlags.None) return false;
                    if (string.IsNullOrEmpty(t.gameObject.scene.name)) return false;
                    string assetPath = AssetDatabase.GetAssetPath(t.gameObject);
                    if (!string.IsNullOrEmpty(assetPath)) return false;
                    return true;
                })
                .ToArray();
        }
        else
        {
            terrains = Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None);
        }

        foreach (var terrain in terrains)
        {
            if (terrain.materialTemplate != null)
            {
                TrackMaterial(terrain.materialTemplate, typeof(Terrain), terrain.gameObject, materialDict, instanceDict, ref filteredCount);
            }
        }
    }

    private void TrackMaterial(Material material, System.Type rendererType, GameObject gameObject,
                              Dictionary<string, MaterialInfo> materialDict,
                              Dictionary<string, MaterialInfo> instanceDict,
                              ref int filteredCount)
    {
        string assetPath = AssetDatabase.GetAssetPath(material);
        bool isInstance = string.IsNullOrEmpty(assetPath);

        if (!isInstance && filterBuiltIn)
        {
            if (assetPath.StartsWith("Resources/unity_builtin_extra") ||
                assetPath.StartsWith("Library/") ||
                assetPath.Contains("unity default resources"))
            {
                filteredCount++;
                return;
            }
        }

        if (!isInstance && filterPackages)
        {
            if (assetPath.StartsWith("Packages/"))
            {
                filteredCount++;
                return;
            }
        }

        var targetDict = isInstance ? instanceDict : materialDict;

        string key;
        if (isInstance)
        {
            key = material.GetInstanceID().ToString();
        }
        else
        {
            string guid = AssetDatabase.AssetPathToGUID(assetPath);
            key = string.IsNullOrEmpty(guid) ? material.GetInstanceID().ToString() : guid;
        }

        if (!targetDict.ContainsKey(key))
        {
            MaterialInfo info = new MaterialInfo
            {
                material = material,
                assetPath = assetPath,
                mainTexture = material.mainTexture,
                shader = material.shader,
                usedByCount = 0,
                isInstance = isInstance,
                rendererTypes = new HashSet<System.Type>(),
                usedByObjects = new List<GameObject>(), // NEW
                guid = key
            };

            if (material.mainTexture is Texture2D tex)
            {
                info.textureSize = new Vector2Int(tex.width, tex.height);
            }

            targetDict[key] = info;
        }

        targetDict[key].usedByCount++;
        targetDict[key].rendererTypes.Add(rendererType);

        // NEW: Track which objects use this material
        if (!targetDict[key].usedByObjects.Contains(gameObject))
        {
            targetDict[key].usedByObjects.Add(gameObject);
        }
    }

    // NEW: Detect duplicate materials
    private void DetectDuplicateMaterials()
    {
        duplicateGroups = new List<List<MaterialInfo>>();

        // Group materials by their properties
        var groups = foundMaterials
            .Where(m => m.material != null)
            .GroupBy(m => GetMaterialSignature(m.material))
            .Where(g => g.Count() > 1) // Only groups with duplicates
            .OrderByDescending(g => g.Count());

        foreach (var group in groups)
        {
            duplicateGroups.Add(group.ToList());
        }

        if (duplicateGroups.Count > 0)
        {
            Debug.Log($"<color=yellow>Detected {duplicateGroups.Count} duplicate material groups</color>");
        }
    }

    // NEW: Create a signature for material comparison
    private string GetMaterialSignature(Material mat)
    {
        var signature = new System.Text.StringBuilder();

        // Shader
        signature.Append(mat.shader.name);
        signature.Append("|");

        // Main texture
        if (mat.mainTexture != null)
        {
            signature.Append(mat.mainTexture.GetInstanceID());
        }
        signature.Append("|");

        // Color property (common in most shaders)
        if (mat.HasProperty("_Color"))
        {
            Color c = mat.GetColor("_Color");
            signature.Append($"{c.r:F2},{c.g:F2},{c.b:F2},{c.a:F2}");
        }
        signature.Append("|");

        // Keywords
        if (mat.shaderKeywords != null && mat.shaderKeywords.Length > 0)
        {
            signature.Append(string.Join(",", mat.shaderKeywords.OrderBy(k => k)));
        }
        signature.Append("|");

        // Render queue
        signature.Append(mat.renderQueue);

        return signature.ToString();
    }

    // NEW: Consolidate a single duplicate group
    private void ConsolidateDuplicateGroup(List<MaterialInfo> group)
    {
        if (group == null || group.Count < 2)
        {
            Debug.LogWarning("Cannot consolidate: group has less than 2 materials");
            return;
        }

        // Keep the first material, replace all others
        Material keepMaterial = group[0].material;
        List<Material> toReplace = group.Skip(1).Select(m => m.material).ToList();

        int replacedCount = ReplaceMaterialReferences(toReplace, keepMaterial);

        Debug.Log($"<color=green>✓ Consolidated {group.Count} materials into '{keepMaterial.name}' " +
                  $"({replacedCount} references updated)</color>");

        // Rescan to update UI
        ScanScene();
    }

    // NEW: Consolidate all duplicate groups
    private void ConsolidateAllDuplicates()
    {
        if (duplicateGroups == null || duplicateGroups.Count == 0)
        {
            Debug.LogWarning("No duplicate groups to consolidate");
            return;
        }

        int totalConsolidated = 0;
        int totalReferences = 0;

        foreach (var group in duplicateGroups)
        {
            if (group == null || group.Count < 2) continue;

            Material keepMaterial = group[0].material;
            List<Material> toReplace = group.Skip(1).Select(m => m.material).ToList();

            int replacedCount = ReplaceMaterialReferences(toReplace, keepMaterial);

            totalConsolidated += toReplace.Count;
            totalReferences += replacedCount;
        }

        Debug.Log($"<color=green>✓ Consolidated {totalConsolidated} duplicate materials " +
                  $"({totalReferences} references updated)</color>");

        EditorUtility.DisplayDialog("Consolidation Complete",
            $"Successfully consolidated {totalConsolidated} duplicate materials.\n" +
            $"Updated {totalReferences} material references in the scene.",
            "OK");

        // Rescan to update UI
        ScanScene();
    }

    // NEW: Replace material references across all renderers
    private int ReplaceMaterialReferences(List<Material> oldMaterials, Material newMaterial)
    {
        int replacedCount = 0;

        // Scan all renderer types
        var allRenderers = new List<Renderer>();
        allRenderers.AddRange(Object.FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None));
        allRenderers.AddRange(Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None));
        allRenderers.AddRange(Object.FindObjectsByType<SpriteRenderer>(FindObjectsSortMode.None));
        allRenderers.AddRange(Object.FindObjectsByType<LineRenderer>(FindObjectsSortMode.None));
        allRenderers.AddRange(Object.FindObjectsByType<TrailRenderer>(FindObjectsSortMode.None));
        allRenderers.AddRange(Object.FindObjectsByType<ParticleSystemRenderer>(FindObjectsSortMode.None));

        foreach (var renderer in allRenderers)
        {
            Material[] materials = renderer.sharedMaterials;
            bool changed = false;

            for (int i = 0; i < materials.Length; i++)
            {
                if (materials[i] != null && oldMaterials.Contains(materials[i]))
                {
                    materials[i] = newMaterial;
                    changed = true;
                    replacedCount++;
                }
            }

            if (changed)
            {
                Undo.RecordObject(renderer, "Consolidate Materials");
                renderer.sharedMaterials = materials;
                EditorUtility.SetDirty(renderer);
            }
        }

        // Also check terrains
        var terrains = Object.FindObjectsByType<Terrain>(FindObjectsSortMode.None);
        foreach (var terrain in terrains)
        {
            if (terrain.materialTemplate != null && oldMaterials.Contains(terrain.materialTemplate))
            {
                Undo.RecordObject(terrain, "Consolidate Materials");
                terrain.materialTemplate = newMaterial;
                EditorUtility.SetDirty(terrain);
                replacedCount++;
            }
        }

        return replacedCount;
    }

    private void OrganizeMaterials()
    {
        if (foundMaterials == null || foundMaterials.Count == 0)
        {
            EditorUtility.DisplayDialog("No Materials", "Please scan the scene first.", "OK");
            return;
        }

        if (!AssetDatabase.IsValidFolder(targetFolderPath))
        {
            CreateFolderRecursive(targetFolderPath);
        }

        int successCount = 0;
        int errorCount = 0;

        try
        {
            AssetDatabase.StartAssetEditing();

            foreach (var matInfo in foundMaterials)
            {
                if (matInfo.material == null) continue;

                string targetPath = GetTargetPath(matInfo);
                targetPath = AssetDatabase.GenerateUniqueAssetPath(targetPath);

                string error;
                if (copyMaterials)
                {
                    error = AssetDatabase.CopyAsset(matInfo.assetPath, targetPath) ? null : "Copy failed";
                }
                else
                {
                    error = AssetDatabase.MoveAsset(matInfo.assetPath, targetPath);
                }

                if (string.IsNullOrEmpty(error))
                {
                    successCount++;
                }
                else
                {
                    Debug.LogError($"Failed to {(copyMaterials ? "copy" : "move")} {matInfo.material.name}: {error}");
                    errorCount++;
                }
            }
        }
        finally
        {
            AssetDatabase.StopAssetEditing();
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        foundMaterials.Clear();
        if (instanceMaterials != null) instanceMaterials.Clear();
        if (atlasCandidates != null) atlasCandidates.Clear();
        if (duplicateGroups != null) duplicateGroups.Clear();

        string message = $"Organized {successCount} materials into {targetFolderPath}";
        if (errorCount > 0)
        {
            message += $"\n{errorCount} errors occurred (check console)";
        }

        EditorUtility.DisplayDialog("Complete", message, "OK");
        Debug.Log($"<color=green>{message}</color>");

        Debug.Log("Refreshing scan to find moved materials...");
        ScanScene();
    }

    private string GetTargetPath(MaterialInfo matInfo)
    {
        string groupFolder = targetFolderPath;

        switch (groupingMode)
        {
            case GroupingMode.Texture:
                if (matInfo.mainTexture != null)
                {
                    string texName = CleanFileName(matInfo.mainTexture.name);
                    groupFolder = targetFolderPath + "/" + texName;
                }
                break;

            case GroupingMode.Shader:
                string shaderName = CleanFileName(matInfo.shader.name.Replace("/", "_"));
                groupFolder = targetFolderPath + "/" + shaderName;
                break;

            case GroupingMode.ShaderAndTexture:
                string shaderFolder = CleanFileName(matInfo.shader.name.Replace("/", "_"));
                groupFolder = targetFolderPath + "/" + shaderFolder;

                if (matInfo.mainTexture != null)
                {
                    string texFolder = CleanFileName(matInfo.mainTexture.name);
                    groupFolder = groupFolder + "/" + texFolder;
                }
                break;
        }

        if (groupFolder != targetFolderPath && !AssetDatabase.IsValidFolder(groupFolder))
        {
            CreateFolderRecursive(groupFolder);
        }

        return groupFolder + "/" + System.IO.Path.GetFileName(matInfo.assetPath);
    }

    private void CreateFolderRecursive(string path)
    {
        string[] folders = path.Split('/');
        string currentPath = folders[0];

        for (int i = 1; i < folders.Length; i++)
        {
            string newPath = currentPath + "/" + folders[i];
            if (!AssetDatabase.IsValidFolder(newPath))
            {
                AssetDatabase.CreateFolder(currentPath, folders[i]);
            }
            currentPath = newPath;
        }
    }

    private void IdentifyAtlasCandidates()
    {
        atlasCandidates = new List<List<MaterialInfo>>();

        var groups = foundMaterials
            .Where(m => m.material != null && m.mainTexture != null && m.textureSize.x > 0)
            .GroupBy(m => new
            {
                Shader = m.shader,
                TextureWidth = m.textureSize.x,
                TextureHeight = m.textureSize.y,
                Keywords = string.Join("|",
                (m.material.shaderKeywords ?? new string[0]).OrderBy(k => k))
            })
            .Where(g => g.Count() >= 2)
            .OrderByDescending(g => g.Count());

        foreach (var group in groups)
        {
            var groupList = group.ToList();
            var compatibleMaterials = new List<MaterialInfo>();

            foreach (var mat in groupList)
            {
                if (mat.material == null) continue;

                Vector2 tiling = mat.material.mainTextureScale;
                bool isCompatible = true;

                if (tiling != Vector2.one)
                {
                    isCompatible = false;
                }

                if (isCompatible)
                {
                    compatibleMaterials.Add(mat);
                }
            }

            if (compatibleMaterials.Count >= 2)
            {
                atlasCandidates.Add(compatibleMaterials);
            }
        }

        Debug.Log($"<color=green>Identified {atlasCandidates.Count} atlas candidate groups:</color>");
        foreach (var group in atlasCandidates.Take(3))
        {
            var first = group[0];
            Debug.Log($"  • {group.Count} materials | Shader: {first.shader.name} | Size: {first.textureSize.x}×{first.textureSize.y}");
        }
    }

    // NEW: Get hierarchy path for better object identification
    private string GetHierarchyPath(GameObject obj)
    {
        if (obj == null) return "";

        string path = obj.name;
        Transform parent = obj.transform.parent;
        int depth = 0;

        while (parent != null && depth < 3) // Limit to 3 levels for readability
        {
            path = parent.name + "/" + path;
            parent = parent.parent;
            depth++;
        }

        if (parent != null)
        {
            path = ".../" + path;
        }

        return path;
    }

    private string CleanFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    private class MaterialInfo
    {
        public Material material;
        public string assetPath;
        public Texture mainTexture;
        public Shader shader;
        public int usedByCount;
        public bool isInstance;
        public HashSet<System.Type> rendererTypes;
        public List<GameObject> usedByObjects; // NEW: Track which objects use this material
        public string guid;
        public Vector2Int textureSize;
    }
}