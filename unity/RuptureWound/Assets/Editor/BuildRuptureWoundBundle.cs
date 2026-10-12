using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEditor;
using UnityEngine;

public static class BuildRuptureWoundBundle
{
    private static readonly string[] ShaderAssetPaths =
    {
        "Assets/Rupture/WoundInterior.shader",
        "Assets/Rupture/BloodStream.shader",
        "Assets/Rupture/LimbSeverSurface.shader",
        "Assets/Rupture/ClothingSoak.shader",
        "Assets/Rupture/ClothingSoak.compute"
    };
    [Serializable]
    private sealed class Verification
    {
        public string bundle;
        public string sha256;
        public string unityVersion;
        public string verifiedUtc;
        public string[] shaders;
        public string[] computeShaders;
    }
    private const string BundleFileName = "rupture-wound.bundle";

    [MenuItem("Tools/Rupture/Build Wound Bundle")]
    public static void Build()
    {
        foreach (string shaderAssetPath in ShaderAssetPaths)
        {
            if (AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(shaderAssetPath) == null)
            {
                throw new FileNotFoundException("Rupture shader was not found.", shaderAssetPath);
            }
        }

        // Procedural variants have no scene renderer/material for Unity's automatic
        // usage scan. Keep them explicitly or the shipped bundle loses instancing.
        var graphics = new SerializedObject(AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0]);
        var stripping = graphics.FindProperty("m_InstancingStripping");
        int keepAll = Array.FindIndex(stripping.enumNames, name => name.Replace(" ", "") == "KeepAll");
        if (keepAll < 0) throw new InvalidOperationException("Unity instancing stripping contract is unavailable.");
        stripping.enumValueIndex = keepAll;
        graphics.ApplyModifiedPropertiesWithoutUndo();
        AssetDatabase.SaveAssets();

        string projectRoot = Directory.GetParent(Application.dataPath).FullName;
        string outputDirectory = Path.Combine(projectRoot, "AssetBundles");
        Directory.CreateDirectory(outputDirectory);
        AssetBundleManifest manifest = BuildPipeline.BuildAssetBundles(
            outputDirectory,
            new[]
            {
                new AssetBundleBuild
                {
                    assetBundleName = BundleFileName,
                    assetNames = ShaderAssetPaths
                }
            },
            BuildAssetBundleOptions.ForceRebuildAssetBundle | BuildAssetBundleOptions.ChunkBasedCompression,
            BuildTarget.StandaloneWindows64);
		string bundlePath = Path.Combine(outputDirectory, BundleFileName);
		if (manifest == null || !File.Exists(bundlePath))
		{
			throw new InvalidOperationException("Unity did not produce " + BundleFileName + ".");
		}

		AssetBundle verificationBundle = AssetBundle.LoadFromFile(bundlePath);
		if (verificationBundle == null)
		{
			throw new InvalidDataException("Unity produced a bundle that cannot be reopened: " + bundlePath);
		}
		try
		{
			string[] assetNames = verificationBundle.GetAllAssetNames();
			Shader[] bundledShaders = verificationBundle.LoadAllAssets<Shader>();
			bool hasWoundShader = bundledShaders.Any(candidate => candidate != null && candidate.name == "Rupture/HeadWoundInterior");
			bool hasStreamShader = bundledShaders.Any(candidate => candidate != null && candidate.name == "Rupture/PulsatingBloodStream");
			bool hasLimbShader = bundledShaders.Any(candidate => candidate != null && candidate.name == "Rupture/LimbSeverSurface");
			bool hasSoakShader = bundledShaders.Any(candidate => candidate != null && candidate.name == "Rupture/ClothingSoak" && candidate.passCount == 2);
			var compute = verificationBundle.LoadAllAssets<ComputeShader>();
			bool hasTransport = compute.Length == 1 && compute[0].name == "ClothingSoak" &&
				compute[0].HasKernel("Skin") && compute[0].HasKernel("Transport");
			if (assetNames.Length != 5 || bundledShaders.Length != 4 || !hasWoundShader || !hasStreamShader || !hasLimbShader || !hasSoakShader || !hasTransport)
			{
                throw new InvalidDataException("Wound bundle must contain four rendering shaders and GPU clothing transport.");
			}
		}
		finally
		{
			verificationBundle.Unload(false);
		}

		string hash;
		using (SHA256 sha256 = SHA256.Create())
		using (FileStream stream = File.OpenRead(bundlePath))
		{
			hash = BitConverter.ToString(sha256.ComputeHash(stream)).Replace("-", string.Empty);
		}
		File.WriteAllText(bundlePath + ".verification.json", JsonUtility.ToJson(new Verification
        {
            bundle = BundleFileName, sha256 = hash, unityVersion = Application.unityVersion,
            verifiedUtc = DateTime.UtcNow.ToString("O"),
            shaders = new[] { "Rupture/HeadWoundInterior", "Rupture/PulsatingBloodStream", "Rupture/LimbSeverSurface", "Rupture/ClothingSoak" },
            computeShaders = new[] { "ClothingSoak" }
        }, true));
		Debug.Log("Built and verified " + bundlePath + " SHA256=" + hash);
		if (!Application.isBatchMode) EditorUtility.RevealInFinder(outputDirectory);
    }
}
