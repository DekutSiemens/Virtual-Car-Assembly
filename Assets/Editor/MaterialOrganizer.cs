using UnityEngine;
using UnityEditor;
using UnityEditor.SceneManagement;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
/// Production-Grade Material Organizer for VR Optimization
/// Handles ALL renderer types, material instances, and shader-based grouping
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
    private List<MaterialInfo> instanceMaterials; // Material instances (no asset)
    private List<List<MaterialInfo>> atlasCandidates; // Groups of materials that can be atlased together
    private Vector2 scrollPosition;
    private bool showInstances = true;
    private bool showAtlasGroups = false;
    
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
            "Production-grade material scanner. Finds ALL materials in scene including instances, " +
            "handles all renderer types, and groups by shader compatibility for atlasing.",
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
            
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(300));
            
            foreach (var matInfo in foundMaterials)
            {
                EditorGUILayout.BeginVertical("box");
                
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(matInfo.material.name, EditorStyles.boldLabel);
                if (GUILayout.Button("Ping", GUILayout.Width(50)))
                {
                    EditorGUIUtility.PingObject(matInfo.material);
                }
                EditorGUILayout.EndHorizontal();
                
                EditorGUILayout.LabelField($"Path: {matInfo.assetPath}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Shader: {matInfo.shader.name}", EditorStyles.miniLabel);
                EditorGUILayout.LabelField($"Used by: {matInfo.usedByCount} objects on {matInfo.rendererTypes.Count} renderer type(s)", EditorStyles.miniLabel);
                
                if (matInfo.mainTexture != null)
                {
                    EditorGUILayout.LabelField($"Main Texture: {matInfo.mainTexture.name}", EditorStyles.miniLabel);
                    
                    // CRITICAL: Show texture resolution for performance analysis
                    if (matInfo.mainTexture is Texture2D tex)
                    {
                        string resolutionColor = tex.width > 2048 || tex.height > 2048 ? "red" : "white";
                        EditorGUILayout.LabelField($"Resolution: {tex.width}×{tex.height}", 
                            new GUIStyle(EditorStyles.miniLabel) { 
                                normal = { textColor = tex.width > 2048 ? Color.red : Color.white } 
                            });
                    }
                }
                
                // CRITICAL: Show shader keywords (affects GPU instancing)
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
                
                // Show renderer types
                if (matInfo.rendererTypes.Count > 0)
                {
                    EditorGUILayout.LabelField($"Renderers: {string.Join(", ", matInfo.rendererTypes.Select(t => t.Name))}", 
                        EditorStyles.miniLabel);
                }
                
                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(3);
            }
            
            EditorGUILayout.EndScrollView();
        }
    }
    
    private void ScanScene()
    {
        foundMaterials = new List<MaterialInfo>();
        instanceMaterials = new List<MaterialInfo>();
        Dictionary<string, MaterialInfo> materialDict = new Dictionary<string, MaterialInfo>(); // Use GUID as key
        Dictionary<string, MaterialInfo> instanceDict = new Dictionary<string, MaterialInfo>();
        
        int totalRenderers = 0;
        int filteredCount = 0;
        
        // CRITICAL: Scan ALL renderer types
        totalRenderers += ScanRendererType<MeshRenderer>(materialDict, instanceDict, ref filteredCount);
        totalRenderers += ScanRendererType<SkinnedMeshRenderer>(materialDict, instanceDict, ref filteredCount);
        totalRenderers += ScanRendererType<SpriteRenderer>(materialDict, instanceDict, ref filteredCount);
        totalRenderers += ScanRendererType<LineRenderer>(materialDict, instanceDict, ref filteredCount);
        totalRenderers += ScanRendererType<TrailRenderer>(materialDict, instanceDict, ref filteredCount);
        totalRenderers += ScanRendererType<ParticleSystemRenderer>(materialDict, instanceDict, ref filteredCount);
        
        // Terrain materials (special case)
        ScanTerrainMaterials(materialDict, instanceDict, ref filteredCount);
        
        Debug.Log($"<color=cyan>Scanned {totalRenderers} renderers across all types</color>");
        if (filteredCount > 0)
        {
            Debug.Log($"<color=yellow>Filtered out {filteredCount} built-in/package materials</color>");
        }
        
        // Convert to lists and sort
        foundMaterials = materialDict.Values.OrderByDescending(m => m.usedByCount).ToList();
        instanceMaterials = instanceDict.Values.OrderByDescending(m => m.usedByCount).ToList();
        
        // Identify atlas candidates
        if (showAtlasCandidates)
        {
            IdentifyAtlasCandidates();
        }
        
        // Log results
        Debug.Log($"<color=green>✓ Found {foundMaterials.Count} asset materials</color>");
        
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
        
        // CRITICAL: Get active scene for validation
        var activeScene = EditorSceneManager.GetActiveScene();
        
        if (includeInactive)
        {
            // CRITICAL: Proper filtering to exclude prefabs and editor objects
            renderers = Resources.FindObjectsOfTypeAll<T>()
                .Where(r => 
                {
                    // CRITICAL: Must be in the ACTIVE scene (not just any loaded scene)
                    if (r.gameObject.scene != activeScene) return false;
                    
                    // Must be in a loaded scene
                    if (!r.gameObject.scene.isLoaded) return false;
                    
                    // CRITICAL: Exclude prefab assets and prefab stages
                    var stage = PrefabStageUtility.GetPrefabStage(r.gameObject);
                    if (stage != null) return false;
                    
                    // Exclude objects with HideFlags (editor-only, hidden, etc)
                    if (r.gameObject.hideFlags != HideFlags.None) return false;
                    
                    // Must have valid scene name (not empty/null)
                    if (string.IsNullOrEmpty(r.gameObject.scene.name)) return false;
                    
                    // CRITICAL: Exclude prefab assets in Project view
                    string assetPath = AssetDatabase.GetAssetPath(r.gameObject);
                    if (!string.IsNullOrEmpty(assetPath)) return false;
                    
                    return true;
                })
                .ToArray();
        }
        else
        {
            // FindObjectsOfType only returns active scene objects (safer)
            renderers = Object.FindObjectsByType<T>(FindObjectsSortMode.None);

        }

        foreach (var renderer in renderers)
        {
            Material[] materials = renderer.sharedMaterials;
            
            foreach (var material in materials)
            {
                if (material == null) continue;
                
                TrackMaterial(material, typeof(T), materialDict, instanceDict, ref filteredCount);
            }
        }
        
        return renderers.Length;
    }
    
    private void ScanTerrainMaterials(Dictionary<string, MaterialInfo> materialDict, 
                                      Dictionary<string, MaterialInfo> instanceDict,
                                      ref int filteredCount)
    {
        Terrain[] terrains;
        
        // CRITICAL: Get active scene for validation
        var activeScene = EditorSceneManager.GetActiveScene();
        
        if (includeInactive)
        {
            // CRITICAL: Same filtering as renderers
            terrains = Resources.FindObjectsOfTypeAll<Terrain>()
                .Where(t => 
                {
                    // CRITICAL: Must be in the ACTIVE scene
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
                TrackMaterial(terrain.materialTemplate, typeof(Terrain), materialDict, instanceDict, ref filteredCount);
            }
        }
    }
    
    private void TrackMaterial(Material material, System.Type rendererType,
                              Dictionary<string, MaterialInfo> materialDict,
                              Dictionary<string, MaterialInfo> instanceDict,
                              ref int filteredCount)
    {
        string assetPath = AssetDatabase.GetAssetPath(material);
        bool isInstance = string.IsNullOrEmpty(assetPath);
        
        // CRITICAL: Filter out Unity built-in materials
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
        
        // CRITICAL: Filter out package materials
        if (!isInstance && filterPackages)
        {
            if (assetPath.StartsWith("Packages/"))
            {
                filteredCount++;
                return;
            }
        }
        
        var targetDict = isInstance ? instanceDict : materialDict;
        
        // Use GUID for asset materials to avoid duplicate counting
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
                guid = key
            };
            
            // Get texture size for performance analysis
            if (material.mainTexture is Texture2D tex)
            {
                info.textureSize = new Vector2Int(tex.width, tex.height);
            }
            
            targetDict[key] = info;
        }
        
        targetDict[key].usedByCount++;
        targetDict[key].rendererTypes.Add(rendererType);
    }
    
    private void OrganizeMaterials()
    {
        if (foundMaterials == null || foundMaterials.Count == 0)
        {
            EditorUtility.DisplayDialog("No Materials", "Please scan the scene first.", "OK");
            return;
        }
        
        // Ensure target folder exists
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
                string targetPath = GetTargetPath(matInfo);
                
                // Avoid overwriting existing files
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
        
        string message = $"Organized {successCount} materials into {targetFolderPath}";
        if (errorCount > 0)
        {
            message += $"\n{errorCount} errors occurred (check console)";
        }
        
        EditorUtility.DisplayDialog("Complete", message, "OK");
        Debug.Log($"<color=green>{message}</color>");
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
        
        // Create folder if needed
        if (groupFolder != targetFolderPath && !AssetDatabase.IsValidFolder(groupFolder))
        {
            CreateFolderRecursive(groupFolder);
        }
        
        // CRITICAL: Use forward slash for Unity asset paths, NOT Path.Combine
        return groupFolder + "/" + System.IO.Path.GetFileName(matInfo.assetPath);
    }
    
    private void CreateFolderRecursive(string path)
    {
        string[] folders = path.Split('/');
        string currentPath = folders[0]; // "Assets"
        
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
        
        // CRITICAL: Group by shader + texture size + KEYWORD SET (not count)
        var groups = foundMaterials
            .Where(m => m.mainTexture != null && m.textureSize.x > 0) // Only materials with textures
            .GroupBy(m => new
            {
                Shader = m.shader,
                TextureWidth = m.textureSize.x,
                TextureHeight = m.textureSize.y,
                // CRITICAL: Use keyword SET, not count - materials must have IDENTICAL keywords
                Keywords = string.Join("|",
                (m.material.shaderKeywords ?? new string[0]).OrderBy(k => k))

            })
            .Where(g => g.Count() >= 2) // Only groups with 2+ materials
            .OrderByDescending(g => g.Count());
        
        foreach (var group in groups)
        {
            var groupList = group.ToList();
            
            // Additional filtering: check if materials are truly compatible
            var compatibleMaterials = new List<MaterialInfo>();
            
            foreach (var mat in groupList)
            {
                // CRITICAL: Materials with tiling != 1,1 CANNOT be atlased (breaks UVs)
                Vector2 tiling = mat.material.mainTextureScale;
                bool isCompatible = true;
                
                if (tiling != Vector2.one)
                {
                    // Materials with custom tiling break atlas UVs
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
        public string guid; // GUID for asset materials, InstanceID for instances
        public Vector2Int textureSize; // For performance analysis and atlas grouping
    }
}
