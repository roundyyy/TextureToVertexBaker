using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

public class TextureToVertexBaker : EditorWindow
{
    private GameObject targetObject;
    private Material sharedMaterial;
    private float textureStrength = 1.0f;
    private float textureContrast = 1.0f;
    private float textureBrightness = 0.0f;
    private string texturePropertyName = "_MainTex"; // Default to '_MainTex'

    // Debug mode
    private bool debugMode = false;

    // Moved to post-processing section
    private bool averageColors = false;
    private float colorAverageStrength = 0.5f;

    // New option to keep existing vertex colors
    private bool keepOldColors = true;

    // New features
    private bool optimizeVertices = false;
    private float colorSimilarityThreshold = 0.1f; // Default value for color similarity threshold
    private bool colorTintEnabled = false;
    private Color colorTint = Color.white;

    // New feature: Average Neighbor Colors
    private bool averageNeighborColors = false;
    private float neighborRadius = 0.1f; // Default radius in world units
    private float neighborAverageStrength = 0.5f; // Default strength for averaging

    // Statistics
    private int totalVerticesBefore = 0;
    private int totalVerticesAfterProcessing = 0;
    private int totalVerticesAfterOptimization = 0;

    // List to store sampling points for debug visualization
    private List<Vector3> debugSamplingPoints = new List<Vector3>();

    // Timer for debug visualization
    private double debugStartTime = 0;

    // New variables for prefab processing
    private bool applyToPrefabs = false;
    private HashSet<string> processedPrefabAssets = new HashSet<string>();

    // Custom undo system for tool operations
    private class MeshUndoData
    {
        public MeshFilter meshFilter;
        public Mesh originalMesh;
        public Material[] originalMaterials;
        public MeshCollider meshCollider;
        public Mesh originalColliderMesh;
        public string createdMeshPath; // Path of mesh created by this operation (to delete on undo)
    }
    private List<MeshUndoData> undoStack = new List<MeshUndoData>();
    private bool hasUndoData = false;

    private bool sampleLightmaps = false;

    private bool regenerateLightmapsUVs = false;

    // Mesh Read/Write scanning
    private List<string> nonReadableMeshPaths = new List<string>();
    private int totalMeshCount = 0;
    private int readableMeshCount = 0;
    private GameObject lastScannedObject = null;
    private HashSet<string> modifiedImporterPaths = new HashSet<string>(); // Track which importers we modified
    private bool updateMeshColliders = true; // Option to update MeshColliders with new mesh (default ON)
    private static System.Random meshNameRandom = new System.Random(); // For generating unique mesh names
    private HashSet<string> createdMeshPaths = new HashSet<string>(); // Track all meshes created in this session

    // UI Foldout states
    private bool showTextureSettings = true;
    private bool showPostProcessing = true;
    private bool showOutputOptions = false;
    private bool showStatistics = false;
    private bool showDebug = false;
    private Vector2 scrollPosition;

    [MenuItem("Tools/Texture to Vertex Baker")]
    public static void ShowWindow()
    {
        GetWindow<TextureToVertexBaker>("Texture to Vertex Baker");
    }

    void OnEnable()
    {
        // Subscribe to the SceneView drawing callback
        SceneView.duringSceneGui += OnSceneGUI;

        // Set minimum window size
        this.minSize = new Vector2(400, 600);

        // Auto-find VertexOneMat material if sharedMaterial is not set
        if (sharedMaterial == null)
        {
            string[] guids = AssetDatabase.FindAssets("VertexOneMat t:Material");
            if (guids.Length > 0)
            {
                string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                sharedMaterial = AssetDatabase.LoadAssetAtPath<Material>(path);
            }
        }
    }

    void OnDisable()
    {
        // Unsubscribe when the window is closed
        SceneView.duringSceneGui -= OnSceneGUI;
    }

    void OnGUI()
    {
        EditorGUIUtility.labelWidth = 180f;

        // Header
        DrawHeader();

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        // ═══════════════════════════════════════════════════════════════════
        // STEP 1: TARGET SELECTION
        // ═══════════════════════════════════════════════════════════════════
        DrawSectionHeader("1. Target Selection", "Select the object and material to process");

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        {
            targetObject = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Target Object", "The root GameObject containing meshes to process"),
                targetObject, typeof(GameObject), true);

            // Auto-scan when targetObject changes
            if (targetObject != lastScannedObject)
            {
                lastScannedObject = targetObject;
                if (targetObject != null)
                {
                    ScanMeshesForReadWrite();
                }
                else
                {
                    nonReadableMeshPaths.Clear();
                    totalMeshCount = 0;
                    readableMeshCount = 0;
                }
            }

            sharedMaterial = (Material)EditorGUILayout.ObjectField(
                new GUIContent("Output Material", "Material applied to converted meshes (auto-finds VertexOneMat)"),
                sharedMaterial, typeof(Material), false);

            texturePropertyName = EditorGUILayout.TextField(
                new GUIContent("Texture Property", "Shader property name for texture sampling"),
                texturePropertyName);
        }
        EditorGUILayout.EndVertical();

        // ═══════════════════════════════════════════════════════════════════
        // STEP 2: MESH PREPARATION
        // ═══════════════════════════════════════════════════════════════════
        if (targetObject != null && totalMeshCount > 0)
        {
            EditorGUILayout.Space(8);
            DrawSectionHeader("2. Mesh Preparation", "Ensure meshes are readable before processing");

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                int nonReadableCount = totalMeshCount - readableMeshCount;
                if (nonReadableCount > 0)
                {
                    EditorGUILayout.HelpBox(
                        $"Found {totalMeshCount} meshes: {readableMeshCount} readable, {nonReadableCount} NOT readable.\nEnable Read/Write before processing!",
                        MessageType.Warning);

                    if (GUILayout.Button(new GUIContent("Enable Read/Write on All Meshes", "Enable Read/Write on all non-readable mesh sources"), GUILayout.Height(25)))
                    {
                        EnableReadWriteOnAll();
                    }
                }
                else
                {
                    EditorGUILayout.HelpBox($"All {totalMeshCount} meshes are readable. Ready to process!", MessageType.Info);
                }
            }
            EditorGUILayout.EndVertical();
        }

        // ═══════════════════════════════════════════════════════════════════
        // STEP 3: TEXTURE SETTINGS
        // ═══════════════════════════════════════════════════════════════════
        EditorGUILayout.Space(8);
        showTextureSettings = DrawFoldoutHeader("3. Texture Sampling Settings", showTextureSettings);

        if (showTextureSettings)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                textureStrength = EditorGUILayout.Slider(
                    new GUIContent("Strength", "Influence of texture color on vertex colors"),
                    textureStrength, 0f, 1f);

                textureContrast = EditorGUILayout.Slider(
                    new GUIContent("Contrast", "Contrast adjustment for sampled colors"),
                    textureContrast, 0f, 2f);

                textureBrightness = EditorGUILayout.Slider(
                    new GUIContent("Brightness", "Brightness offset for sampled colors"),
                    textureBrightness, -1f, 1f);

                EditorGUILayout.Space(4);
                keepOldColors = EditorGUILayout.Toggle(
                    new GUIContent("Preserve Existing Colors", "Blend with existing vertex colors (e.g., AO)"),
                    keepOldColors);
            }
            EditorGUILayout.EndVertical();
        }

        // ═══════════════════════════════════════════════════════════════════
        // STEP 4: POST-PROCESSING OPTIONS
        // ═══════════════════════════════════════════════════════════════════
        EditorGUILayout.Space(8);
        showPostProcessing = DrawFoldoutHeader("4. Post-Processing Options", showPostProcessing);

        if (showPostProcessing)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                // Lightmap Sampling
                EditorGUILayout.LabelField("Lightmap", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                sampleLightmaps = EditorGUILayout.Toggle(
                    new GUIContent("Sample Lightmaps", "Multiply vertex colors with baked lightmap data"),
                    sampleLightmaps);
                if (sampleLightmaps)
                {
                    EditorGUILayout.HelpBox("Breaks prefab connection. Disable 'Apply to Prefabs' automatically.", MessageType.Warning);
                }
                regenerateLightmapsUVs = EditorGUILayout.Toggle(
                    new GUIContent("Regenerate Lightmap UVs", "Generate new UV2 channel for lightmapping"),
                    regenerateLightmapsUVs);
                EditorGUI.indentLevel--;

                DrawSeparator();

                // Color Averaging
                EditorGUILayout.LabelField("Color Averaging", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                averageColors = EditorGUILayout.Toggle(
                    new GUIContent("Average Whole Mesh", "Blend all vertex colors toward mesh average"),
                    averageColors);
                if (averageColors)
                {
                    colorAverageStrength = EditorGUILayout.Slider(
                        new GUIContent("    Strength", "How much to blend toward average"),
                        colorAverageStrength, 0f, 1f);
                }

                averageNeighborColors = EditorGUILayout.Toggle(
                    new GUIContent("Average Neighbors", "Smooth colors with nearby vertices"),
                    averageNeighborColors);
                if (averageNeighborColors)
                {
                    neighborRadius = EditorGUILayout.Slider(
                        new GUIContent("    Radius", "Search radius in world units"),
                        neighborRadius, 0.001f, 1f);
                    neighborAverageStrength = EditorGUILayout.Slider(
                        new GUIContent("    Strength", "Blending strength"),
                        neighborAverageStrength, 0f, 1f);
                }
                EditorGUI.indentLevel--;

                DrawSeparator();

                // Color Tint
                EditorGUILayout.LabelField("Color Adjustments", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                colorTintEnabled = EditorGUILayout.Toggle(
                    new GUIContent("Apply Tint", "Multiply vertex colors by a tint color"),
                    colorTintEnabled);
                if (colorTintEnabled)
                {
                    colorTint = EditorGUILayout.ColorField(
                        new GUIContent("    Tint Color", "Color to multiply with vertex colors"),
                        colorTint);
                }
                EditorGUI.indentLevel--;

                DrawSeparator();

                // Optimization
                EditorGUILayout.LabelField("Optimization", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                optimizeVertices = EditorGUILayout.Toggle(
                    new GUIContent("Optimize Vertices", "Merge vertices with similar colors"),
                    optimizeVertices);
                if (optimizeVertices)
                {
                    colorSimilarityThreshold = EditorGUILayout.Slider(
                        new GUIContent("    Similarity Threshold", "0 = identical, 1.732 = max difference"),
                        colorSimilarityThreshold, 0f, 1.732f);
                }
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndVertical();
        }

        // ═══════════════════════════════════════════════════════════════════
        // STEP 5: OUTPUT OPTIONS
        // ═══════════════════════════════════════════════════════════════════
        EditorGUILayout.Space(8);
        showOutputOptions = DrawFoldoutHeader("5. Output Options", showOutputOptions);

        if (showOutputOptions)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                // Prefab handling
                EditorGUILayout.LabelField("Prefab Handling", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button(new GUIContent("Check for Prefabs", "Scan hierarchy for prefab instances"), GUILayout.Height(22)))
                {
                    if (targetObject != null)
                        CheckForPrefabs();
                    else
                        EditorUtility.DisplayDialog("Error", "Please assign a GameObject.", "OK");
                }
                EditorGUILayout.EndHorizontal();

                EditorGUI.BeginDisabledGroup(sampleLightmaps);
                applyToPrefabs = EditorGUILayout.Toggle(
                    new GUIContent("Apply to Prefabs", "Modify original prefab assets"),
                    sampleLightmaps ? false : applyToPrefabs);
                EditorGUI.EndDisabledGroup();
                EditorGUI.indentLevel--;

                DrawSeparator();

                // Mesh options
                EditorGUILayout.LabelField("Mesh Settings", EditorStyles.miniBoldLabel);
                EditorGUI.indentLevel++;
                updateMeshColliders = EditorGUILayout.Toggle(
                    new GUIContent("Update Mesh Colliders", "Assign converted mesh to MeshCollider components"),
                    updateMeshColliders);
                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndVertical();
        }

        // ═══════════════════════════════════════════════════════════════════
        // ACTIONS
        // ═══════════════════════════════════════════════════════════════════
        EditorGUILayout.Space(12);
        DrawSectionHeader("Actions", "Execute conversion operations");

        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        {
            EditorGUI.BeginDisabledGroup(targetObject == null);

            // Main Process Button
            GUI.backgroundColor = new Color(0.4f, 0.8f, 0.4f);
            if (GUILayout.Button(new GUIContent("Process Meshes", "Sample textures and convert to vertex colors"), GUILayout.Height(32)))
            {
                ExecuteProcess();
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            {
                // Post-Process Only
                GUI.backgroundColor = new Color(0.6f, 0.7f, 0.9f);
                if (GUILayout.Button(new GUIContent("Post-Process Only", "Apply post-processing to existing vertex colors"), GUILayout.Height(26)))
                {
                    ExecutePostProcess();
                }
                GUI.backgroundColor = Color.white;

                // Undo
                EditorGUI.EndDisabledGroup(); // End the targetObject disable group
                EditorGUI.BeginDisabledGroup(!hasUndoData);
                GUI.backgroundColor = hasUndoData ? new Color(0.9f, 0.7f, 0.5f) : Color.gray;
                string undoTooltip = hasUndoData ? $"Revert last operation ({undoStack.Count} mesh(es))" : "No operations to undo";
                if (GUILayout.Button(new GUIContent("Undo", undoTooltip), GUILayout.Height(26), GUILayout.Width(60)))
                {
                    UndoLastOperation();
                }
                GUI.backgroundColor = Color.white;
                EditorGUI.EndDisabledGroup();
                EditorGUI.BeginDisabledGroup(targetObject == null); // Re-open for consistency
            }
            EditorGUILayout.EndHorizontal();

            EditorGUI.EndDisabledGroup();
        }
        EditorGUILayout.EndVertical();

        // ═══════════════════════════════════════════════════════════════════
        // FINALIZATION
        // ═══════════════════════════════════════════════════════════════════
        EditorGUILayout.Space(8);
        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
        {
            if (createdMeshPaths.Count > 0)
            {
                EditorGUILayout.HelpBox(
                    $"{createdMeshPaths.Count} mesh(es) created this session. Click 'Finalize' when done editing to disable Read/Write and save memory.",
                    MessageType.Info);
            }

            GUI.backgroundColor = new Color(0.5f, 0.8f, 0.9f);
            if (GUILayout.Button(new GUIContent("Finalize Converted Meshes", "Disable Read/Write on converted meshes to save memory"), GUILayout.Height(24)))
            {
                FinalizeConvertedMeshes();
            }
            GUI.backgroundColor = Color.white;
        }
        EditorGUILayout.EndVertical();

        // ═══════════════════════════════════════════════════════════════════
        // STATISTICS & DEBUG
        // ═══════════════════════════════════════════════════════════════════
        EditorGUILayout.Space(8);
        showStatistics = DrawFoldoutHeader("Statistics", showStatistics);

        if (showStatistics && totalVerticesBefore > 0)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                EditorGUILayout.LabelField($"Vertices Before:", totalVerticesBefore.ToString("N0"));

                if (totalVerticesAfterProcessing > 0)
                {
                    float changePercent = ((float)totalVerticesAfterProcessing / totalVerticesBefore - 1f) * 100f;
                    EditorGUILayout.LabelField($"After Processing:", $"{totalVerticesAfterProcessing:N0} ({changePercent:+0.#;-0.#;0}%)");
                }

                if (totalVerticesAfterOptimization > 0)
                {
                    float changePercent = ((float)totalVerticesAfterOptimization / totalVerticesBefore - 1f) * 100f;
                    string label = optimizeVertices ? "After Optimization:" : "After Post-Process:";
                    EditorGUILayout.LabelField(label, $"{totalVerticesAfterOptimization:N0} ({changePercent:+0.#;-0.#;0}%)");
                }
            }
            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.Space(4);
        showDebug = DrawFoldoutHeader("Debug", showDebug);

        if (showDebug)
        {
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            {
                debugMode = EditorGUILayout.Toggle(
                    new GUIContent("Enable Debug Mode", "Show detailed logs and visualizations"),
                    debugMode);
            }
            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.Space(10);
        EditorGUILayout.EndScrollView();
    }

    // ═══════════════════════════════════════════════════════════════════════
    // UI HELPER METHODS
    // ═══════════════════════════════════════════════════════════════════════

    void DrawHeader()
    {
        EditorGUILayout.Space(4);
        EditorGUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();

        GUIStyle titleStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 16,
            alignment = TextAnchor.MiddleCenter
        };
        GUILayout.Label("Texture To Vertex Baker", titleStyle);

        GUILayout.FlexibleSpace();
        EditorGUILayout.EndHorizontal();
        EditorGUILayout.Space(2);

        // Subtle separator line
        Rect rect = EditorGUILayout.GetControlRect(false, 1);
        EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.5f));
        EditorGUILayout.Space(4);
    }

    void DrawSectionHeader(string title, string tooltip = "")
    {
        GUIStyle headerStyle = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 12
        };
        EditorGUILayout.LabelField(new GUIContent(title, tooltip), headerStyle);
    }

    bool DrawFoldoutHeader(string title, bool isExpanded)
    {
        EditorGUILayout.BeginHorizontal();

        GUIStyle foldoutStyle = new GUIStyle(EditorStyles.foldout)
        {
            fontStyle = FontStyle.Bold,
            fontSize = 12
        };

        isExpanded = EditorGUILayout.Foldout(isExpanded, title, true, foldoutStyle);

        EditorGUILayout.EndHorizontal();

        return isExpanded;
    }

    void DrawSeparator()
    {
        EditorGUILayout.Space(4);
        Rect rect = EditorGUILayout.GetControlRect(false, 1);
        rect.x += 10;
        rect.width -= 20;
        EditorGUI.DrawRect(rect, new Color(0.5f, 0.5f, 0.5f, 0.3f));
        EditorGUILayout.Space(4);
    }

    void ExecuteProcess()
    {
        if (targetObject == null)
        {
            EditorUtility.DisplayDialog("Error", "Please assign a GameObject.", "OK");
            return;
        }

        // Clear previous undo data for new operation
        undoStack.Clear();
        hasUndoData = false;

        debugSamplingPoints.Clear();
        processedPrefabAssets.Clear();
        debugStartTime = EditorApplication.timeSinceStartup;

        totalVerticesBefore = 0;
        totalVerticesAfterProcessing = 0;
        totalVerticesAfterOptimization = 0;

        ProcessMeshesInHierarchy(targetObject);
        SceneView.RepaintAll();
    }

    void ExecutePostProcess()
    {
        if (targetObject == null)
        {
            EditorUtility.DisplayDialog("Error", "Please assign a GameObject.", "OK");
            return;
        }

        // Clear previous undo data for new operation
        undoStack.Clear();
        hasUndoData = false;

        debugSamplingPoints.Clear();
        processedPrefabAssets.Clear();
        debugStartTime = EditorApplication.timeSinceStartup;

        totalVerticesBefore = 0;
        totalVerticesAfterProcessing = 0;
        totalVerticesAfterOptimization = 0;

        PostProcessMeshesInHierarchy(targetObject);
        SceneView.RepaintAll();
    }

    void OnSceneGUI(SceneView sceneView)
    {
        if (debugMode && debugSamplingPoints.Count > 0)
        {
            // Keep the debug spheres visible for 5 seconds
            if (EditorApplication.timeSinceStartup - debugStartTime < 5.0)
            {
                Handles.color = Color.red;
                foreach (Vector3 point in debugSamplingPoints)
                {
                    Handles.DrawWireDisc(point, sceneView.camera.transform.forward, 0.02f);
                }
            }
            else
            {
                // Clear debug points after 5 seconds
                debugSamplingPoints.Clear();
                SceneView.RepaintAll();
            }
        }
    }

    void CheckForPrefabs()
    {
        HashSet<string> prefabAssetPaths = new HashSet<string>();

        MeshFilter[] meshFilters = targetObject.GetComponentsInChildren<MeshFilter>(true);

        foreach (MeshFilter mf in meshFilters)
        {
            GameObject go = mf.gameObject;

            PrefabInstanceStatus prefabInstanceStatus = PrefabUtility.GetPrefabInstanceStatus(go);

            if (prefabInstanceStatus != PrefabInstanceStatus.NotAPrefab)
            {
                GameObject prefabAsset = PrefabUtility.GetCorrespondingObjectFromSource(go);

                string assetPath = AssetDatabase.GetAssetPath(prefabAsset);

                prefabAssetPaths.Add(assetPath);
            }
        }

        if (prefabAssetPaths.Count == 0)
        {
            EditorUtility.DisplayDialog("Info", "No prefabs found in the selected GameObject hierarchy.", "OK");
        }
        else
        {
            string message = "Prefabs found in hierarchy:\n";

            foreach (string path in prefabAssetPaths)
            {
                message += path + "\n";
            }

            EditorUtility.DisplayDialog("Prefabs Found", message, "OK");
        }
    }

    void ScanMeshesForReadWrite()
    {
        nonReadableMeshPaths.Clear();
        totalMeshCount = 0;
        readableMeshCount = 0;

        if (targetObject == null)
            return;

        MeshFilter[] meshFilters = targetObject.GetComponentsInChildren<MeshFilter>(true);
        HashSet<Mesh> processedMeshes = new HashSet<Mesh>();
        HashSet<string> nonReadablePathsSet = new HashSet<string>();

        foreach (MeshFilter mf in meshFilters)
        {
            Mesh mesh = mf.sharedMesh;
            if (mesh == null || processedMeshes.Contains(mesh))
                continue;

            processedMeshes.Add(mesh);
            totalMeshCount++;

            string assetPath = AssetDatabase.GetAssetPath(mesh);

            if (IsMeshReadable(mesh))
            {
                readableMeshCount++;
            }
            else
            {
                // Get the model importer path (could be different from mesh asset path for sub-assets)
                string importerPath = GetMeshImporterPath(mesh);
                if (!string.IsNullOrEmpty(importerPath))
                {
                    // This is a model file (FBX, OBJ, etc.) - we can fix it via ModelImporter
                    if (!nonReadablePathsSet.Contains(importerPath))
                    {
                        nonReadablePathsSet.Add(importerPath);
                        nonReadableMeshPaths.Add(importerPath);
                    }
                }
                else if (!string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".asset"))
                {
                    // This is a .asset mesh - we can fix it via SerializedObject
                    if (!nonReadablePathsSet.Contains(assetPath))
                    {
                        nonReadablePathsSet.Add(assetPath);
                        nonReadableMeshPaths.Add(assetPath + " (.asset)");
                    }
                }
                else if (!string.IsNullOrEmpty(assetPath))
                {
                    // Other asset type we can't fix
                    if (!nonReadablePathsSet.Contains(assetPath))
                    {
                        nonReadablePathsSet.Add(assetPath);
                        nonReadableMeshPaths.Add(assetPath + " (cannot auto-fix)");
                    }
                }
                else
                {
                    // Runtime/procedural mesh that isn't readable
                    string meshDesc = $"[Runtime mesh: {mesh.name}] (cannot auto-fix)";
                    if (!nonReadablePathsSet.Contains(meshDesc))
                    {
                        nonReadablePathsSet.Add(meshDesc);
                        nonReadableMeshPaths.Add(meshDesc);
                    }
                }
            }
        }

        // Show results in console
        Debug.Log($"[Texture to Vertex Baker] Mesh Scan Results: {totalMeshCount} meshes total, {readableMeshCount} readable, {totalMeshCount - readableMeshCount} not readable.");
        if (nonReadableMeshPaths.Count > 0)
        {
            Debug.LogWarning($"[Texture to Vertex Baker] Non-readable mesh sources:\n- " + string.Join("\n- ", nonReadableMeshPaths));
        }

        // Show popup dialog
        if (nonReadableMeshPaths.Count > 0)
        {
            string message = $"Found {totalMeshCount} meshes total.\n";
            message += $"{readableMeshCount} meshes are readable.\n";
            message += $"{totalMeshCount - readableMeshCount} meshes have Read/Write DISABLED:\n\n";

            foreach (string path in nonReadableMeshPaths)
            {
                message += $"- {path}\n";
            }

            message += "\nClick 'Enable Read/Write on All' button to fix before processing.";

            EditorUtility.DisplayDialog("Mesh Read/Write Scan Results", message, "OK");
        }
        else if (totalMeshCount > 0)
        {
            EditorUtility.DisplayDialog("Mesh Read/Write Scan Results",
                $"All {totalMeshCount} meshes are readable. Ready to process!", "OK");
        }
    }

    bool IsMeshReadable(Mesh mesh)
    {
        if (mesh == null)
            return false;

        // Use the isReadable property directly - this is the reliable way
        return mesh.isReadable;
    }

    string GetMeshImporterPath(Mesh mesh)
    {
        string assetPath = AssetDatabase.GetAssetPath(mesh);
        if (string.IsNullOrEmpty(assetPath))
            return null;

        // Check if this is a model file (FBX, OBJ, etc.)
        AssetImporter importer = AssetImporter.GetAtPath(assetPath);
        if (importer is ModelImporter)
        {
            return assetPath;
        }

        return null;
    }

    void EnableReadWriteOnAll()
    {
        if (nonReadableMeshPaths.Count == 0)
        {
            EditorUtility.DisplayDialog("Info", "No meshes need Read/Write enabled.", "OK");
            return;
        }

        // Categorize paths by type
        List<string> modelPaths = new List<string>();
        List<string> assetPaths = new List<string>();
        List<string> unfixablePaths = new List<string>();

        foreach (string path in nonReadableMeshPaths)
        {
            if (path.Contains("(cannot auto-fix)"))
            {
                unfixablePaths.Add(path);
            }
            else if (path.EndsWith(" (.asset)"))
            {
                // Extract actual path by removing the suffix
                assetPaths.Add(path.Replace(" (.asset)", ""));
            }
            else
            {
                modelPaths.Add(path);
            }
        }

        if (modelPaths.Count == 0 && assetPaths.Count == 0)
        {
            EditorUtility.DisplayDialog("Cannot Auto-Fix",
                "All non-readable meshes are runtime meshes that cannot be auto-fixed.",
                "OK");
            return;
        }

        modifiedImporterPaths.Clear();
        int modelSuccessCount = 0;
        int assetSuccessCount = 0;
        int failCount = 0;

        EditorUtility.DisplayProgressBar("Enabling Read/Write", "Processing...", 0f);

        try
        {
            int totalCount = modelPaths.Count + assetPaths.Count;
            int currentIndex = 0;

            // Process model files via ModelImporter
            foreach (string path in modelPaths)
            {
                EditorUtility.DisplayProgressBar("Enabling Read/Write", $"Processing {Path.GetFileName(path)}", (float)currentIndex / totalCount);
                currentIndex++;

                ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer != null)
                {
                    importer.isReadable = true;
                    importer.SaveAndReimport();
                    modifiedImporterPaths.Add(path);
                    modelSuccessCount++;
                    Debug.Log($"[Texture to Vertex Baker] Enabled Read/Write on model: {path}");
                }
                else
                {
                    failCount++;
                    Debug.LogWarning($"[Texture to Vertex Baker] Could not get ModelImporter for: {path}");
                }
            }

            // Process .asset files via SerializedObject
            foreach (string path in assetPaths)
            {
                EditorUtility.DisplayProgressBar("Enabling Read/Write", $"Processing {Path.GetFileName(path)}", (float)currentIndex / totalCount);
                currentIndex++;

                if (EnableReadWriteOnMeshAsset(path))
                {
                    assetSuccessCount++;
                    Debug.Log($"[Texture to Vertex Baker] Enabled Read/Write on .asset: {path}");
                }
                else
                {
                    failCount++;
                    Debug.LogWarning($"[Texture to Vertex Baker] Could not enable Read/Write on: {path}");
                }
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        // Re-scan to update the UI
        ScanMeshesForReadWrite();

        string resultMessage = "";
        if (modelSuccessCount > 0)
            resultMessage += $"Enabled Read/Write on {modelSuccessCount} model file(s).\n";
        if (assetSuccessCount > 0)
            resultMessage += $"Enabled Read/Write on {assetSuccessCount} .asset mesh(es).\n";
        if (failCount > 0)
            resultMessage += $"\n{failCount} mesh(es) could not be modified.";
        if (unfixablePaths.Count > 0)
            resultMessage += $"\n\n{unfixablePaths.Count} mesh(es) cannot be auto-fixed (runtime meshes).";

        EditorUtility.DisplayDialog("Enable Read/Write Complete", resultMessage.Trim(), "OK");
    }

    bool EnableReadWriteOnMeshAsset(string assetPath)
    {
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        if (mesh == null)
            return false;

        SerializedObject serializedMesh = new SerializedObject(mesh);
        SerializedProperty isReadableProp = serializedMesh.FindProperty("m_IsReadable");
        if (isReadableProp != null)
        {
            isReadableProp.boolValue = true;
            serializedMesh.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();
            return true;
        }
        return false;
    }

    void DisableReadWriteOnModifiedImporters()
    {
        if (modifiedImporterPaths.Count == 0)
            return;

        EditorUtility.DisplayProgressBar("Restoring Read/Write Settings", "Processing...", 0f);

        try
        {
            int i = 0;
            foreach (string path in modifiedImporterPaths)
            {
                EditorUtility.DisplayProgressBar("Restoring Read/Write Settings", $"Processing {Path.GetFileName(path)}", (float)i / modifiedImporterPaths.Count);

                ModelImporter importer = AssetImporter.GetAtPath(path) as ModelImporter;
                if (importer != null)
                {
                    importer.isReadable = false;
                    importer.SaveAndReimport();
                    Debug.Log($"[Texture to Vertex Baker] Disabled Read/Write on: {path}");
                }
                i++;
            }
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        modifiedImporterPaths.Clear();
    }

    void ProcessMeshesInHierarchy(GameObject rootObject)
    {
        // Check for non-readable meshes
        int nonReadableCount = totalMeshCount - readableMeshCount;
        if (nonReadableCount > 0)
        {
            bool proceed = EditorUtility.DisplayDialog("Warning: Non-Readable Meshes",
                $"{nonReadableCount} mesh(es) have Read/Write disabled.\n\nProcessing may fail or skip these meshes.\n\nDo you want to continue anyway?",
                "Continue", "Cancel");
            if (!proceed)
                return;
        }

        HashSet<GameObject> prefabsToProcess = new HashSet<GameObject>();
        List<MeshFilter> nonPrefabMeshFilters = new List<MeshFilter>();

        MeshFilter[] meshFilters = rootObject.GetComponentsInChildren<MeshFilter>(true);

        foreach (MeshFilter mf in meshFilters)
        {
            GameObject go = mf.gameObject;

            if (go.activeInHierarchy && mf.GetComponent<MeshRenderer>() != null)
            {
                PrefabInstanceStatus prefabInstanceStatus = PrefabUtility.GetPrefabInstanceStatus(go);

                if (prefabInstanceStatus != PrefabInstanceStatus.NotAPrefab)
                {
                    GameObject prefabAsset = PrefabUtility.GetCorrespondingObjectFromSource(go);

                    if (applyToPrefabs)
                    {
                        prefabsToProcess.Add(prefabAsset);
                    }
                    else
                    {
                        // Process the instance as before
                        nonPrefabMeshFilters.Add(mf);
                    }
                }
                else
                {
                    // Not a prefab instance, process as before
                    nonPrefabMeshFilters.Add(mf);
                }
            }
        }

        try
        {
            // Process prefab assets
            foreach (GameObject prefabAsset in prefabsToProcess)
            {
                ProcessPrefabAsset(prefabAsset);
            }

            // Process non-prefab MeshFilters
            foreach (MeshFilter mf in nonPrefabMeshFilters)
            {
                ProcessMeshFilter(mf, "Assets/ConvertedMeshes");
            }

            EditorUtility.DisplayDialog("Operation Complete", $"Successfully processed meshes.\n\n{createdMeshPaths.Count} mesh(es) created. Click 'Finalize' when done editing.", "OK");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[Texture to Vertex Baker] Error during processing: {ex.Message}\n{ex.StackTrace}");
            EditorUtility.DisplayDialog("Processing Error", $"An error occurred during processing:\n\n{ex.Message}\n\nCheck console for details.", "OK");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    void PostProcessMeshesInHierarchy(GameObject rootObject)
    {
        // Check for non-readable meshes
        int nonReadableCount = totalMeshCount - readableMeshCount;
        if (nonReadableCount > 0)
        {
            bool proceed = EditorUtility.DisplayDialog("Warning: Non-Readable Meshes",
                $"{nonReadableCount} mesh(es) have Read/Write disabled.\n\nProcessing may fail or skip these meshes.\n\nDo you want to continue anyway?",
                "Continue", "Cancel");
            if (!proceed)
                return;
        }

        HashSet<GameObject> prefabsToProcess = new HashSet<GameObject>();
        List<MeshFilter> nonPrefabMeshFilters = new List<MeshFilter>();

        MeshFilter[] meshFilters = rootObject.GetComponentsInChildren<MeshFilter>(true);

        foreach (MeshFilter mf in meshFilters)
        {
            GameObject go = mf.gameObject;

            if (go.activeInHierarchy && mf.GetComponent<MeshRenderer>() != null)
            {
                PrefabInstanceStatus prefabInstanceStatus = PrefabUtility.GetPrefabInstanceStatus(go);

                if (prefabInstanceStatus != PrefabInstanceStatus.NotAPrefab)
                {
                    GameObject prefabAsset = PrefabUtility.GetCorrespondingObjectFromSource(go);

                    if (applyToPrefabs)
                    {
                        prefabsToProcess.Add(prefabAsset);
                    }
                    else
                    {
                        // Process the instance as before
                        nonPrefabMeshFilters.Add(mf);
                    }
                }
                else
                {
                    // Not a prefab instance, process as before
                    nonPrefabMeshFilters.Add(mf);
                }
            }
        }

        try
        {
            // Process prefab assets
            foreach (GameObject prefabAsset in prefabsToProcess)
            {
                PostProcessPrefabAsset(prefabAsset);
            }

            // Process non-prefab MeshFilters
            foreach (MeshFilter mf in nonPrefabMeshFilters)
            {
                PostProcessMeshFilter(mf, "Assets/ConvertedMeshes");
            }

            EditorUtility.DisplayDialog("Operation Complete", $"Successfully post-processed meshes.\n\n{createdMeshPaths.Count} mesh(es) created. Click 'Finalize' when done editing.", "OK");
        }
        catch (System.Exception ex)
        {
            Debug.LogError($"[Texture to Vertex Baker] Error during post-processing: {ex.Message}\n{ex.StackTrace}");
            EditorUtility.DisplayDialog("Processing Error", $"An error occurred during post-processing:\n\n{ex.Message}\n\nCheck console for details.", "OK");
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }
    }

    void ProcessPrefabAsset(GameObject prefabAsset)
    {
        string assetPath = AssetDatabase.GetAssetPath(prefabAsset);

        if (processedPrefabAssets.Contains(assetPath))
            return;

        processedPrefabAssets.Add(assetPath);

        GameObject prefabContents = PrefabUtility.LoadPrefabContents(assetPath);

        try
        {
            // Register Undo for the prefab contents
            Undo.RegisterFullObjectHierarchyUndo(prefabContents, "Modify Prefab");

            // Determine the folder to save meshes
            string prefabFolderPath = Path.GetDirectoryName(assetPath);

            // Process the prefab contents
            MeshFilter[] meshFilters = prefabContents.GetComponentsInChildren<MeshFilter>(true);

            foreach (MeshFilter mf in meshFilters)
            {
                if (PrefabUtility.IsAnyPrefabInstanceRoot(mf.gameObject))
                {
                    // This is a nested prefab instance root
                    GameObject nestedPrefabAsset = PrefabUtility.GetCorrespondingObjectFromSource(mf.gameObject);
                    ProcessPrefabAsset(nestedPrefabAsset);
                }
                else
                {
                    // Process the MeshFilter
                    ProcessMeshFilter(mf, prefabFolderPath + "/ConvertedMeshes");
                }
            }

            PrefabUtility.SaveAsPrefabAsset(prefabContents, assetPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(prefabContents);
        }
    }

    void PostProcessPrefabAsset(GameObject prefabAsset)
    {
        string assetPath = AssetDatabase.GetAssetPath(prefabAsset);

        if (processedPrefabAssets.Contains(assetPath))
            return;

        processedPrefabAssets.Add(assetPath);

        GameObject prefabContents = PrefabUtility.LoadPrefabContents(assetPath);

        try
        {
            // Register Undo for the prefab contents
            Undo.RegisterFullObjectHierarchyUndo(prefabContents, "Modify Prefab");

            // Determine the folder to save meshes
            string prefabFolderPath = Path.GetDirectoryName(assetPath);

            // Process the prefab contents
            MeshFilter[] meshFilters = prefabContents.GetComponentsInChildren<MeshFilter>(true);

            foreach (MeshFilter mf in meshFilters)
            {
                if (PrefabUtility.IsAnyPrefabInstanceRoot(mf.gameObject))
                {
                    // This is a nested prefab instance root
                    GameObject nestedPrefabAsset = PrefabUtility.GetCorrespondingObjectFromSource(mf.gameObject);
                    PostProcessPrefabAsset(nestedPrefabAsset);
                }
                else
                {
                    // Process the MeshFilter
                    PostProcessMeshFilter(mf, prefabFolderPath + "/ConvertedMeshes");
                }
            }

            PrefabUtility.SaveAsPrefabAsset(prefabContents, assetPath);
        }
        finally
        {
            PrefabUtility.UnloadPrefabContents(prefabContents);
        }
    }

    void UndoLastOperation()
    {
        if (!hasUndoData || undoStack.Count == 0)
        {
            EditorUtility.DisplayDialog("Undo", "Nothing to undo. No tool operations have been performed.", "OK");
            return;
        }

        int restoredCount = 0;
        List<string> meshesToDelete = new List<string>();

        // Restore all mesh filters to their original state
        foreach (MeshUndoData undoData in undoStack)
        {
            if (undoData.meshFilter != null && undoData.originalMesh != null)
            {
                undoData.meshFilter.sharedMesh = undoData.originalMesh;
                EditorUtility.SetDirty(undoData.meshFilter);
                restoredCount++;
            }

            // Restore original materials if we have them
            if (undoData.meshFilter != null && undoData.originalMaterials != null)
            {
                Renderer renderer = undoData.meshFilter.GetComponent<Renderer>();
                if (renderer != null)
                {
                    renderer.sharedMaterials = undoData.originalMaterials;
                    EditorUtility.SetDirty(renderer);
                }
            }

            // Restore mesh collider
            if (undoData.meshCollider != null)
            {
                undoData.meshCollider.sharedMesh = undoData.originalColliderMesh;
                EditorUtility.SetDirty(undoData.meshCollider);
            }

            // Collect mesh paths to delete
            if (!string.IsNullOrEmpty(undoData.createdMeshPath))
            {
                meshesToDelete.Add(undoData.createdMeshPath);
            }
        }

        // Clear undo stack
        undoStack.Clear();
        hasUndoData = false;

        // Delete created mesh assets
        foreach (string meshPath in meshesToDelete)
        {
            if (File.Exists(meshPath))
            {
                AssetDatabase.DeleteAsset(meshPath);
                createdMeshPaths.Remove(meshPath);
            }
        }

        AssetDatabase.Refresh();

        // Reset statistics
        totalVerticesBefore = 0;
        totalVerticesAfterProcessing = 0;
        totalVerticesAfterOptimization = 0;

        EditorUtility.DisplayDialog("Undo Complete", $"Restored {restoredCount} mesh(es) to original state.\nDeleted {meshesToDelete.Count} created mesh asset(s).", "OK");
    }

    void ProcessMeshFilter(MeshFilter mf, string folderPath)
    {
        Mesh originalMesh = mf.sharedMesh;

        if (originalMesh == null)
        {
            if (debugMode)
                Debug.LogWarning($"MeshFilter on GameObject '{mf.gameObject.name}' does not have a mesh assigned.");
            return;
        }

        // Check if mesh is readable - if not, we can't process it
        if (!originalMesh.isReadable)
        {
            Debug.LogWarning($"[Texture to Vertex Baker] Skipping mesh '{originalMesh.name}' on '{mf.gameObject.name}' - Read/Write is disabled. Enable Read/Write on the source mesh first.");
            return;
        }

        Renderer renderer = mf.GetComponent<Renderer>();
        MeshCollider meshCollider = mf.GetComponent<MeshCollider>();

        // Record custom undo data BEFORE making any changes
        MeshUndoData undoData = new MeshUndoData
        {
            meshFilter = mf,
            originalMesh = originalMesh,
            originalMaterials = renderer != null ? renderer.sharedMaterials.Clone() as Material[] : null,
            meshCollider = meshCollider,
            originalColliderMesh = meshCollider != null ? meshCollider.sharedMesh : null,
            createdMeshPath = null // Will be set after mesh is saved
        };
        undoStack.Add(undoData);

        // Create a copy of the original mesh
        Mesh modifiedMesh = Instantiate(originalMesh);
        modifiedMesh.name = originalMesh.name + "_Modified";

        // Get the materials assigned to the mesh
        Material[] originalMaterials = renderer.sharedMaterials;

        // Process the mesh (handling submeshes)
        if (!ProcessMesh(modifiedMesh, mf.gameObject.name, originalMaterials, mf.transform))
        {
            return;
        }

        // Assign the shared material if set; otherwise, use the first material or create a new one
        Material newMaterial;
        if (sharedMaterial != null)
        {
            newMaterial = sharedMaterial;
        }
        else if (originalMaterials.Length > 0 && originalMaterials[0] != null)
        {
            newMaterial = new Material(originalMaterials[0]);
            newMaterial.name = originalMaterials[0].name + "_VertexColors";
        }
        else
        {
            // Create a new material with a vertex color shader
            Shader vertexColorShader = Shader.Find("Legacy Shaders/VertexLit");
            if (vertexColorShader != null)
            {
                newMaterial = new Material(vertexColorShader);
                newMaterial.name = "VertexColorMaterial";
            }
            else
            {
                if (debugMode)
                    Debug.LogWarning("Vertex Color Shader not found. Using default material.");
                newMaterial = new Material(Shader.Find("Standard"));
            }
        }

        // Set the renderer to use only one material
        renderer.sharedMaterials = new Material[] { newMaterial };

        // Ensure the mesh has only one submesh
        if (modifiedMesh.subMeshCount > 1)
        {
            int[] allTriangles = modifiedMesh.triangles;
            modifiedMesh.Clear();
            modifiedMesh.vertices = originalMesh.vertices;
            modifiedMesh.uv = originalMesh.uv;
            modifiedMesh.colors = modifiedMesh.colors; // Preserve vertex colors
            modifiedMesh.SetTriangles(allTriangles, 0);
            modifiedMesh.RecalculateNormals();
            modifiedMesh.RecalculateBounds();
        }

        // Generate lightmap UVs
        UnwrapParam unwrapParams = new UnwrapParam();
        UnwrapParam.SetDefaults(out unwrapParams);
        Unwrapping.GenerateSecondaryUVSet(modifiedMesh, unwrapParams);

        // Save the modified mesh
        string meshAssetPath = SaveMesh(modifiedMesh, folderPath, mf.gameObject.name);

        // Record created mesh path for undo
        if (undoStack.Count > 0)
        {
            undoStack[undoStack.Count - 1].createdMeshPath = meshAssetPath;
        }
        hasUndoData = true;

        // Load the saved mesh asset and assign it to the MeshFilter
        Mesh savedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath);
        mf.sharedMesh = savedMesh;

        // Update MeshCollider if option is enabled
        if (updateMeshColliders && meshCollider != null)
        {
            meshCollider.sharedMesh = savedMesh;
            EditorUtility.SetDirty(meshCollider);
            if (debugMode)
                Debug.Log($"[Texture to Vertex Baker] Updated MeshCollider on '{mf.gameObject.name}'");
        }

        EditorUtility.SetDirty(mf);
        EditorUtility.SetDirty(renderer);

        if (debugMode)
            Debug.Log($"Processed mesh '{savedMesh.name}', assigned single material '{newMaterial.name}', and generated lightmap UVs. Submesh count: {savedMesh.subMeshCount}");
    }

    void PostProcessMeshFilter(MeshFilter mf, string folderPath)
    {
        Mesh originalMesh = mf.sharedMesh;

        if (originalMesh == null)
        {
            if (debugMode)
                Debug.LogWarning($"MeshFilter on GameObject '{mf.gameObject.name}' does not have a mesh assigned.");
            return;
        }

        // Check if mesh is readable - if not, we can't process it
        if (!originalMesh.isReadable)
        {
            Debug.LogWarning($"[Texture to Vertex Baker] Skipping mesh '{originalMesh.name}' on '{mf.gameObject.name}' - Read/Write is disabled. Enable Read/Write on the source mesh first.");
            return;
        }

        Renderer renderer = mf.GetComponent<Renderer>();
        MeshCollider meshCollider = mf.GetComponent<MeshCollider>();

        // Record custom undo data BEFORE making any changes
        MeshUndoData undoData = new MeshUndoData
        {
            meshFilter = mf,
            originalMesh = originalMesh,
            originalMaterials = renderer != null ? renderer.sharedMaterials.Clone() as Material[] : null,
            meshCollider = meshCollider,
            originalColliderMesh = meshCollider != null ? meshCollider.sharedMesh : null,
            createdMeshPath = null
        };
        undoStack.Add(undoData);

        // Create a copy of the original mesh
        Mesh modifiedMesh = Instantiate(originalMesh);
        modifiedMesh.name = originalMesh.name + "_Modified";

        // Post-process the mesh
        if (!PostProcessMesh(modifiedMesh, mf.gameObject.name, renderer))
        {
            // Remove the undo data if processing failed
            undoStack.RemoveAt(undoStack.Count - 1);
            return;
        }

        // Save the modified mesh
        string meshAssetPath = SaveMesh(modifiedMesh, folderPath, mf.gameObject.name);

        // Record created mesh path for undo
        undoStack[undoStack.Count - 1].createdMeshPath = meshAssetPath;
        hasUndoData = true;

        // Load the saved mesh asset and assign it to the MeshFilter
        Mesh savedMesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshAssetPath);
        mf.sharedMesh = savedMesh;

        // Update MeshCollider if option is enabled
        if (updateMeshColliders && meshCollider != null)
        {
            meshCollider.sharedMesh = savedMesh;
            EditorUtility.SetDirty(meshCollider);
            if (debugMode)
                Debug.Log($"[Texture to Vertex Baker] Updated MeshCollider on '{mf.gameObject.name}'");
        }

        EditorUtility.SetDirty(mf);
        EditorUtility.SetDirty(renderer);

        if (debugMode)
            Debug.Log($"Post-processed mesh '{savedMesh.name}'.");
    }

    bool PostProcessMesh(Mesh mesh, string meshName, Renderer renderer)
    {
        if (sampleLightmaps)
        {
            SampleLightmapsAndMultiplyVertexColors(mesh, renderer, meshName);
        }
        List<Color> colors = new List<Color>();
        mesh.GetColors(colors);

        if (colors.Count == 0)
        {
            if (debugMode)
                Debug.LogWarning($"Mesh '{meshName}' does not have vertex colors. Cannot perform post-processing.");
            return false;
        }

        totalVerticesBefore += mesh.vertexCount;



        if (averageColors)
        {
            Color totalColor = Color.black;
            foreach (Color c in colors)
            {
                totalColor += c;
            }
            Color averageColor = totalColor / colors.Count;

            // Apply the average color to each vertex color, based on the strength
            for (int i = 0; i < colors.Count; i++)
            {
                colors[i] = Color.Lerp(colors[i], averageColor, colorAverageStrength);
            }

            if (debugMode)
                Debug.Log($"Applied average color to mesh '{meshName}'.");
        }

        if (averageNeighborColors)
        {
            AverageNeighborVertexColors(mesh, colors, neighborRadius, neighborAverageStrength);

            if (debugMode)
                Debug.Log($"Applied neighbor color averaging to mesh '{meshName}'.");
        }

        if (colorTintEnabled)
        {
            for (int i = 0; i < colors.Count; i++)
            {
                colors[i] *= colorTint;
            }

            if (debugMode)
                Debug.Log($"Applied color tint to mesh '{meshName}'.");
        }

        mesh.SetColors(colors);

        if (optimizeVertices)
        {
            OptimizeMeshVertices(mesh, colors, colorSimilarityThreshold);

            if (debugMode)
                Debug.Log($"Optimized vertices for mesh '{meshName}'.");
        }
        if (regenerateLightmapsUVs)
        {
            UnwrapParam unwrapParams = new UnwrapParam();
            UnwrapParam.SetDefaults(out unwrapParams);
            Unwrapping.GenerateSecondaryUVSet(mesh, unwrapParams);
        }

        // Update statistics
        totalVerticesAfterOptimization += mesh.vertexCount;

        return true;
    }

    void AverageNeighborVertexColors(Mesh mesh, List<Color> colors, float radius, float strength)
    {
        Vector3[] vertices = mesh.vertices;
        Color[] originalColors = colors.ToArray(); // Keep a copy of original colors
        int vertexCount = vertices.Length;

        // For performance, consider using spatial partitioning structures
        for (int i = 0; i < vertexCount; i++)
        {
            Vector3 vi = vertices[i];
            Color ci = originalColors[i];
            Color accumulatedColor = ci;
            int neighborCount = 1;

            for (int j = 0; j < vertexCount; j++)
            {
                if (i == j) continue;

                Vector3 vj = vertices[j];
                float distance = Vector3.Distance(vi, vj);
                if (distance <= radius)
                {
                    accumulatedColor += originalColors[j];
                    neighborCount++;
                }
            }

            Color averageNeighborColor = accumulatedColor / neighborCount;

            // Blend the original color towards the average neighbor color
            colors[i] = Color.Lerp(ci, averageNeighborColor, strength);
        }
    }

    void OptimizeMeshVertices(Mesh mesh, List<Color> colors, float colorSimilarityThreshold)
    {
        Vector3[] vertices = mesh.vertices;
        Vector2[] uvs = mesh.uv; // Retrieve UVs
        int[] triangles = mesh.triangles;
        List<Vector3> newVertices = new List<Vector3>();
        List<Color> newColors = new List<Color>();
        List<Vector2> newUVs = new List<Vector2>(); // List to store new UVs
        List<int> newTriangles = new List<int>();

        Dictionary<int, int> oldToNewIndexMap = new Dictionary<int, int>();
        Dictionary<Vector3, List<int>> positionToIndices = new Dictionary<Vector3, List<int>>();

        float positionPrecision = 0.0001f; // Adjust as needed

        // Build a mapping from quantized positions to indices
        for (int i = 0; i < vertices.Length; i++)
        {
            Vector3 quantizedPos = QuantizePosition(vertices[i], positionPrecision);
            if (!positionToIndices.ContainsKey(quantizedPos))
            {
                positionToIndices[quantizedPos] = new List<int>();
            }
            positionToIndices[quantizedPos].Add(i);
        }

        // For each group of vertices at the same position, merge vertices with similar colors
        foreach (var kvp in positionToIndices)
        {
            List<int> indices = kvp.Value;

            // Group indices by similar colors
            List<List<int>> colorGroups = new List<List<int>>();

            foreach (int idx in indices)
            {
                Color color = colors[idx];
                bool added = false;
                foreach (List<int> group in colorGroups)
                {
                    Color groupColor = colors[group[0]];
                    if (IsColorSimilar(color, groupColor, colorSimilarityThreshold))
                    {
                        group.Add(idx);
                        added = true;
                        break;
                    }
                }
                if (!added)
                {
                    colorGroups.Add(new List<int> { idx });
                }
            }

            // For each color group, create a new vertex
            foreach (List<int> group in colorGroups)
            {
                int newIndex = newVertices.Count;
                newVertices.Add(vertices[group[0]]);
                newColors.Add(colors[group[0]]);

                // Average UVs for the new vertex
                Vector2 accumulatedUV = Vector2.zero;
                foreach (int oldIndex in group)
                {
                    accumulatedUV += uvs[oldIndex];
                }
                Vector2 averageUV = accumulatedUV / group.Count;
                newUVs.Add(averageUV);

                foreach (int oldIndex in group)
                {
                    oldToNewIndexMap[oldIndex] = newIndex;
                }
            }
        }

        // Rebuild triangles with new indices
        for (int i = 0; i < triangles.Length; i++)
        {
            int oldIndex = triangles[i];
            if (oldToNewIndexMap.TryGetValue(oldIndex, out int newIndex))
            {
                newTriangles.Add(newIndex);
            }
            else
            {
                // This should not happen, but just in case
                newTriangles.Add(oldIndex);
            }
        }

        // Update the mesh with new data
        mesh.Clear();
        mesh.SetVertices(newVertices);
        mesh.SetTriangles(newTriangles, 0);
        mesh.SetColors(newColors);
        mesh.SetUVs(0, newUVs); // Assign the new UVs

        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        mesh.Optimize();

        // Update statistics
        totalVerticesAfterOptimization += mesh.vertexCount;

        if (debugMode)
            Debug.Log($"Optimized mesh '{mesh.name}'. Vertices before: {vertices.Length}, after: {newVertices.Count}");
    }



    Vector3 QuantizePosition(Vector3 pos, float precision)
    {
        float x = Mathf.Round(pos.x / precision) * precision;
        float y = Mathf.Round(pos.y / precision) * precision;
        float z = Mathf.Round(pos.z / precision) * precision;
        return new Vector3(x, y, z);
    }

    bool IsColorSimilar(Color a, Color b, float threshold)
    {
        // Convert colors to HSV
        Color.RGBToHSV(a, out float h1, out float s1, out float v1);
        Color.RGBToHSV(b, out float h2, out float s2, out float v2);

        // Compute differences, considering hue wrap-around
        float dh = Mathf.Min(Mathf.Abs(h1 - h2), 1f - Mathf.Abs(h1 - h2)) * 360f; // Hue difference in degrees
        float ds = Mathf.Abs(s1 - s2);
        float dv = Mathf.Abs(v1 - v2);

        dh /= 360f; // Normalize hue difference to [0,1]

        // Compute combined difference
        float diff = Mathf.Sqrt(dh * dh + ds * ds + dv * dv);

        return diff <= threshold;
    }

    bool ProcessMesh(Mesh mesh, string meshName, Material[] materials, Transform meshTransform)
    {
        // Update statistics
        totalVerticesBefore += mesh.vertexCount;

        // Check if the mesh has submeshes
        if (mesh.subMeshCount > 1)
        {
            if (debugMode)
                Debug.Log($"Mesh '{meshName}' has {mesh.subMeshCount} submeshes. Processing each submesh separately.");

            List<Mesh> processedSubMeshes = new List<Mesh>();

            for (int i = 0; i < mesh.subMeshCount; i++)
            {
                Mesh subMesh = ExtractSubMesh(mesh, i);

                Material material = (materials != null && i < materials.Length) ? materials[i] : null;

                // Process the submesh
                if (ProcessMesh(subMesh, meshName + "_SubMesh" + i, new Material[] { material }, meshTransform))
                {
                    processedSubMeshes.Add(subMesh);
                }
            }

            // Combine processed submeshes into one mesh
            Mesh combinedMesh = CombineMeshes(processedSubMeshes, meshTransform);

            if (combinedMesh != null)
            {
                // Replace the original mesh with the combined mesh
                mesh.Clear();
                mesh.SetVertices(combinedMesh.vertices);
                mesh.SetTriangles(combinedMesh.triangles, 0);
                mesh.SetColors(combinedMesh.colors);
                mesh.RecalculateNormals();
                mesh.RecalculateTangents();
                mesh.RecalculateBounds();
                mesh.Optimize();

                // Update statistics
                totalVerticesAfterProcessing += mesh.vertexCount;

                return true;
            }
            else
            {
                if (debugMode)
                    Debug.LogWarning($"Failed to combine submeshes for mesh '{meshName}'.");
                return false;
            }
        }
        else
        {
            // Process as usual
            bool result = ProcessSingleMesh(mesh, meshName, materials != null && materials.Length > 0 ? materials[0] : null, meshTransform);

            // Update statistics
            totalVerticesAfterProcessing += mesh.vertexCount;

            return result;
        }
    }

    Mesh ExtractSubMesh(Mesh mesh, int subMeshIndex)
    {
        Mesh subMesh = new Mesh();
        subMesh.name = mesh.name + "_SubMesh" + subMeshIndex;

        int[] indices = mesh.GetTriangles(subMeshIndex);

        Vector3[] vertices = mesh.vertices;
        Vector2[] uvs = mesh.uv;

        // Safety check for UV array
        bool hasValidUVs = uvs != null && uvs.Length == vertices.Length;
        if (!hasValidUVs && debugMode)
        {
            Debug.LogWarning($"SubMesh {subMeshIndex}: UV array mismatch or missing. Vertices: {vertices.Length}, UVs: {(uvs != null ? uvs.Length : 0)}. Using default UVs.");
        }

        List<Vector3> newVertices = new List<Vector3>();
        List<Vector2> newUVs = new List<Vector2>();
        int[] newIndices = new int[indices.Length];

        Dictionary<int, int> indexMap = new Dictionary<int, int>();

        for (int i = 0; i < indices.Length; i++)
        {
            int oldIndex = indices[i];

            if (!indexMap.ContainsKey(oldIndex))
            {
                indexMap[oldIndex] = newVertices.Count;
                newVertices.Add(vertices[oldIndex]);
                // Safe UV access with fallback
                if (hasValidUVs && oldIndex < uvs.Length)
                {
                    newUVs.Add(uvs[oldIndex]);
                }
                else
                {
                    newUVs.Add(Vector2.zero); // Fallback UV
                }
            }

            newIndices[i] = indexMap[oldIndex];
        }

        subMesh.vertices = newVertices.ToArray();
        subMesh.triangles = newIndices;
        subMesh.uv = newUVs.ToArray();

        return subMesh;
    }

    Mesh CombineMeshes(List<Mesh> meshes, Transform meshTransform)
    {
        List<CombineInstance> combineInstances = new List<CombineInstance>();

        foreach (Mesh m in meshes)
        {
            CombineInstance ci = new CombineInstance();
            ci.mesh = m;
            ci.transform = Matrix4x4.identity;
            combineInstances.Add(ci);
        }

        Mesh combinedMesh = new Mesh();
        combinedMesh.name = "CombinedMesh";

        combinedMesh.CombineMeshes(combineInstances.ToArray(), true, false);

        // Preserve vertex colors
        List<Color> combinedColors = new List<Color>();

        foreach (Mesh m in meshes)
        {
            List<Color> colors = new List<Color>();
            m.GetColors(colors);
            combinedColors.AddRange(colors);
        }

        combinedMesh.SetColors(combinedColors);

        return combinedMesh;
    }

    bool ProcessSingleMesh(Mesh mesh, string meshName, Material material, Transform meshTransform)
    {
        Vector3[] oldVertices = mesh.vertices;
        int[] oldTriangles = mesh.triangles;
        Vector2[] uvs = mesh.uv; // Retrieve UVs

        if (uvs == null || uvs.Length == 0)
        {
            if (debugMode)
                Debug.LogWarning($"Mesh '{meshName}' does not have UVs. Skipping.");
            return false;
        }

        // Log material properties
        if (debugMode)
            LogMaterialProperties(material);

        // Get the texture from the material
        Texture2D texture = null;
        if (material != null)
        {
            // Try to get the texture using the specified property name
            if (material.HasProperty(texturePropertyName))
            {
                Texture matTexture = material.GetTexture(texturePropertyName);
                texture = matTexture as Texture2D;
                if (texture != null)
                {
                    if (debugMode)
                        Debug.Log($"Texture '{texture.name}' of type '{texture.GetType()}' retrieved from material '{material.name}' using property '{texturePropertyName}'.");
                }
                else if (matTexture != null)
                {
                    if (debugMode)
                        Debug.LogWarning($"Texture '{matTexture.name}' is not a Texture2D. It's a '{matTexture.GetType()}'.");
                }
                else
                {
                    if (debugMode)
                        Debug.LogWarning($"Texture property '{texturePropertyName}' is null in material '{material.name}'.");
                }
            }

            // If texture is still null, try to find any texture property
            if (texture == null)
            {
                Shader shader = material.shader;
                int propertyCount = shader.GetPropertyCount();
                for (int i = 0; i < propertyCount; i++)
                {
                    ShaderPropertyType propertyType = shader.GetPropertyType(i);
                    if (propertyType == ShaderPropertyType.Texture)
                    {
                        string propName = shader.GetPropertyName(i);
                        Texture matTexture = material.GetTexture(propName);
                        texture = matTexture as Texture2D;
                        if (texture != null)
                        {
                            texturePropertyName = propName; // Update the property name
                            if (debugMode)
                                Debug.Log($"Texture '{texture.name}' of type '{texture.GetType()}' retrieved from material '{material.name}' using property '{propName}'.");
                            break;
                        }
                        else if (matTexture != null)
                        {
                            if (debugMode)
                                Debug.LogWarning($"Texture '{matTexture.name}' is not a Texture2D. It's a '{matTexture.GetType()}'.");
                        }
                    }
                }
            }

            if (texture == null)
            {
                if (debugMode)
                    Debug.LogWarning($"No Texture2D found in material '{material.name}'.");
            }
        }
        else
        {
            if (debugMode)
                Debug.LogWarning($"Material is null for mesh '{meshName}'.");
        }

        // Get the material color if it has the _Color property
        Color materialColor = Color.white;
        if (material != null && material.HasProperty("_Color"))
        {
            materialColor = material.color;
            if (debugMode)
                Debug.Log($"Material color for '{material.name}': {materialColor}");
        }
        else
        {
            if (material != null)
            {
                if (debugMode)
                    Debug.LogWarning($"Material '{material.name}' does not have a '_Color' property. Using Color.white.");
            }
            else
            {
                if (debugMode)
                    Debug.LogWarning($"Material is null for mesh '{meshName}'. Using Color.white.");
            }
        }

        // Check if texture is readable
        if (texture != null)
        {
            try
            {
                if (!texture.isReadable)
                {
                    if (debugMode)
                        Debug.LogWarning($"Texture '{texture.name}' is not readable. Creating a temporary readable copy.");

                    // Create a temporary readable copy
                    Texture2D readableTexture = MakeTextureReadable(texture);

                    if (readableTexture != null)
                    {
                        texture = readableTexture;
                    }
                    else
                    {
                        if (debugMode)
                            Debug.LogError($"Failed to create a readable copy of texture '{texture.name}'.");
                        texture = null;
                    }
                }
            }
            catch (UnityException ex)
            {
                if (debugMode)
                    Debug.LogError($"UnityException while checking if texture '{texture.name}' is readable: {ex.Message}");
                texture = null;
            }
            catch (System.Exception ex)
            {
                if (debugMode)
                    Debug.LogError($"Exception while checking if texture '{texture.name}' is readable: {ex.Message}");
                texture = null;
            }
        }

        // Test texture sampling at known UVs
        if (texture != null && debugMode)
        {
            TestTextureSampling(texture);
        }

        // Detect if any UVs are outside [0,1] to identify tiling textures
        bool isTilingMesh = false;
        foreach (var uv in uvs)
        {
            if (uv.x < 0f || uv.x > 1f || uv.y < 0f || uv.y > 1f)
            {
                isTilingMesh = true;
                if (debugMode)
                    Debug.Log($"Mesh '{meshName}' has tiling UVs.");
                break;
            }
        }

        // If the mesh has tiling UVs, compute the average color of the entire texture
        Color avgTextureColor = Color.white; // default
        if (isTilingMesh)
        {
            if (texture != null)
            {
                avgTextureColor = AverageColorOfTexture(texture);

                // Multiply with material color
                avgTextureColor *= materialColor;

                // Apply adjustments
                avgTextureColor = AdjustColor(avgTextureColor, textureStrength, textureContrast, textureBrightness);

                // Set alpha to 0
                avgTextureColor.a = 0f;

                if (debugMode)
                    Debug.Log($"Average color of texture '{texture.name}': {avgTextureColor}");
            }
            else
            {
                avgTextureColor = materialColor;
                avgTextureColor.a = 0f;
                if (debugMode)
                    Debug.LogWarning($"Texture is null for mesh '{meshName}'. Using material color as average color.");
            }
        }

        int totalTriangles = oldTriangles.Length / 3;
        int processedTriangles = 0;

        // Check if mesh has existing vertex colors
        List<Color> existingColors = new List<Color>();
        mesh.GetColors(existingColors);
        bool hasExistingColors = existingColors.Count == oldVertices.Length;

        if (hasExistingColors && keepOldColors)
        {
            if (debugMode)
                Debug.Log($"Mesh '{meshName}' has existing vertex colors. Preserving them and adding new texture colors.");
        }

        List<Vector3> newVertices = new List<Vector3>();
        List<int> newTriangles = new List<int>();
        List<Color> newColors = new List<Color>();
        List<Vector2> newUVs = new List<Vector2>(); // List to store new UVs

        for (int i = 0; i < oldTriangles.Length; i += 3)
        {
            processedTriangles++;

            int index0 = oldTriangles[i];
            int index1 = oldTriangles[i + 1];
            int index2 = oldTriangles[i + 2];

            Vector3 v0 = oldVertices[index0];
            Vector3 v1 = oldVertices[index1];
            Vector3 v2 = oldVertices[index2];

            Vector2 uv0 = uvs[index0];
            Vector2 uv1 = uvs[index1];
            Vector2 uv2 = uvs[index2];

            Color avgColor;

            if (isTilingMesh)
            {
                // Use the average color of the entire texture
                avgColor = avgTextureColor;
            }
            else
            {
                // Sample texture color from the triangle by averaging all pixels within the triangle on the texture
                avgColor = AverageColorOverUVTriangle(texture, uv0, uv1, uv2, v0, v1, v2, materialColor, meshTransform);
            }

            // Create new vertices for the triangle and assign colors
            int newIndex0 = newVertices.Count;
            newVertices.Add(v0);
            // Get existing color for this vertex, or white if none exists
            Color existingColor0 = (hasExistingColors && keepOldColors) ? existingColors[index0] : Color.white;
            // Combine existing color with new texture color - we add them together instead of overwriting
            newColors.Add(BlendColors(existingColor0, avgColor));
            newUVs.Add(uv0); // Preserve original UV

            int newIndex1 = newVertices.Count;
            newVertices.Add(v1);
            Color existingColor1 = (hasExistingColors && keepOldColors) ? existingColors[index1] : Color.white;
            newColors.Add(BlendColors(existingColor1, avgColor));
            newUVs.Add(uv1); // Preserve original UV

            int newIndex2 = newVertices.Count;
            newVertices.Add(v2);
            Color existingColor2 = (hasExistingColors && keepOldColors) ? existingColors[index2] : Color.white;
            newColors.Add(BlendColors(existingColor2, avgColor));
            newUVs.Add(uv2); // Preserve original UV

            newTriangles.Add(newIndex0);
            newTriangles.Add(newIndex1);
            newTriangles.Add(newIndex2);

            // Display progress bar
            float progress = (float)processedTriangles / totalTriangles;
            EditorUtility.DisplayProgressBar("Processing Meshes", $"Processing {meshName}", progress);
        }

        // Post-processing: 

        if (averageColors)
        {
            Color totalColor = Color.black;
            foreach (Color c in newColors)
            {
                totalColor += c;
            }
            Color averageColor = totalColor / newColors.Count;

            // Apply the average color to each vertex color, based on the strength
            for (int i = 0; i < newColors.Count; i++)
            {
                newColors[i] = Color.Lerp(newColors[i], averageColor, colorAverageStrength);
            }
        }

        // Apply average neighbor colors
        if (averageNeighborColors)
        {
            AverageNeighborVertexColorsDirect(newVertices, newColors, neighborRadius, neighborAverageStrength);
        }

        // Apply color tint
        if (colorTintEnabled)
        {
            for (int i = 0; i < newColors.Count; i++)
            {
                newColors[i] *= colorTint;
            }
        }

        if (debugMode)
            Debug.Log("Clearing mesh");

        // Update the mesh with new data
        mesh.Clear();
        mesh.SetVertices(newVertices);
        mesh.SetTriangles(newTriangles, 0);
        mesh.SetColors(newColors);
        mesh.SetUVs(0, newUVs); // Assign the new UVs
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        mesh.RecalculateBounds();
        mesh.Optimize();

        // Optimize vertices if enabled
        if (optimizeVertices)
        {
            OptimizeMeshVertices(mesh, newColors, colorSimilarityThreshold);

            // Update statistics
            totalVerticesAfterOptimization += mesh.vertexCount;
        }
        else
        {
            // Update statistics
            totalVerticesAfterOptimization += mesh.vertexCount;
        }

        if (debugMode)
            Debug.Log($"Mesh '{meshName}' processed. Total Vertices: {newVertices.Count}, Total Triangles: {newTriangles.Count / 3}");

        return true;
    }

    // Helper method to blend existing vertex color with new texture color
    Color BlendColors(Color existingColor, Color textureColor)
    {
        if (keepOldColors)
        {
            // Add texture color to existing color while keeping alpha
            float alpha = existingColor.a;
            Color result = existingColor * textureColor;
            result.a = alpha;
            return result;
        }
        else
        {
            // Just use texture color
            return textureColor;
        }
    }

    void AverageNeighborVertexColorsDirect(List<Vector3> vertices, List<Color> colors, float radius, float strength)
    {
        Color[] originalColors = colors.ToArray(); // Keep a copy of original colors
        int vertexCount = vertices.Count;

        // Brute-force approach
        for (int i = 0; i < vertexCount; i++)
        {
            Vector3 vi = vertices[i];
            Color ci = originalColors[i];
            Color accumulatedColor = ci;
            int neighborCount = 1;

            for (int j = 0; j < vertexCount; j++)
            {
                if (i == j) continue;

                Vector3 vj = vertices[j];
                float distance = Vector3.Distance(vi, vj);
                if (distance <= radius)
                {
                    accumulatedColor += originalColors[j];
                    neighborCount++;
                }
            }

            Color averageNeighborColor = accumulatedColor / neighborCount;

            // Blend the original color towards the average neighbor color
            colors[i] = Color.Lerp(ci, averageNeighborColor, strength);
        }
    }

    Color AverageColorOverUVTriangle(Texture2D texture, Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector3 v0, Vector3 v1, Vector3 v2, Color materialColor, Transform meshTransform)
    {
        if (texture == null)
        {
            if (debugMode)
                Debug.LogWarning("Texture is null when entering AverageColorOverUVTriangle. Using material color.");
            return materialColor;
        }

        int textureWidth = texture.width;
        int textureHeight = texture.height;

        // Convert UVs to pixel coordinates
        Vector2 p0 = new Vector2(uv0.x * (textureWidth - 1), uv0.y * (textureHeight - 1));
        Vector2 p1 = new Vector2(uv1.x * (textureWidth - 1), uv1.y * (textureHeight - 1));
        Vector2 p2 = new Vector2(uv2.x * (textureWidth - 1), uv2.y * (textureHeight - 1));

        // Compute bounding box
        int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p0.x, p1.x, p2.x)), 0, textureWidth - 1);
        int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p0.x, p1.x, p2.x)), 0, textureWidth - 1);
        int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p0.y, p1.y, p2.y)), 0, textureHeight - 1);
        int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p0.y, p1.y, p2.y)), 0, textureHeight - 1);

        Color accumulatedColor = Color.black;
        int pixelCount = 0;

        // For debug visualization
        List<Vector3> samplingPoints = new List<Vector3>();

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector2 p = new Vector2(x, y);

                // Convert back to UV coordinates
                Vector2 uv = new Vector2(p.x / (textureWidth - 1), p.y / (textureHeight - 1));

                if (IsPointInTriangle(uv, uv0, uv1, uv2))
                {
                    Color sampledColor = texture.GetPixel(x, y);
                    accumulatedColor += sampledColor;
                    pixelCount++;

                    // For debug visualization
                    if (debugMode)
                    {
                        // Compute the sample point in world space
                        Vector3 samplePoint = BarycentricInterpolation(uv, uv0, uv1, uv2, v0, v1, v2);

                        // Transform the sample point to world space
                        samplePoint = meshTransform.TransformPoint(samplePoint);

                        samplingPoints.Add(samplePoint);
                    }
                }
            }
        }

        // If no pixels were found (degenerate UV triangle or sub-pixel triangle), sample at centroid
        if (pixelCount == 0)
        {
            Vector2 centroidUV = (uv0 + uv1 + uv2) / 3f;

            if (debugMode)
            {
                bool isDegenerateUV = (uv0 == uv1 && uv1 == uv2);
                Debug.LogWarning($"No pixels in UV triangle. {(isDegenerateUV ? "Degenerate UVs (all identical)" : "Sub-pixel triangle")}. UVs: ({uv0}, {uv1}, {uv2}), Sampling centroid: {centroidUV}");
            }

            Color centroidColor = texture.GetPixelBilinear(centroidUV.x, centroidUV.y);
            centroidColor *= materialColor;
            centroidColor = AdjustColor(centroidColor, textureStrength, textureContrast, textureBrightness);
            return centroidColor;
        }

        // Compute the average color
        Color averageTextureColor = accumulatedColor / pixelCount;

        // Multiply with material color
        Color finalColor = averageTextureColor * materialColor;

        // Apply adjustments
        finalColor = AdjustColor(finalColor, textureStrength, textureContrast, textureBrightness);

        // Add sampling points to debug list
        if (debugMode)
        {
            debugSamplingPoints.AddRange(samplingPoints);
        }

        return finalColor;
    }

    Color AverageColorOfTexture(Texture2D texture)
    {
        if (texture == null)
        {
            if (debugMode)
                Debug.LogWarning("Texture is null in AverageColorOfTexture. Returning Color.white.");
            return Color.white;
        }

        Color[] pixels = texture.GetPixels();
        Color sumColor = Color.black;
        int pixelCount = pixels.Length;

        for (int i = 0; i < pixelCount; i++)
        {
            sumColor += pixels[i];
        }

        Color avgColor = sumColor / pixelCount;
        return avgColor;
    }

    bool IsPointInTriangle(Vector2 p, Vector2 p0, Vector2 p1, Vector2 p2)
    {
        // Barycentric coordinate method
        float s = p0.y * p2.x - p0.x * p2.y + (p2.y - p0.y) * p.x + (p0.x - p2.x) * p.y;
        float t = p0.x * p1.y - p0.y * p1.x + (p0.y - p1.y) * p.x + (p1.x - p0.x) * p.y;

        if ((s < 0) != (t < 0))
            return false;

        float area = -p1.y * p2.x + p0.y * (p2.x - p1.x) + p0.x * (p1.y - p2.y) + p1.x * p2.y;

        return area == 0 || (s + t <= area && area > 0) || (s + t >= area && area < 0);
    }

    Vector3 BarycentricInterpolation(Vector2 uv, Vector2 uv0, Vector2 uv1, Vector2 uv2, Vector3 v0, Vector3 v1, Vector3 v2)
    {
        // Compute barycentric coordinates
        float denom = (uv1.y - uv2.y) * (uv0.x - uv2.x) + (uv2.x - uv1.x) * (uv0.y - uv2.y);
        float a = ((uv1.y - uv2.y) * (uv.x - uv2.x) + (uv2.x - uv1.x) * (uv.y - uv2.y)) / denom;
        float b = ((uv2.y - uv0.y) * (uv.x - uv2.x) + (uv0.x - uv2.x) * (uv.y - uv2.y)) / denom;
        float c = 1.0f - a - b;

        // Interpolate vertex positions
        Vector3 position = a * v0 + b * v1 + c * v2;

        return position;
    }

    Color AdjustColor(Color color, float strength, float contrast, float brightness)
    {
        // Adjust brightness first
        color.r = Mathf.Clamp01(color.r + brightness);
        color.g = Mathf.Clamp01(color.g + brightness);
        color.b = Mathf.Clamp01(color.b + brightness);

        // Adjust contrast
        color.r = ((color.r - 0.5f) * contrast + 0.5f);
        color.g = ((color.g - 0.5f) * contrast + 0.5f);
        color.b = ((color.b - 0.5f) * contrast + 0.5f);

        // Clamp after contrast adjustment
        color.r = Mathf.Clamp01(color.r);
        color.g = Mathf.Clamp01(color.g);
        color.b = Mathf.Clamp01(color.b);

        // Apply texture strength
        color = Color.Lerp(Color.white, color, strength);

        return color;
    }

    string SaveMesh(Mesh mesh, string folderPath, string objectName)
    {
        if (!AssetDatabase.IsValidFolder(folderPath))
        {
            string parentFolder = Path.GetDirectoryName(folderPath);
            string newFolderName = Path.GetFileName(folderPath);

            AssetDatabase.CreateFolder(parentFolder, newFolderName);
            AssetDatabase.SaveAssets();
        }

        // Clean up mesh name - remove _Modified suffix if present to avoid name growing
        string baseMeshName = mesh.name;
        while (baseMeshName.EndsWith("_Modified"))
        {
            baseMeshName = baseMeshName.Substring(0, baseMeshName.Length - "_Modified".Length);
        }

        // Generate unique filename with random 10-digit number
        string assetPath;
        int maxAttempts = 100;
        int attempts = 0;
        do
        {
            long randomNumber = (long)(meshNameRandom.NextDouble() * 9000000000L) + 1000000000L; // 10-digit number
            string sanitizedObjectName = SanitizeFileName(objectName);
            assetPath = $"{folderPath}/Mesh_{sanitizedObjectName}_{randomNumber}.asset";
            attempts++;
        }
        while (AssetDatabase.LoadAssetAtPath<Mesh>(assetPath) != null && attempts < maxAttempts);

        if (attempts >= maxAttempts)
        {
            Debug.LogWarning($"[Texture to Vertex Baker] Could not generate unique filename after {maxAttempts} attempts. Using timestamp.");
            string sanitizedObjectName = SanitizeFileName(objectName);
            assetPath = $"{folderPath}/Mesh_{sanitizedObjectName}_{System.DateTime.Now.Ticks}.asset";
        }

        // Update mesh name to match the file
        mesh.name = Path.GetFileNameWithoutExtension(assetPath);

        AssetDatabase.CreateAsset(mesh, assetPath);
        AssetDatabase.SaveAssets();

        // Track this mesh for later finalization
        createdMeshPaths.Add(assetPath);

        if (debugMode)
            Debug.Log($"Mesh saved at {assetPath}");

        return assetPath;
    }

    void DisableReadWriteOnMeshAsset(string assetPath)
    {
        Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(assetPath);
        if (mesh == null)
            return;

        // Use SerializedObject to modify the isReadable property on the saved asset
        SerializedObject serializedMesh = new SerializedObject(mesh);
        SerializedProperty isReadableProp = serializedMesh.FindProperty("m_IsReadable");
        if (isReadableProp != null)
        {
            isReadableProp.boolValue = false;
            serializedMesh.ApplyModifiedPropertiesWithoutUndo();
            AssetDatabase.SaveAssets();

            if (debugMode)
                Debug.Log($"[Texture to Vertex Baker] Disabled Read/Write on saved mesh: {assetPath}");
        }
    }

    void FinalizeConvertedMeshes()
    {
        // Find all .asset meshes in the ConvertedMeshes folders
        List<string> allMeshPaths = new List<string>();

        // Add tracked meshes from this session
        allMeshPaths.AddRange(createdMeshPaths);

        // Also scan the ConvertedMeshes folder for any meshes we might have missed
        string[] convertedMeshesFolders = AssetDatabase.FindAssets("ConvertedMeshes", new[] { "Assets" });
        foreach (string folderGuid in convertedMeshesFolders)
        {
            string folderPath = AssetDatabase.GUIDToAssetPath(folderGuid);
            if (AssetDatabase.IsValidFolder(folderPath))
            {
                string[] meshGuids = AssetDatabase.FindAssets("t:Mesh", new[] { folderPath });
                foreach (string meshGuid in meshGuids)
                {
                    string meshPath = AssetDatabase.GUIDToAssetPath(meshGuid);
                    if (meshPath.EndsWith(".asset") && !allMeshPaths.Contains(meshPath))
                    {
                        allMeshPaths.Add(meshPath);
                    }
                }
            }
        }

        if (allMeshPaths.Count == 0)
        {
            EditorUtility.DisplayDialog("Finalize", "No converted meshes found to finalize.", "OK");
            return;
        }

        // Count meshes that need finalization vs already finalized
        int needsFinalizationCount = 0;
        int alreadyFinalizedCount = 0;
        foreach (string meshPath in allMeshPaths)
        {
            Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
            if (mesh != null)
            {
                if (mesh.isReadable)
                    needsFinalizationCount++;
                else
                    alreadyFinalizedCount++;
            }
        }

        if (needsFinalizationCount == 0)
        {
            EditorUtility.DisplayDialog("Finalize", $"All {alreadyFinalizedCount} mesh(es) are already finalized.", "OK");
            createdMeshPaths.Clear();
            return;
        }

        // Confirm with user - show clear breakdown
        string dialogMessage = $"Meshes to finalize: {needsFinalizationCount}";
        if (alreadyFinalizedCount > 0)
            dialogMessage += $"\nAlready finalized: {alreadyFinalizedCount}";
        dialogMessage += "\n\nThis will disable Read/Write to save memory.\nAfter finalization, you'll need to enable Read/Write again to edit these meshes.\n\nContinue?";

        bool proceed = EditorUtility.DisplayDialog("Finalize Converted Meshes", dialogMessage, "Finalize", "Cancel");

        if (!proceed)
            return;

        int disabledCount = 0;
        int colliderUpdateCount = 0;
        int alreadyDisabledCount = 0;

        EditorUtility.DisplayProgressBar("Finalizing Meshes", "Processing...", 0f);

        try
        {
            // First, update all MeshColliders on the target object
            if (targetObject != null)
            {
                MeshFilter[] meshFilters = targetObject.GetComponentsInChildren<MeshFilter>(true);
                foreach (MeshFilter mf in meshFilters)
                {
                    MeshCollider meshCollider = mf.GetComponent<MeshCollider>();
                    if (meshCollider != null && mf.sharedMesh != null)
                    {
                        // Check if the mesh is one of our converted meshes
                        string meshPath = AssetDatabase.GetAssetPath(mf.sharedMesh);
                        if (!string.IsNullOrEmpty(meshPath) && allMeshPaths.Contains(meshPath))
                        {
                            Undo.RecordObject(meshCollider, "Update MeshCollider");
                            meshCollider.sharedMesh = mf.sharedMesh;
                            EditorUtility.SetDirty(meshCollider);
                            colliderUpdateCount++;
                            if (debugMode)
                                Debug.Log($"[Texture to Vertex Baker] Updated MeshCollider on '{mf.gameObject.name}'");
                        }
                    }
                }
            }

            // Now disable Read/Write on all meshes
            for (int i = 0; i < allMeshPaths.Count; i++)
            {
                string meshPath = allMeshPaths[i];
                EditorUtility.DisplayProgressBar("Finalizing Meshes", $"Processing {Path.GetFileName(meshPath)}", (float)i / allMeshPaths.Count);

                Mesh mesh = AssetDatabase.LoadAssetAtPath<Mesh>(meshPath);
                if (mesh == null)
                    continue;

                if (mesh.isReadable)
                {
                    DisableReadWriteOnMeshAsset(meshPath);
                    disabledCount++;
                }
                else
                {
                    alreadyDisabledCount++;
                }
            }

            AssetDatabase.SaveAssets();
        }
        finally
        {
            EditorUtility.ClearProgressBar();
        }

        // Clear tracked meshes
        createdMeshPaths.Clear();

        // Re-scan to update the UI
        if (targetObject != null)
        {
            lastScannedObject = null; // Force re-scan
        }

        string message = $"Finalization complete!\n\n";
        message += $"Meshes with Read/Write disabled: {disabledCount}\n";
        if (alreadyDisabledCount > 0)
            message += $"Already disabled: {alreadyDisabledCount}\n";
        if (colliderUpdateCount > 0)
            message += $"MeshColliders updated: {colliderUpdateCount}";

        EditorUtility.DisplayDialog("Finalization Complete", message, "OK");
    }

    string SanitizeFileName(string name)
    {
        // First handle standard invalid characters
        string invalidChars = Regex.Escape(new string(Path.GetInvalidFileNameChars()));
        string invalidReStr = string.Format(@"[{0}]", invalidChars);
        string result = Regex.Replace(name, invalidReStr, "_");

        // Then specifically handle spaces and parentheses which can cause issues with Unity's asset system
        result = result.Replace(" ", "_").Replace("(", "_").Replace(")", "_");

        // Collapse consecutive underscores to a single one
        result = Regex.Replace(result, @"_+", "_");

        return result;
    }

    void LogMaterialProperties(Material material)
    {
        if (material == null)
        {
            if (debugMode)
                Debug.Log("Material is null.");
            return;
        }

        Shader shader = material.shader;
        int propertyCount = shader.GetPropertyCount();
        if (debugMode)
            Debug.Log($"Listing properties for material '{material.name}' with shader '{shader.name}':");
        for (int i = 0; i < propertyCount; i++)
        {
            string propertyName = shader.GetPropertyName(i);
            ShaderPropertyType propertyType = shader.GetPropertyType(i);
            if (debugMode)
                Debug.Log($"Property {i}: Name = '{propertyName}', Type = {propertyType}");
        }
    }

    void TestTextureSampling(Texture2D texture)
    {
        Vector2[] testUVs = new Vector2[]
        {
            new Vector2(0.0f, 0.0f),
            new Vector2(0.5f, 0.5f),
            new Vector2(1.0f, 1.0f),
        };

        foreach (Vector2 uv in testUVs)
        {
            Color sampledColor = texture.GetPixelBilinear(uv.x, uv.y);
            if (debugMode)
                Debug.Log($"Test UV ({uv.x:F3}, {uv.y:F3}) - Sampled Color: {sampledColor}");
        }
    }

    Texture2D MakeTextureReadable(Texture2D texture)
    {
        // Create a temporary RenderTexture of the same size as the texture
        RenderTexture tempRT = RenderTexture.GetTemporary(
            texture.width,
            texture.height,
            0,
            RenderTextureFormat.Default,
            RenderTextureReadWrite.Linear);

        // Blit the texture onto the RenderTexture
        Graphics.Blit(texture, tempRT);

        // Backup the currently active RenderTexture
        RenderTexture previous = RenderTexture.active;

        // Set the RenderTexture as active
        RenderTexture.active = tempRT;

        // Create a new readable Texture2D
        Texture2D readableTexture = new Texture2D(texture.width, texture.height, TextureFormat.RGBA32, false);

        // Read the pixels from the RenderTexture to the new Texture2D
        readableTexture.ReadPixels(new Rect(0, 0, texture.width, texture.height), 0, 0);
        readableTexture.Apply();

        // Restore the previous active RenderTexture
        RenderTexture.active = previous;

        // Release the temporary RenderTexture
        RenderTexture.ReleaseTemporary(tempRT);

        return readableTexture;
    }
    void SampleLightmapsAndMultiplyVertexColors(Mesh mesh, Renderer renderer, string meshName)
    {
        int lightmapIndex = renderer.lightmapIndex;

        if (debugMode)
        {
            Debug.Log($"Renderer '{renderer.gameObject.name}' lightmapIndex: {lightmapIndex}");
            Debug.Log($"Renderer '{renderer.gameObject.name}' lightmapScaleOffset: {renderer.lightmapScaleOffset}");
        }

        if (lightmapIndex < 0 || lightmapIndex >= LightmapSettings.lightmaps.Length)
        {
            if (debugMode)
                Debug.LogWarning($"Invalid lightmap index for mesh '{meshName}'. Skipping lightmap sampling.");
            return;
        }

        Texture2D lightmap = LightmapSettings.lightmaps[lightmapIndex].lightmapColor;

        if (lightmap == null)
        {
            if (debugMode)
                Debug.LogWarning($"Lightmap texture is null for mesh '{meshName}'. Skipping lightmap sampling.");
            return;
        }
        else if (debugMode)
        {
            Debug.Log($"Lightmap '{lightmap.name}' retrieved for renderer '{renderer.gameObject.name}'.");
        }

        // Ensure lightmap is readable
        if (!lightmap.isReadable)
        {
            if (debugMode)
                Debug.LogWarning($"Lightmap '{lightmap.name}' is not readable. Attempting to make it readable.");
            lightmap = MakeTextureReadable(lightmap);
        }

        Vector2[] uv2s = mesh.uv2;

        if (uv2s == null || uv2s.Length != mesh.vertexCount)
        {
            if (debugMode)
                Debug.LogWarning($"Mesh '{meshName}' does not have valid UV2s. Generating lightmap UVs.");
            // Generate lightmap UVs
            UnwrapParam unwrapParams = new UnwrapParam();
            UnwrapParam.SetDefaults(out unwrapParams);
            Unwrapping.GenerateSecondaryUVSet(mesh, unwrapParams);
            uv2s = mesh.uv2;
        }

        if (uv2s == null || uv2s.Length != mesh.vertexCount)
        {
            if (debugMode)
                Debug.LogWarning($"Failed to generate UV2s for mesh '{meshName}'. Skipping lightmap sampling.");
            return;
        }

        if (debugMode)
        {
            Debug.Log($"UV2s count for mesh '{meshName}': {uv2s.Length}");
            Debug.Log($"Sample UV2[0]: {uv2s[0]}");
        }

        int[] triangles = mesh.triangles;
        int vertexCount = mesh.vertexCount;

        // Get existing vertex colors
        List<Color> colors = new List<Color>();
        mesh.GetColors(colors);
        if (colors.Count != vertexCount)
        {
            Debug.LogWarning($"Existing vertex colors count ({colors.Count}) does not match vertex count ({vertexCount}). Resetting colors.");
            colors = new List<Color>(vertexCount);
            for (int i = 0; i < vertexCount; i++)
            {
                colors.Add(Color.white);
            }
        }
        Debug.Log($"Initial colors count: {colors.Count}");

        // Arrays to accumulate colors and counts per vertex
        Color[] accumulatedColors = new Color[vertexCount];
        int[] counts = new int[vertexCount];

        // Loop over each triangle
        for (int i = 0; i < triangles.Length; i += 3)
        {
            int index0 = triangles[i];
            int index1 = triangles[i + 1];
            int index2 = triangles[i + 2];

            Vector2 uv0 = ApplyLightmapScaleOffset(uv2s[index0], renderer.lightmapScaleOffset);
            Vector2 uv1 = ApplyLightmapScaleOffset(uv2s[index1], renderer.lightmapScaleOffset);
            Vector2 uv2 = ApplyLightmapScaleOffset(uv2s[index2], renderer.lightmapScaleOffset);

            // Compute average lightmap color over the triangle area
            Color avgLightmapColor = AverageColorOverUVTriangle(lightmap, uv0, uv1, uv2);

            // Accumulate the color for each vertex
            accumulatedColors[index0] += avgLightmapColor;
            accumulatedColors[index1] += avgLightmapColor;
            accumulatedColors[index2] += avgLightmapColor;

            // Increment counts
            counts[index0]++;
            counts[index1]++;
            counts[index2]++;
        }

        // Compute final colors for each vertex
        for (int i = 0; i < vertexCount; i++)
        {
            if (counts[i] > 0)
            {
                Color avgColor = accumulatedColors[i] / counts[i];
                Color originalColor = colors[i];
                colors[i] = new Color(
                    colors[i].r * avgColor.r,
                    colors[i].g * avgColor.g,
                    colors[i].b * avgColor.b,
                    0f  // Set alpha to 0
                );
                if (debugMode)
                    Debug.Log($"Vertex {i} - Original Color: {originalColor}, Avg Lightmap Color: {avgColor}, Final Color: {colors[i]}");
            }
            else
            {
                if (debugMode)
                    Debug.LogWarning($"Vertex {i} was not part of any triangle. Color unchanged: {colors[i]}");
            }
        }

        mesh.SetColors(colors);

        EditorUtility.SetDirty(mesh);
        AssetDatabase.SaveAssets();
        AssetDatabase.Refresh();
    }
    Vector2 ApplyLightmapScaleOffset(Vector2 uv, Vector4 lightmapScaleOffset)
    {
        uv.x = uv.x * lightmapScaleOffset.x + lightmapScaleOffset.z;
        uv.y = uv.y * lightmapScaleOffset.y + lightmapScaleOffset.w;
        return uv;
    }

    Color AverageColorOverUVTriangle(Texture2D texture, Vector2 uv0, Vector2 uv1, Vector2 uv2)
    {
        int textureWidth = texture.width;
        int textureHeight = texture.height;

        // Convert UVs to pixel coordinates
        Vector2 p0 = new Vector2(uv0.x * (textureWidth - 1), uv0.y * (textureHeight - 1));
        Vector2 p1 = new Vector2(uv1.x * (textureWidth - 1), uv1.y * (textureHeight - 1));
        Vector2 p2 = new Vector2(uv2.x * (textureWidth - 1), uv2.y * (textureHeight - 1));

        // Compute bounding box
        int minX = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p0.x, p1.x, p2.x)), 0, textureWidth - 1);
        int maxX = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p0.x, p1.x, p2.x)), 0, textureWidth - 1);
        int minY = Mathf.Clamp(Mathf.FloorToInt(Mathf.Min(p0.y, p1.y, p2.y)), 0, textureHeight - 1);
        int maxY = Mathf.Clamp(Mathf.CeilToInt(Mathf.Max(p0.y, p1.y, p2.y)), 0, textureHeight - 1);

        Color accumulatedColor = Color.black;
        int pixelCount = 0;

        for (int y = minY; y <= maxY; y++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                Vector2 p = new Vector2(x, y);
                Vector2 uv = new Vector2(p.x / (textureWidth - 1), p.y / (textureHeight - 1));

                if (IsPointInTriangle(uv, uv0, uv1, uv2))
                {
                    Color sampledColor = texture.GetPixel(x, y);
                    accumulatedColor += sampledColor;
                    pixelCount++;
                }
            }
        }

        if (pixelCount == 0)
        {
            // If no pixels were found within the triangle, sample at the centroid
            Vector2 centroidUV = (uv0 + uv1 + uv2) / 3f;
            Color sampledColor = texture.GetPixelBilinear(centroidUV.x, centroidUV.y);
            return sampledColor;
        }
        else
        {
            Color avgColor = accumulatedColor / pixelCount;
            return avgColor;
        }
    }



}