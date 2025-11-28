using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// Production-Grade Material Organizer V4 - Complete VR Optimization Suite
/// NEW IN V4:
/// - Atlas Preview Tab with visual texture grid and memory estimates
/// - VR Performance Scoring system (Quest 3 optimized)
/// - One-Click Full Optimization workflow
/// - Priority-based recommendations
/// - Estimated FPS impact per fix
/// </summary>
public class MaterialOrganizerV4 : EditorWindow
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
    private List<List<MaterialInfo>> duplicateGroups;
    private List<List<MaterialInfo>> tilingVariantGroups;

    // NEW: VR Performance data
    private Dictionary<Material, VRPerformanceScore> performanceScores = new Dictionary<Material, VRPerformanceScore>();
    private float overallSceneScore = 0f;
    private int estimatedDrawCallSavings = 0;

    private Vector2 masterScrollPosition;
    private Vector2 previewScrollPosition;
    private Vector2 instancingScrollPosition;
    private Vector2 atlasPreviewScrollPosition; // NEW
    private Vector2 performanceScrollPosition; // NEW

    private bool showInstances = true;
    private bool showAtlasGroups = false;
    private bool showDuplicates = false;
    private bool showTilingVariants = false;
    private bool showGPUInstancingAnalysis = false;
    private bool showAtlasPreview = false; // NEW
    private bool showVRPerformance = false; // NEW

    private Dictionary<string, bool> materialObjectFoldouts = new Dictionary<string, bool>();

    // NEW: View tabs
    private enum ViewTab { Overview, AtlasPreview, VRPerformance }
    private ViewTab currentTab = ViewTab.Overview;

    private enum GroupingMode
    {
        None,
        Texture,
        Shader,
        ShaderAndTexture
    }

    [MenuItem("Tools/VR Optimization/Material Organizer V4")]
    public static void ShowWindow()
    {
        GetWindow<MaterialOrganizerV4>("Material Organizer V4");
    }

    private void OnGUI()
    {
        masterScrollPosition = EditorGUILayout.BeginScrollView(masterScrollPosition);

        try
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("Material Organizer V4 - VR Optimization Suite", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Complete VR optimization: Duplicates, Tiling Variants, Atlas Preview, Performance Scoring.",
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
            EditorGUILayout.LabelField("Analysis", EditorStyles.boldLabel);
            groupingMode = (GroupingMode)EditorGUILayout.EnumPopup("Grouping Mode:", groupingMode);
            showAtlasCandidates = EditorGUILayout.Toggle("Identify Atlas Candidates", showAtlasCandidates);
            showPreview = EditorGUILayout.Toggle("Show Material Details", showPreview);

            EditorGUILayout.Space(10);

            // Action Buttons
            EditorGUILayout.BeginHorizontal();

            if (GUILayout.Button("1. Scan Scene", GUILayout.Height(30)))
            {
                ScanScene();
            }

            GUI.enabled = foundMaterials != null && foundMaterials.Count > 0;

            // NEW: One-Click Optimization
            GUI.backgroundColor = Color.green;
            if (GUILayout.Button("⚡ AUTO-OPTIMIZE", GUILayout.Height(30)))
            {
                PerformAutoOptimization();
            }
            GUI.backgroundColor = Color.white;

            if (GUILayout.Button("2. Organize", GUILayout.Height(30)))
            {
                OrganizeMaterials();
            }

            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(10);

            // NEW: Tab Selection
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(currentTab == ViewTab.Overview, "Overview", "Button", GUILayout.Height(25)))
                currentTab = ViewTab.Overview;
            if (GUILayout.Toggle(currentTab == ViewTab.AtlasPreview, "Atlas Preview", "Button", GUILayout.Height(25)))
                currentTab = ViewTab.AtlasPreview;
            if (GUILayout.Toggle(currentTab == ViewTab.VRPerformance, "VR Performance", "Button", GUILayout.Height(25)))
                currentTab = ViewTab.VRPerformance;
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

                // NEW: VR Performance Score
                if (overallSceneScore > 0)
                {
                    Color scoreColor = overallSceneScore >= 80 ? Color.green :
                                      overallSceneScore >= 60 ? Color.yellow : Color.red;
                    EditorGUILayout.LabelField($"🎯 VR Performance Score: {overallSceneScore:F1}/100",
                        new GUIStyle(EditorStyles.label) { normal = { textColor = scoreColor } });

                    if (estimatedDrawCallSavings > 0)
                    {
                        EditorGUILayout.LabelField($"💡 Potential Draw Call Savings: {estimatedDrawCallSavings}",
                            new GUIStyle(EditorStyles.label) { normal = { textColor = Color.cyan } });
                    }
                }

                if (tilingVariantGroups != null && tilingVariantGroups.Count > 0)
                {
                    int totalVariants = tilingVariantGroups.Sum(group => group.Count - 1);
                    EditorGUILayout.LabelField($"🔧 Tiling Variants: {tilingVariantGroups.Count} groups ({totalVariants} variants)",
                        new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(1f, 0.5f, 0f) } });
                }

                if (duplicateGroups != null && duplicateGroups.Count > 0)
                {
                    int totalDuplicates = duplicateGroups.Sum(group => group.Count - 1);
                    EditorGUILayout.LabelField($"⚠ Duplicates: {duplicateGroups.Count} groups ({totalDuplicates} duplicates)",
                        new GUIStyle(EditorStyles.label) { normal = { textColor = Color.yellow } });
                }

                if (atlasCandidates != null && atlasCandidates.Count > 0)
                {
                    int totalAtlasable = atlasCandidates.Sum(group => group.Count);
                    EditorGUILayout.LabelField($"✓ Atlas Candidates: {atlasCandidates.Count} groups ({totalAtlasable} materials)",
                        new GUIStyle(EditorStyles.label) { normal = { textColor = Color.green } });
                }

                if (instanceMaterials != null && instanceMaterials.Count > 0)
                {
                    EditorGUILayout.LabelField($"⚠ Instances: {instanceMaterials.Count}",
                        new GUIStyle(EditorStyles.label) { normal = { textColor = Color.yellow } });
                }

                EditorGUILayout.EndVertical();
            }

            EditorGUILayout.Space(10);

            // Content based on selected tab
            switch (currentTab)
            {
                case ViewTab.Overview:
                    DrawOverviewTab();
                    break;
                case ViewTab.AtlasPreview:
                    DrawAtlasPreviewTab();
                    break;
                case ViewTab.VRPerformance:
                    DrawVRPerformanceTab();
                    break;
            }
        }
        finally
        {
            EditorGUILayout.EndScrollView();
        }
    }

    // NEW: Overview Tab (existing content)
    private void DrawOverviewTab()
    {
        // Tiling Variants Section
        if (tilingVariantGroups != null && tilingVariantGroups.Count > 0)
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.HelpBox(
                $"🔧 Found {tilingVariantGroups.Count} TILING VARIANT groups! Fix these FIRST for maximum FPS gain.",
                MessageType.Warning
            );

            showTilingVariants = EditorGUILayout.Foldout(showTilingVariants, "Show Tiling Variant Groups");

            if (showTilingVariants)
            {
                EditorGUILayout.BeginVertical("box");
                int groupNum = 1;

                foreach (var group in tilingVariantGroups)
                {
                    if (group == null || group.Count == 0) continue;

                    EditorGUILayout.BeginVertical("box");

                    // NEW: Show priority and estimated impact
                    EditorGUILayout.BeginHorizontal();
                    EditorGUILayout.LabelField($"Priority #{groupNum} - Tiling Variant Group ({group.Count} variants)",
                        EditorStyles.boldLabel);

                    GUI.backgroundColor = Color.cyan;
                    EditorGUILayout.LabelField($"+{group.Count - 1} Draw Calls",
                        new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.cyan } },
                        GUILayout.Width(100));
                    GUI.backgroundColor = Color.white;
                    EditorGUILayout.EndHorizontal();

                    var first = group[0];
                    if (first.material != null)
                    {
                        EditorGUILayout.LabelField($"  Shader: {first.shader.name}", EditorStyles.miniLabel);
                        if (first.mainTexture != null)
                        {
                            EditorGUILayout.LabelField($"  Texture: {first.mainTexture.name}", EditorStyles.miniLabel);
                        }
                    }

                    EditorGUILayout.Space(3);

                    foreach (var matInfo in group)
                    {
                        if (matInfo.material == null) continue;

                        Vector2 tiling = matInfo.material.mainTextureScale;
                        Vector2 offset = matInfo.material.mainTextureOffset;

                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField($"    • {matInfo.material.name}", EditorStyles.miniLabel, GUILayout.Width(180));
                        EditorGUILayout.LabelField($"Tiling: ({tiling.x:F2}, {tiling.y:F2})",
                            EditorStyles.miniLabel, GUILayout.Width(110));
                        EditorGUILayout.LabelField($"({matInfo.usedByCount} objs)",
                            EditorStyles.miniLabel, GUILayout.Width(60));

                        if (GUILayout.Button("Ping", GUILayout.Width(50)))
                        {
                            EditorGUIUtility.PingObject(matInfo.material);
                        }
                        if (GUILayout.Button("Select", GUILayout.Width(60)))
                        {
                            Selection.objects = matInfo.usedByObjects.ToArray();
                        }
                        EditorGUILayout.EndHorizontal();
                    }

                    EditorGUILayout.Space(5);

                    GUI.backgroundColor = new Color(0.3f, 0.7f, 1f);
                    EditorGUILayout.HelpBox(
                        $"💡 FIX: Bake tiling into UVs → Save {group.Count - 1} draw calls (~{(group.Count - 1) * 2} FPS)",
                        MessageType.Info
                    );
                    GUI.backgroundColor = Color.white;

                    EditorGUILayout.EndVertical();
                    EditorGUILayout.Space(5);
                    groupNum++;
                }

                EditorGUILayout.EndVertical();
            }
        }

        // Duplicate Materials Section
        if (duplicateGroups != null && duplicateGroups.Count > 0)
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.HelpBox(
                $"⚠ Found {duplicateGroups.Count} EXACT DUPLICATE groups. Auto-consolidate NOW!",
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
                            EditorGUILayout.LabelField($"  Texture: {first.mainTexture.name}", EditorStyles.miniLabel);
                        }
                    }

                    EditorGUILayout.Space(3);

                    foreach (var matInfo in group)
                    {
                        if (matInfo.material == null) continue;

                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.LabelField($"    • {matInfo.material.name}", EditorStyles.miniLabel, GUILayout.Width(250));
                        EditorGUILayout.LabelField($"({matInfo.usedByCount} objects)", EditorStyles.miniLabel, GUILayout.Width(80));

                        if (GUILayout.Button("Ping Material", GUILayout.Width(100)))
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

                    GUI.backgroundColor = Color.yellow;
                    if (GUILayout.Button($"Consolidate Group {groupNum}", GUILayout.Height(25)))
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

                GUI.backgroundColor = new Color(1f, 0.5f, 0f);
                if (GUILayout.Button("⚠ CONSOLIDATE ALL DUPLICATES", GUILayout.Height(35)))
                {
                    if (EditorUtility.DisplayDialog("Consolidate All Duplicates",
                        $"Replace {duplicateGroups.Sum(g => g.Count - 1)} duplicates?",
                        "Yes", "Cancel"))
                    {
                        ConsolidateAllDuplicates();
                    }
                }
                GUI.backgroundColor = Color.white;

                EditorGUILayout.EndVertical();
            }
        }

        // GPU Instancing Analysis
        if (foundMaterials != null && foundMaterials.Count > 0)
        {
            EditorGUILayout.Space(5);
            showGPUInstancingAnalysis = EditorGUILayout.Foldout(showGPUInstancingAnalysis,
                "GPU Instancing Analysis");

            if (showGPUInstancingAnalysis)
            {
                EditorGUILayout.BeginVertical("box");
                EditorGUILayout.HelpBox(
                    "GPU Instancing: Dynamic objects (10+) with same mesh.\nStatic Batching: Static world geometry.",
                    MessageType.Info
                );

                instancingScrollPosition = EditorGUILayout.BeginScrollView(instancingScrollPosition, GUILayout.Height(250));

                foreach (var matInfo in foundMaterials)
                {
                    if (matInfo.material == null) continue;
                    AnalyzeGPUInstancingPotential(matInfo);
                }

                EditorGUILayout.EndScrollView();
                EditorGUILayout.EndVertical();
            }
        }

        // Material Details Preview
        if (showPreview && foundMaterials != null && foundMaterials.Count > 0)
        {
            EditorGUILayout.Space(5);
            EditorGUILayout.LabelField($"Material Details: {foundMaterials.Count} materials", EditorStyles.boldLabel);

            previewScrollPosition = EditorGUILayout.BeginScrollView(previewScrollPosition, GUILayout.Height(300));

            foreach (var matInfo in foundMaterials)
            {
                if (matInfo == null || matInfo.material == null) continue;

                EditorGUILayout.BeginVertical("box");

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(matInfo.material.name, EditorStyles.boldLabel);

                if (GUILayout.Button("Ping Material", GUILayout.Width(100)))
                {
                    EditorGUIUtility.PingObject(matInfo.material);
                }

                GUI.enabled = matInfo.usedByObjects != null && matInfo.usedByObjects.Count > 0;
                if (GUILayout.Button("Select Objects", GUILayout.Width(100)))
                {
                    Selection.objects = matInfo.usedByObjects.ToArray();
                }
                GUI.enabled = true;

                EditorGUILayout.EndHorizontal();

                EditorGUILayout.LabelField($"Shader: {matInfo.shader.name}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Used by: {matInfo.usedByCount} objects ({matInfo.staticCount} static, {matInfo.dynamicCount} dynamic)",
                    EditorStyles.miniLabel);

                if (matInfo.mainTexture is Texture2D tex)
                {
                    EditorGUILayout.LabelField($"Resolution: {tex.width}×{tex.height}",
                        new GUIStyle(EditorStyles.miniLabel)
                        {
                            normal = { textColor = tex.width > 2048 ? Color.red : Color.white }
                        });
                }

                Vector2 tiling = matInfo.material.mainTextureScale;
                EditorGUILayout.LabelField($"Tiling: ({tiling.x:F2}, {tiling.y:F2})", EditorStyles.miniLabel);

                // Object references foldout
                if (matInfo.usedByObjects != null && matInfo.usedByObjects.Count > 0)
                {
                    string foldoutKey = matInfo.guid;
                    if (!materialObjectFoldouts.ContainsKey(foldoutKey))
                    {
                        materialObjectFoldouts[foldoutKey] = false;
                    }

                    materialObjectFoldouts[foldoutKey] = EditorGUILayout.Foldout(
                        materialObjectFoldouts[foldoutKey],
                        $"Scene Objects ({matInfo.usedByObjects.Count})");

                    if (materialObjectFoldouts[foldoutKey])
                    {
                        EditorGUILayout.BeginVertical("box");

                        foreach (var obj in matInfo.usedByObjects.Take(10))
                        {
                            if (obj == null) continue;

                            EditorGUILayout.BeginHorizontal();

                            if (GUILayout.Button("→", GUILayout.Width(25)))
                            {
                                Selection.activeGameObject = obj;
                                EditorGUIUtility.PingObject(obj);
                            }

                            EditorGUILayout.LabelField(obj.name, EditorStyles.miniLabel);

                            EditorGUILayout.EndHorizontal();
                        }

                        if (matInfo.usedByObjects.Count > 10)
                        {
                            EditorGUILayout.LabelField($"... and {matInfo.usedByObjects.Count - 10} more",
                                EditorStyles.miniLabel);
                        }

                        EditorGUILayout.EndVertical();
                    }
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(3);
            }

            EditorGUILayout.EndScrollView();
        }
    }

    // NEW: Atlas Preview Tab
    private void DrawAtlasPreviewTab()
    {
        if (atlasCandidates == null || atlasCandidates.Count == 0)
        {
            EditorGUILayout.HelpBox("No atlas candidates found. Click 'Scan Scene' with 'Identify Atlas Candidates' enabled.", MessageType.Info);
            return;
        }

        EditorGUILayout.LabelField($"Atlas Candidate Groups: {atlasCandidates.Count}", EditorStyles.boldLabel);
        EditorGUILayout.HelpBox(
            "Materials grouped by shader, texture size, and keywords. These can be safely combined into texture atlases.",
            MessageType.Info
        );

        EditorGUILayout.Space(10);

        atlasPreviewScrollPosition = EditorGUILayout.BeginScrollView(atlasPreviewScrollPosition);

        int groupNum = 1;
        foreach (var group in atlasCandidates)
        {
            if (group == null || group.Count == 0) continue;

            EditorGUILayout.BeginVertical("box");

            // Group header
            var first = group[0];
            EditorGUILayout.LabelField($"Atlas Group {groupNum}: {group.Count} materials", EditorStyles.boldLabel);

            // NEW: Memory estimate - declare variables outside if block
            int totalTextures = group.Count;
            int textureMemoryMB = 0;
            int atlasMemoryMB = 0;
            int memorySavingMB = 0;

            if (first.material != null)
            {
                EditorGUILayout.LabelField($"Shader: {first.shader.name}", EditorStyles.miniLabel);
                if (first.textureSize.x > 0)
                {
                    EditorGUILayout.LabelField($"Texture Size: {first.textureSize.x}×{first.textureSize.y}", EditorStyles.miniLabel);

                    // Calculate memory estimates
                    textureMemoryMB = (first.textureSize.x * first.textureSize.y * 4 * totalTextures) / (1024 * 1024);
                    atlasMemoryMB = (Mathf.NextPowerOfTwo(first.textureSize.x * 2) * Mathf.NextPowerOfTwo(first.textureSize.y * 2) * 4) / (1024 * 1024);
                    memorySavingMB = textureMemoryMB - atlasMemoryMB;

                    EditorGUILayout.LabelField($"Current Memory: ~{textureMemoryMB}MB | After Atlas: ~{atlasMemoryMB}MB | Savings: ~{memorySavingMB}MB",
                        EditorStyles.miniLabel);
                }

                EditorGUILayout.LabelField($"Draw Call Reduction: {group.Count} → 1 (save {group.Count - 1} draw calls)",
                    new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.green } });
            }

            EditorGUILayout.Space(5);

            // NEW: Texture preview grid
            EditorGUILayout.LabelField("Textures in Group:", EditorStyles.miniLabel);

            int previewSize = 64;
            int texPerRow = Mathf.FloorToInt((EditorGUIUtility.currentViewWidth - 50) / (previewSize + 5));
            int currentRow = 0;

            EditorGUILayout.BeginHorizontal();

            foreach (var matInfo in group)
            {
                if (matInfo.material == null || matInfo.mainTexture == null) continue;

                if (currentRow >= texPerRow)
                {
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.BeginHorizontal();
                    currentRow = 0;
                }

                EditorGUILayout.BeginVertical(GUILayout.Width(previewSize));

                // Texture preview
                Rect previewRect = GUILayoutUtility.GetRect(previewSize, previewSize);
                EditorGUI.DrawPreviewTexture(previewRect, matInfo.mainTexture);

                // Material name (truncated)
                string shortName = matInfo.material.name;
                if (shortName.Length > 10) shortName = shortName.Substring(0, 10) + "...";
                EditorGUILayout.LabelField(shortName, EditorStyles.miniLabel);

                EditorGUILayout.EndVertical();

                currentRow++;
            }

            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(5);

            // Material list
            EditorGUILayout.LabelField("Materials:", EditorStyles.miniLabel);
            foreach (var matInfo in group)
            {
                if (matInfo.material == null) continue;

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"  • {matInfo.material.name}", EditorStyles.miniLabel, GUILayout.Width(250));
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

            EditorGUILayout.Space(5);

            // NEW: Create atlas suggestion
            GUI.backgroundColor = Color.green;
            if (GUILayout.Button($"💡 Create Atlas for Group {groupNum} (External Tool Required)", GUILayout.Height(30)))
            {
                EditorUtility.DisplayDialog("Create Texture Atlas",
                    $"To create an atlas for this group:\n\n" +
                    $"1. Export these {group.Count} textures\n" +
                    $"2. Use TexturePacker, Unity Sprite Atlas, or similar tool\n" +
                    $"3. Create atlas texture (recommended size: {Mathf.NextPowerOfTwo(first.textureSize.x * 2)}×{Mathf.NextPowerOfTwo(first.textureSize.y * 2)})\n" +
                    $"4. Update material to use atlased texture\n" +
                    $"5. Update UVs to match atlas layout\n\n" +
                    $"Estimated savings: {group.Count - 1} draw calls, ~{memorySavingMB}MB memory",
                    "Got it");
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(10);

            groupNum++;
        }

        EditorGUILayout.EndScrollView();
    }

    // NEW: VR Performance Tab
    private void DrawVRPerformanceTab()
    {
        if (foundMaterials == null || foundMaterials.Count == 0)
        {
            EditorGUILayout.HelpBox("No materials found. Click 'Scan Scene' first.", MessageType.Info);
            return;
        }

        EditorGUILayout.BeginVertical("box");
        EditorGUILayout.LabelField("VR Performance Analysis (Quest 3 Optimized)", EditorStyles.boldLabel);

        // Overall score
        Color scoreColor = overallSceneScore >= 80 ? Color.green :
                          overallSceneScore >= 60 ? Color.yellow : Color.red;

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Overall Scene Score:", EditorStyles.boldLabel, GUILayout.Width(150));
        EditorGUILayout.LabelField($"{overallSceneScore:F1}/100",
            new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = scoreColor } });
        EditorGUILayout.EndHorizontal();

        string scoreDescription = overallSceneScore >= 80 ? "Excellent - VR Ready" :
                                  overallSceneScore >= 60 ? "Good - Minor optimization needed" :
                                  overallSceneScore >= 40 ? "Fair - Optimization recommended" :
                                  "Poor - Requires significant optimization";

        EditorGUILayout.LabelField(scoreDescription, EditorStyles.miniLabel);

        EditorGUILayout.Space(5);

        EditorGUILayout.LabelField($"Estimated Draw Call Savings: {estimatedDrawCallSavings}", EditorStyles.boldLabel);
        EditorGUILayout.LabelField($"Estimated FPS Improvement: +{estimatedDrawCallSavings * 0.5f:F0} FPS", EditorStyles.miniLabel);

        EditorGUILayout.EndVertical();

        EditorGUILayout.Space(10);

        // Priority issues
        EditorGUILayout.LabelField("Priority Issues to Fix:", EditorStyles.boldLabel);

        performanceScrollPosition = EditorGUILayout.BeginScrollView(performanceScrollPosition);

        // Sort materials by performance score (worst first)
        var sortedMaterials = foundMaterials
            .Where(m => m.material != null && performanceScores.ContainsKey(m.material))
            .OrderBy(m => performanceScores[m.material].totalScore)
            .ToList();

        int priority = 1;
        foreach (var matInfo in sortedMaterials)
        {
            if (!performanceScores.ContainsKey(matInfo.material)) continue;

            var score = performanceScores[matInfo.material];

            // Only show problematic materials (score < 70)
            if (score.totalScore >= 70) continue;

            EditorGUILayout.BeginVertical("box");

            // Header
            EditorGUILayout.BeginHorizontal();

            Color priorityColor = score.totalScore < 40 ? Color.red :
                                 score.totalScore < 60 ? new Color(1f, 0.5f, 0f) : Color.yellow;

            GUI.backgroundColor = priorityColor;
            EditorGUILayout.LabelField($"#{priority}", EditorStyles.boldLabel, GUILayout.Width(30));
            GUI.backgroundColor = Color.white;

            EditorGUILayout.LabelField(matInfo.material.name, EditorStyles.boldLabel, GUILayout.Width(250));
            EditorGUILayout.LabelField($"Score: {score.totalScore:F0}/100",
                new GUIStyle(EditorStyles.boldLabel) { normal = { textColor = priorityColor } },
                GUILayout.Width(100));

            if (GUILayout.Button("Ping", GUILayout.Width(50)))
            {
                EditorGUIUtility.PingObject(matInfo.material);
            }
            if (GUILayout.Button("Select Objects", GUILayout.Width(100)))
            {
                Selection.objects = matInfo.usedByObjects.ToArray();
            }

            EditorGUILayout.EndHorizontal();

            // Performance breakdown
            EditorGUILayout.LabelField("Issues:", EditorStyles.miniLabel);

            foreach (var issue in score.issues)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField($"  • {issue}",
                    new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.yellow } });
                EditorGUILayout.EndHorizontal();
            }

            if (score.recommendations.Count > 0)
            {
                EditorGUILayout.Space(3);
                EditorGUILayout.LabelField("Recommendations:", EditorStyles.miniLabel);

                foreach (var rec in score.recommendations)
                {
                    EditorGUILayout.LabelField($"  💡 {rec}",
                        new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.cyan } });
                }
            }

            EditorGUILayout.Space(3);
            EditorGUILayout.LabelField($"Impact: {score.impactDescription}",
                new GUIStyle(EditorStyles.miniLabel) { fontStyle = FontStyle.Italic });

            EditorGUILayout.EndVertical();
            EditorGUILayout.Space(5);

            priority++;

            // Limit to top 20 issues
            if (priority > 20) break;
        }

        if (priority == 1)
        {
            EditorGUILayout.HelpBox("✓ No major performance issues detected! Your materials are well-optimized for VR.", MessageType.Info);
        }

        EditorGUILayout.EndScrollView();
    }

    // NEW: Auto-Optimization workflow
    private void PerformAutoOptimization()
    {
        if (foundMaterials == null || foundMaterials.Count == 0)
        {
            EditorUtility.DisplayDialog("Error", "Please scan scene first.", "OK");
            return;
        }

        int duplicatesToFix = duplicateGroups != null ? duplicateGroups.Sum(g => g.Count - 1) : 0;
        int tilingVariantsWarning = tilingVariantGroups != null ? tilingVariantGroups.Count : 0;

        string message = "Auto-Optimization will:\n\n";

        if (duplicatesToFix > 0)
        {
            message += $"✓ Consolidate {duplicatesToFix} duplicate materials\n";
        }

        if (tilingVariantsWarning > 0)
        {
            message += $"⚠ Alert: {tilingVariantsWarning} tiling variant groups need MANUAL fix (bake UVs)\n";
        }

        message += $"\nEstimated draw call savings: {estimatedDrawCallSavings}\n";
        message += $"Estimated FPS improvement: +{estimatedDrawCallSavings * 0.5f:F0} FPS\n\n";
        message += "Continue?";

        if (!EditorUtility.DisplayDialog("Auto-Optimize Scene", message, "Yes", "Cancel"))
        {
            return;
        }

        int totalFixed = 0;

        // Consolidate duplicates
        if (duplicateGroups != null && duplicateGroups.Count > 0)
        {
            ConsolidateAllDuplicates();
            totalFixed += duplicatesToFix;
        }

        // Report results
        string resultMessage = $"Auto-Optimization Complete!\n\n";
        resultMessage += $"✓ Fixed {totalFixed} duplicate materials\n";

        if (tilingVariantsWarning > 0)
        {
            resultMessage += $"\n⚠ MANUAL ACTION REQUIRED:\n";
            resultMessage += $"  • {tilingVariantsWarning} tiling variant groups need UV baking\n";
            resultMessage += $"  • Switch to 'Overview' tab to see details\n";
        }

        resultMessage += $"\nEstimated improvement: +{totalFixed * 0.5f:F0} FPS";

        EditorUtility.DisplayDialog("Optimization Complete", resultMessage, "OK");
    }

    // NEW: Calculate VR performance scores
    private void CalculateVRPerformanceScores()
    {
        performanceScores.Clear();
        estimatedDrawCallSavings = 0;
        float totalScore = 0;
        int scoredMaterials = 0;

        foreach (var matInfo in foundMaterials)
        {
            if (matInfo.material == null) continue;

            var score = new VRPerformanceScore();
            score.issues = new List<string>();
            score.recommendations = new List<string>();

            // Base score
            score.totalScore = 100;

            // Check texture resolution (Quest 3 prefers 1K or less)
            if (matInfo.mainTexture is Texture2D tex)
            {
                if (tex.width > 2048 || tex.height > 2048)
                {
                    score.totalScore -= 30;
                    score.issues.Add($"Texture too large: {tex.width}×{tex.height} (>2K kills Quest 3 performance)");
                    score.recommendations.Add($"Downscale to 1024×1024 or 512×512");
                }
                else if (tex.width > 1024 || tex.height > 1024)
                {
                    score.totalScore -= 15;
                    score.issues.Add($"Texture large: {tex.width}×{tex.height} (consider 1K or less for VR)");
                    score.recommendations.Add($"Consider downscaling to 1024×1024");
                }
            }

            // Check shader keywords (breaks instancing)
            if (matInfo.material.shaderKeywords != null && matInfo.material.shaderKeywords.Length > 0)
            {
                score.totalScore -= 20;
                score.issues.Add($"Has {matInfo.material.shaderKeywords.Length} shader keywords (breaks GPU instancing)");
                score.recommendations.Add("Use simpler shader or disable keywords");
            }

            // Check usage count
            if (matInfo.usedByCount > 50)
            {
                score.totalScore -= 10;
                score.issues.Add($"Used by {matInfo.usedByCount} objects (high draw call impact)");
                score.recommendations.Add("Consider GPU instancing or atlasing");
            }

            // Check if it's a duplicate or tiling variant
            bool isDuplicate = duplicateGroups != null && duplicateGroups.Any(g => g.Any(m => m.guid == matInfo.guid));
            bool isTilingVariant = tilingVariantGroups != null && tilingVariantGroups.Any(g => g.Any(m => m.guid == matInfo.guid));

            if (isDuplicate)
            {
                score.totalScore -= 25;
                score.issues.Add("Duplicate material detected");
                score.recommendations.Add("Auto-consolidate to save draw calls");
                estimatedDrawCallSavings++;
            }

            if (isTilingVariant)
            {
                score.totalScore -= 20;
                score.issues.Add("Tiling variant detected");
                score.recommendations.Add("Bake tiling into UVs");
                estimatedDrawCallSavings++;
            }

            // Impact description
            if (score.totalScore < 40)
            {
                score.impactDescription = "CRITICAL - Fix immediately for VR";
            }
            else if (score.totalScore < 60)
            {
                score.impactDescription = "HIGH - Significant FPS impact";
            }
            else if (score.totalScore < 80)
            {
                score.impactDescription = "MEDIUM - Minor optimization recommended";
            }
            else
            {
                score.impactDescription = "LOW - Already well optimized";
            }

            performanceScores[matInfo.material] = score;
            totalScore += score.totalScore;
            scoredMaterials++;
        }

        // Calculate overall scene score
        if (scoredMaterials > 0)
        {
            overallSceneScore = totalScore / scoredMaterials;
        }
    }

    private void ScanScene()
    {
        foundMaterials = new List<MaterialInfo>();
        instanceMaterials = new List<MaterialInfo>();
        materialObjectFoldouts.Clear();

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

        Debug.Log($"<color=cyan>Scanned {totalRenderers} renderers</color>");

        foundMaterials = materialDict.Values.OrderByDescending(m => m.usedByCount).ToList();
        instanceMaterials = instanceDict.Values.OrderByDescending(m => m.usedByCount).ToList();

        DetectTilingVariants();
        DetectDuplicateMaterials();

        if (showAtlasCandidates)
        {
            IdentifyAtlasCandidates();
        }

        // NEW: Calculate VR performance scores
        CalculateVRPerformanceScores();

        Debug.Log($"<color=green>✓ Found {foundMaterials.Count} materials | Score: {overallSceneScore:F1}/100</color>");

        if (tilingVariantGroups != null && tilingVariantGroups.Count > 0)
        {
            Debug.LogWarning($"🔧 {tilingVariantGroups.Count} tiling variant groups - FIX FIRST!");
        }

        if (duplicateGroups != null && duplicateGroups.Count > 0)
        {
            Debug.LogWarning($"⚠ {duplicateGroups.Count} duplicate groups - AUTO-CONSOLIDATE");
        }

        Repaint();
    }

    // Rest of the implementation (same as V3)
    // Including: ScanRendererType, TrackMaterial, DetectTilingVariants, DetectDuplicateMaterials,
    // ConsolidateDuplicateGroup, ConsolidateAllDuplicates, ReplaceMaterialReferences,
    // OrganizeMaterials, IdentifyAtlasCandidates, AnalyzeGPUInstancingPotential, etc.

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
            bool isStatic = renderer.gameObject.isStatic;

            foreach (var material in materials)
            {
                if (material == null) continue;
                TrackMaterial(material, typeof(T), renderer.gameObject, isStatic, materialDict, instanceDict, ref filteredCount);
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
                bool isStatic = terrain.gameObject.isStatic;
                TrackMaterial(terrain.materialTemplate, typeof(Terrain), terrain.gameObject, isStatic, materialDict, instanceDict, ref filteredCount);
            }
        }
    }

    private void TrackMaterial(Material material, System.Type rendererType, GameObject gameObject, bool isStatic,
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
                usedByObjects = new List<GameObject>(),
                guid = key,
                staticCount = 0,
                dynamicCount = 0
            };

            if (material.mainTexture is Texture2D tex)
            {
                info.textureSize = new Vector2Int(tex.width, tex.height);
            }

            targetDict[key] = info;
        }

        targetDict[key].usedByCount++;
        targetDict[key].rendererTypes.Add(rendererType);

        if (!targetDict[key].usedByObjects.Contains(gameObject))
        {
            targetDict[key].usedByObjects.Add(gameObject);
        }

        if (isStatic)
        {
            targetDict[key].staticCount++;
        }
        else
        {
            targetDict[key].dynamicCount++;
        }
    }

    private void DetectTilingVariants()
    {
        tilingVariantGroups = new List<List<MaterialInfo>>();

        var groups = foundMaterials
            .Where(m => m.material != null)
            .GroupBy(m => GetMaterialSignatureWithoutTiling(m.material))
            .Where(g => g.Count() > 1);

        foreach (var group in groups)
        {
            var groupList = group.ToList();

            bool onlyTilingDiffers = true;
            Vector2 firstTiling = groupList[0].material.mainTextureScale;
            Vector2 firstOffset = groupList[0].material.mainTextureOffset;

            foreach (var mat in groupList.Skip(1))
            {
                Vector2 tiling = mat.material.mainTextureScale;
                Vector2 offset = mat.material.mainTextureOffset;

                if (tiling == firstTiling && offset == firstOffset)
                {
                    onlyTilingDiffers = false;
                    break;
                }
            }

            if (onlyTilingDiffers)
            {
                tilingVariantGroups.Add(groupList);
            }
        }

        tilingVariantGroups = tilingVariantGroups.OrderByDescending(g => g.Count).ToList();
    }

    private string GetMaterialSignatureWithoutTiling(Material mat)
    {
        var signature = new System.Text.StringBuilder();

        signature.Append(mat.shader.name);
        signature.Append("|");

        if (mat.mainTexture != null)
        {
            signature.Append(mat.mainTexture.GetInstanceID());
        }
        signature.Append("|");

        if (mat.HasProperty("_Color"))
        {
            Color c = mat.GetColor("_Color");
            signature.Append($"{c.r:F2},{c.g:F2},{c.b:F2},{c.a:F2}");
        }
        signature.Append("|");

        if (mat.shaderKeywords != null && mat.shaderKeywords.Length > 0)
        {
            signature.Append(string.Join(",", mat.shaderKeywords.OrderBy(k => k)));
        }
        signature.Append("|");

        signature.Append(mat.renderQueue);

        return signature.ToString();
    }

    private void DetectDuplicateMaterials()
    {
        duplicateGroups = new List<List<MaterialInfo>>();

        var groups = foundMaterials
            .Where(m => m.material != null)
            .GroupBy(m => GetMaterialSignature(m.material))
            .Where(g => g.Count() > 1)
            .OrderByDescending(g => g.Count());

        foreach (var group in groups)
        {
            duplicateGroups.Add(group.ToList());
        }
    }

    private string GetMaterialSignature(Material mat)
    {
        var signature = new System.Text.StringBuilder();

        signature.Append(mat.shader.name);
        signature.Append("|");

        if (mat.mainTexture != null)
        {
            signature.Append(mat.mainTexture.GetInstanceID());
        }
        signature.Append("|");

        if (mat.HasProperty("_Color"))
        {
            Color c = mat.GetColor("_Color");
            signature.Append($"{c.r:F2},{c.g:F2},{c.b:F2},{c.a:F2}");
        }
        signature.Append("|");

        if (mat.shaderKeywords != null && mat.shaderKeywords.Length > 0)
        {
            signature.Append(string.Join(",", mat.shaderKeywords.OrderBy(k => k)));
        }
        signature.Append("|");

        signature.Append(mat.renderQueue);
        signature.Append("|");

        Vector2 tiling = mat.mainTextureScale;
        Vector2 offset = mat.mainTextureOffset;
        signature.Append($"{tiling.x:F3},{tiling.y:F3}|{offset.x:F3},{offset.y:F3}");

        return signature.ToString();
    }

    private void ConsolidateDuplicateGroup(List<MaterialInfo> group)
    {
        if (group == null || group.Count < 2) return;

        Material keepMaterial = group[0].material;
        List<Material> toReplace = group.Skip(1).Select(m => m.material).ToList();

        int replacedCount = ReplaceMaterialReferences(toReplace, keepMaterial);

        Debug.Log($"<color=green>✓ Consolidated {group.Count} materials ({replacedCount} refs)</color>");

        ScanScene();
    }

    private void ConsolidateAllDuplicates()
    {
        if (duplicateGroups == null || duplicateGroups.Count == 0) return;

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

        Debug.Log($"<color=green>✓ Consolidated {totalConsolidated} duplicates ({totalReferences} refs)</color>");

        ScanScene();
    }

    private int ReplaceMaterialReferences(List<Material> oldMaterials, Material newMaterial)
    {
        int replacedCount = 0;

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
            EditorUtility.DisplayDialog("No Materials", "Please scan first.", "OK");
            return;
        }

        if (!AssetDatabase.IsValidFolder(targetFolderPath))
        {
            CreateFolderRecursive(targetFolderPath);
        }

        int successCount = 0;

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
        if (tilingVariantGroups != null) tilingVariantGroups.Clear();

        Debug.Log($"<color=green>Organized {successCount} materials</color>");
        ScanScene();
    }

    private string GetTargetPath(MaterialInfo matInfo)
    {
        string groupFolder = targetFolderPath;

        switch (groupingMode)
        {
            case GroupingMode.Shader:
                string shaderName = CleanFileName(matInfo.shader.name.Replace("/", "_"));
                groupFolder = targetFolderPath + "/" + shaderName;
                break;

            case GroupingMode.Texture:
                if (matInfo.mainTexture != null)
                {
                    string texName = CleanFileName(matInfo.mainTexture.name);
                    groupFolder = targetFolderPath + "/" + texName;
                }
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
                Keywords = string.Join("|", (m.material.shaderKeywords ?? new string[0]).OrderBy(k => k))
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

                if (tiling == Vector2.one)
                {
                    compatibleMaterials.Add(mat);
                }
            }

            if (compatibleMaterials.Count >= 2)
            {
                atlasCandidates.Add(compatibleMaterials);
            }
        }
    }

    private void AnalyzeGPUInstancingPotential(MaterialInfo matInfo)
    {
        if (matInfo.material == null) return;

        EditorGUILayout.BeginVertical("box");

        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField(matInfo.material.name, EditorStyles.boldLabel, GUILayout.Width(250));

        bool hasKeywords = matInfo.material.shaderKeywords != null && matInfo.material.shaderKeywords.Length > 0;
        bool hasEnoughInstances = matInfo.usedByCount >= 10;
        bool hasDynamicObjects = matInfo.dynamicCount > 0;

        if (!hasKeywords && hasDynamicObjects && hasEnoughInstances)
        {
            GUI.backgroundColor = Color.green;
            EditorGUILayout.LabelField("✓ GOOD FOR INSTANCING", EditorStyles.miniLabel, GUILayout.Width(150));
            GUI.backgroundColor = Color.white;
        }
        else if (matInfo.staticCount > matInfo.dynamicCount)
        {
            GUI.backgroundColor = new Color(0.5f, 0.5f, 1f);
            EditorGUILayout.LabelField("USE STATIC BATCHING", EditorStyles.miniLabel, GUILayout.Width(150));
            GUI.backgroundColor = Color.white;
        }
        else
        {
            GUI.backgroundColor = Color.yellow;
            EditorGUILayout.LabelField("⚠ CHECK CONDITIONS", EditorStyles.miniLabel, GUILayout.Width(150));
            GUI.backgroundColor = Color.white;
        }

        EditorGUILayout.EndHorizontal();

        EditorGUILayout.LabelField($"  Usage: {matInfo.usedByCount} objects ({matInfo.staticCount} static, {matInfo.dynamicCount} dynamic)",
            EditorStyles.miniLabel);
        EditorGUILayout.LabelField($"  Keywords: {(hasKeywords ? matInfo.material.shaderKeywords.Length + " (breaks instancing)" : "None ✓")}",
            hasKeywords ? new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = Color.red } } : EditorStyles.miniLabel);

        EditorGUILayout.EndVertical();
    }

    private string CleanFileName(string name)
    {
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }
        return name;
    }

    // NEW: VR Performance Score class
    private class VRPerformanceScore
    {
        public float totalScore;
        public List<string> issues;
        public List<string> recommendations;
        public string impactDescription;
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
        public List<GameObject> usedByObjects;
        public string guid;
        public Vector2Int textureSize;
        public int staticCount;
        public int dynamicCount;
    }
}