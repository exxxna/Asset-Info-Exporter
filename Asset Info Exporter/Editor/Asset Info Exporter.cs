/*!
 * Asset Info Exporter
 *
 * © 2026 Enerchu (aVentuRine)
 *
 * Released under the MIT license.
 * see https://opensource.org/licenses/MIT
 */

using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine.Profiling;
using System.Text.RegularExpressions;

public class AssetInfoExporter : EditorWindow
{
    private GameObject targetPrefab;
    private DefaultAsset targetFolder;
    private string outputText = "";
    private Vector2 scrollPos;

    [MenuItem("Tools/aVentuRine/AssetInfoExporter")]
    public static void ShowWindow()
    {
        var window = GetWindow<AssetInfoExporter>("AssetInfoExporter");
        window.minSize = new Vector2(400, 500);
    }

    private void OnGUI()
    {
        GUILayout.Label("アセット情報出力ツール by aVentuRine", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        targetPrefab = (GameObject)EditorGUILayout.ObjectField("参照Prefab", targetPrefab, typeof(GameObject), false);
        targetFolder = (DefaultAsset)EditorGUILayout.ObjectField("商品フォルダ", targetFolder, typeof(DefaultAsset), false);

        EditorGUILayout.Space();

        if (GUILayout.Button("情報を出力", GUILayout.Height(30)))
        {
            if (targetPrefab == null || targetFolder == null)
            {
                EditorUtility.DisplayDialog("エラー", "参照Prefabと商品フォルダの両方を指定してください。", "OK");
                return;
            }
            GenerateInfo();
        }

        EditorGUILayout.Space();
        GUILayout.Label("出力結果", EditorStyles.boldLabel);

        scrollPos = EditorGUILayout.BeginScrollView(scrollPos);
        outputText = EditorGUILayout.TextArea(outputText, GUILayout.ExpandHeight(true));
        EditorGUILayout.EndScrollView();

        EditorGUILayout.Space();

        if (GUILayout.Button("クリップボードにコピー", GUILayout.Height(30)))
        {
            EditorGUIUtility.systemCopyBuffer = outputText;
            Debug.Log("クリップボードにコピーしました。");
        }
    }

    private void GenerateInfo()
    {
        outputText = "";
        string productFolderPath = AssetDatabase.GetAssetPath(targetFolder);

        // 前提アセットの取得
        outputText += "【前提アセット】\n";
        string dependenciesStr = GetDependenciesInfo(targetPrefab, productFolderPath);
        outputText += string.IsNullOrEmpty(dependenciesStr) ? "なし\n" : dependenciesStr;
        outputText += "\n";

        // パフォーマンス情報の取得
        outputText += "【パフォーマンス情報】\n";
        outputText += GetPrefabInfo(targetPrefab) + "\n\n";

        // フォルダ構成の取得
        outputText += "【フォルダ構成】\n";
        outputText += Path.GetFileName(productFolderPath) + "\n";
        outputText += GetFolderTree(productFolderPath, "");
    }

    private bool IsEditorOnly(Component comp)
    {
        if (comp == null) return true;
        Transform t = comp.transform;
        while (t != null)
        {
            if (t.gameObject.CompareTag("EditorOnly")) return true;
            t = t.parent;
        }
        return false;
    }

    private string GetDependenciesInfo(GameObject prefab, string productFolderPath)
    {
        // Prefabが依存している全てのアセットを取得
        Object[] dependencies = EditorUtility.CollectDependencies(new Object[] { prefab });
        Dictionary<string, string> externalAssets = new Dictionary<string, string>(); // Key: RootFolder, Value: VersionedName

        foreach (var dep in dependencies)
        {
            string path = AssetDatabase.GetAssetPath(dep);
            if (string.IsNullOrEmpty(path)) continue;

            // 商品フォルダ内、またはUnity標準・VRChatSDK関連は無視
            if (path.StartsWith(productFolderPath) || 
                path.StartsWith("Library") || 
                path.StartsWith("Resources") || 
                path.StartsWith("Packages/com.unity.") || 
                path.Contains("VRCSDK") || 
                path.Contains("VRChat") || 
                path.Contains("com.vrchat."))
            {
                continue;
            }

            // ルートフォルダの特定 (Assets/XXX または Packages/XXX)
            string[] parts = path.Split('/');
            if (parts.Length < 2) continue;
            string rootPath = parts[0] + "/" + parts[1];

            if (!externalAssets.ContainsKey(rootPath))
            {
                externalAssets[rootPath] = GetPackageInfo(rootPath, parts[1]);
            }
        }

        if (externalAssets.Count == 0) return "";

        List<string> assetLines = new List<string>();
        foreach (var asset in externalAssets.Values)
        {
            assetLines.Add($"・{asset}");
        }

        return string.Join("\n", assetLines) + "\n";
    }

    private string GetPackageInfo(string rootPath, string fallbackName)
    {
        string packageJsonPath = Path.Combine(rootPath, "package.json");
        string name = fallbackName;
        string version = "";

        if (File.Exists(packageJsonPath))
        {
            string json = File.ReadAllText(packageJsonPath);
            
            // displayNameを優先、なければname
            Match mName = Regex.Match(json, @"""displayName""\s*:\s*""([^""]+)""");
            if (!mName.Success) mName = Regex.Match(json, @"""name""\s*:\s*""([^""]+)""");
            if (mName.Success) name = mName.Groups[1].Value;

            // versionの取得
            Match mVer = Regex.Match(json, @"""version""\s*:\s*""([^""]+)""");
            if (mVer.Success) version = mVer.Groups[1].Value;
        }

        return string.IsNullOrEmpty(version) ? name : $"{name} (v{version})";
    }

    private string GetPrefabInfo(GameObject prefab)
    {
        long triangles = 0;
        HashSet<Material> materials = new HashSet<Material>();
        HashSet<Texture> textures = new HashSet<Texture>();
        int pbComponents = 0;
        int pbColliders = 0;
        int parametersBit = 0;

        // EditorOnlyを除外してコンポーネントを取得
        Component[] allComponents = prefab.GetComponentsInChildren<Component>(true)
                                          .Where(c => !IsEditorOnly(c))
                                          .ToArray();

        // --- ポリゴン数 (Triangles) ---
        foreach (var comp in allComponents)
        {
            if (comp is MeshFilter mf && mf.sharedMesh != null)
                triangles += mf.sharedMesh.triangles.Length / 3;
            else if (comp is SkinnedMeshRenderer smr && smr.sharedMesh != null)
                triangles += smr.sharedMesh.triangles.Length / 3;
        }

        // --- マテリアル (Materials) ---
        foreach (var comp in allComponents)
        {
            if (comp is Renderer r)
            {
                foreach (var mat in r.sharedMaterials)
                {
                    if (mat != null) materials.Add(mat);
                }
            }
        }

        // --- テクスチャメモリ (Texture Memory Usage) ---
        Object[] matObjects = materials.Cast<Object>().ToArray();
        Object[] deps = EditorUtility.CollectDependencies(matObjects);
        foreach (var dep in deps)
        {
            if (dep is Texture tex) textures.Add(tex);
        }
        
        long textureMemoryBytes = 0;
        foreach (var tex in textures)
        {
            textureMemoryBytes += Profiler.GetRuntimeMemorySizeLong(tex);
        }
        float textureMemoryMB = textureMemoryBytes / 1048576f;

        // --- PBコンポーネント・パラメーター計算 ---
        foreach (var comp in allComponents)
        {
            string typeName = comp.GetType().Name;

            if (typeName.Contains("VRCPhysBoneCollider")) pbColliders++;
            else if (typeName.Contains("VRCPhysBone")) pbComponents++;
            
            // MA Parametersの取得
            if (typeName == "ModularAvatarParameters")
            {
                SerializedObject so = new SerializedObject(comp);
                SerializedProperty parametersProp = so.FindProperty("parameters");
                if (parametersProp != null && parametersProp.isArray)
                {
                    for (int i = 0; i < parametersProp.arraySize; i++)
                    {
                        SerializedProperty element = parametersProp.GetArrayElementAtIndex(i);
                        SerializedProperty syncType = element.FindPropertyRelative("syncType");
                        
                        if (syncType != null)
                        {
                            int typeEnum = syncType.enumValueIndex;
                            if (typeEnum == 1) parametersBit += 1;
                            else if (typeEnum == 2 || typeEnum == 3) parametersBit += 8;
                        }
                    }
                }
            }
            
            // アバター本体の VRCExpressionParameters の取得
            if (typeName == "VRCAvatarDescriptor")
            {
                SerializedObject so = new SerializedObject(comp);
                SerializedProperty expParamsProp = so.FindProperty("expressionParameters");
                if (expParamsProp != null && expParamsProp.objectReferenceValue != null)
                {
                    SerializedObject expSO = new SerializedObject(expParamsProp.objectReferenceValue);
                    SerializedProperty parametersProp = expSO.FindProperty("parameters");
                    if (parametersProp != null && parametersProp.isArray)
                    {
                        for (int i = 0; i < parametersProp.arraySize; i++)
                        {
                            SerializedProperty element = parametersProp.GetArrayElementAtIndex(i);
                            SerializedProperty nameProp = element.FindPropertyRelative("name");
                            
                            // 名前が空のパラメーター（未割り当てのスロット）はスキップ
                            if (nameProp == null || string.IsNullOrEmpty(nameProp.stringValue)) continue;

                            SerializedProperty valueTypeProp = element.FindPropertyRelative("valueType");
                            if (valueTypeProp != null)
                            {
                                int typeEnum = valueTypeProp.enumValueIndex;
                                // VRCExpressionParameters.ValueType: 0=Int, 1=Float, 2=Bool
                                if (typeEnum == 0 || typeEnum == 1) parametersBit += 8;
                                else if (typeEnum == 2) parametersBit += 1;
                            }
                        }
                    }
                }
            }
        }

        // --- 結果の構築 (0のものは記載しない) ---
        List<string> infoLines = new List<string>();
        
        if (triangles > 0) infoLines.Add($"・Triangles : △{triangles:N0}");
        if (materials.Count > 0) infoLines.Add($"・Materials : {materials.Count}");
        if (textureMemoryMB > 0) infoLines.Add($"・Texture Memory Usage : {textureMemoryMB:F2} MB");
        if (pbComponents > 0) infoLines.Add($"・PB Components : {pbComponents}");
        if (pbColliders > 0) infoLines.Add($"・PB Colliders : {pbColliders}");
        if (parametersBit > 0) infoLines.Add($"・Parameters : {parametersBit} bit");

        return string.Join("\n", infoLines);
    }

    private string GetFolderTree(string path, string indent)
    {
        string result = "";
        
        string[] directories = Directory.GetDirectories(path);
        string[] files = Directory.GetFiles(path).Where(f => !f.EndsWith(".meta")).ToArray();

        // 拡張子ごとの集計
        Dictionary<string, int> fileCounts = new Dictionary<string, int>();
        foreach (var file in files)
        {
            string ext = Path.GetExtension(file).Replace(".", "");
            if (string.IsNullOrEmpty(ext)) ext = "File";
            ext = char.ToUpper(ext[0]) + ext.Substring(1); 

            if (fileCounts.ContainsKey(ext)) fileCounts[ext]++;
            else fileCounts[ext] = 1;
        }

        int totalItems = directories.Length + fileCounts.Count;
        int currentItem = 0;

        // ファイル集計を出力（拡張子ごとに改行）
        foreach (var kvp in fileCounts)
        {
            currentItem++;
            bool isLastItem = (currentItem == totalItems);
            string branch = isLastItem ? "└ " : "├ ";
            result += indent + branch + $"{kvp.Key} × {kvp.Value}\n";
        }

        // ディレクトリを出力
        for (int i = 0; i < directories.Length; i++)
        {
            currentItem++;
            bool isLastItem = (currentItem == totalItems);
            string branch = isLastItem ? "└ " : "├ ";
            string nextIndent = indent + (isLastItem ? "　 " : "│ ");

            result += indent + branch + Path.GetFileName(directories[i]) + "\n";
            result += GetFolderTree(directories[i], nextIndent);
        }

        return result;
    }
}