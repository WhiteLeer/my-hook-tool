using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using Object = UnityEngine.Object;

public static class StaticSkillSampleBuilder
{
    private static string Hash(string file)
    {
        using var stream = File.OpenRead(file); using var sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }
    public static object Build(string planPath, string output, string reportPath)
    {
        if (EditorApplication.isPlaying || EditorApplication.isCompiling) throw new InvalidOperationException("Editor must be idle.");
        output = output.Replace('\\', '/').TrimEnd('/');
        if (!output.StartsWith("Assets/RenderDocShaderSamples/HSR-4.6/", StringComparison.Ordinal) || output.Split('/').Contains(".."))
            throw new InvalidDataException("Use an isolated versioned output.");
        var plan = JObject.Parse(File.ReadAllText(planPath));
        if ((string)plan["schema"] != "my-hook.skill-preview.v1" || Hash((string)plan["clip"]) != (string)plan["clipSha256"] ||
            Hash((string)plan["sourceEvidence"]) != (string)plan["sourceEvidenceSha256"])
            throw new InvalidDataException("Plan/source evidence changed.");
        if (Directory.Exists(output))
        {
            if (!File.Exists(reportPath)) throw new InvalidDataException("Existing output has no ownership report.");
            var old = JObject.Parse(File.ReadAllText(reportPath));
            var known = ((JObject)old["ownedAssets"]).Properties().ToDictionary(p => p.Name, p => (string)p.Value);
            foreach (var pair in known)
                if (!File.Exists(pair.Key) || Hash(pair.Key) != pair.Value) throw new InvalidDataException("Output edited: " + pair.Key);
            foreach (string file in Directory.GetFiles(output, "*", SearchOption.AllDirectories))
                if ((file.Replace('\\','/').StartsWith(output+"/_Preview/")||file.Replace('\\','/').StartsWith(output+"/Animations/"))&&!known.ContainsKey(file.Replace('\\', '/'))) throw new InvalidDataException("Unowned output: " + file);
        }
        if (Resources.FindObjectsOfTypeAll<Object>().Any(o => EditorUtility.IsPersistent(o) && EditorUtility.IsDirty(o) && AssetDatabase.GetAssetPath(o).StartsWith("Assets/")))
            throw new InvalidOperationException("Persistent assets have unsaved edits.");
        var prefab = AssetDatabase.LoadAssetAtPath<GameObject>((string)plan["characterPrefab"]);
        if (prefab == null) throw new InvalidDataException("Verified native character prefab absent.");
        string prefabHash = Hash((string)plan["characterPrefab"]);
        Directory.CreateDirectory(output + "/Animations"); Directory.CreateDirectory(output + "/_Preview");
        string clipPath = output + "/Animations/" + Path.GetFileName((string)plan["clip"]);
        File.Copy((string)plan["clip"], clipPath, true); AssetDatabase.ImportAsset(clipPath, ImportAssetOptions.ForceSynchronousImport);
        var clip = AssetDatabase.LoadAssetAtPath<AnimationClip>(clipPath);
        if (clip == null || clip.legacy) throw new InvalidDataException("Expected nonlegacy source motion.");
        var previous = SceneManager.GetActiveScene();
        ulong occupied = SceneCullingMasks.DefaultSceneCullingMask;
        for (int i = 0; i < SceneManager.sceneCount; i++) occupied |= EditorSceneManager.GetSceneCullingMask(SceneManager.GetSceneAt(i));
        ulong mask = Enumerable.Range(0, 64).Select(i => 1UL << i).First(x => (occupied & x) == 0);
        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Additive);
        var frames = new JArray(); var report = new JObject();
        try
        {
            SceneManager.SetActiveScene(scene); EditorSceneManager.SetSceneCullingMask(scene, mask);
            var root = new GameObject("Hyacine Skill01 - static source preview");
            var actor = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene); actor.transform.SetParent(root.transform, false);
            foreach (var idle in actor.GetComponentsInChildren<StaticCharacterMotionPreview>(true)) Object.DestroyImmediate(idle);
            var bindings = AnimationUtility.GetCurveBindings(clip);
            var missing = bindings.Where(b => b.type == typeof(Transform) && b.path != "" && actor.transform.Find(b.path) == null).Select(b => b.path).Distinct().ToArray();
            if (missing.Length != 0) throw new InvalidDataException("Unresolved animation paths: " + string.Join(",", missing.Take(10)));
            var target = new GameObject("Stationary target fixture - not recovered game placement"); target.transform.SetParent(root.transform, false);
            var pos = plan["targetFixture"]["position"]; target.transform.localPosition = new Vector3((float)pos[0], (float)pos[1], (float)pos[2]);
            var clock = root.AddComponent<StaticSkillSequencePreview>(); clock.animate = false; clock.actor = actor; clock.clip = clip;
            clock.cycleDuration = clip.length + 0.6f;
            var effects = new System.Collections.Generic.List<StaticSkillSequencePreview.Effect>();
            foreach (JObject effect in (JArray)plan["effects"])
            {
                var asset = AssetDatabase.LoadAssetAtPath<GameObject>((string)effect["prefab"]);
                if (asset == null || Hash((string)effect["prefab"]) != (string)effect["prefabSha256"]) throw new InvalidDataException("Effect prefab changed.");
                if ((string)effect["targetAlias"] != "AbilityTargetEntity") throw new InvalidDataException("Unimplemented target semantics, do not guess a parent.");
                var item = (GameObject)PrefabUtility.InstantiatePrefab(asset, scene); item.transform.SetParent(target.transform, false);
                // Explicit source-field placement adapter; original follow enums/runtime spawn code are not recovered.
                var local=effect["sourceMonoEffect"]["parameters"]["LocalOffset"];var world=effect["sourceMonoEffect"]["parameters"]["WorldOffset"];
                item.transform.localPosition=new Vector3((float)local["X"]+(float)world["X"],(float)local["Y"]+(float)world["Y"],(float)local["Z"]+(float)world["Z"]);
                var rotation=effect["sourceMonoEffect"]["parameters"]["LocalRotationOffset"];
                item.transform.localRotation=Quaternion.Euler((float)rotation["X"],(float)rotation["Y"],(float)rotation["Z"]);
                foreach (var old in item.GetComponentsInChildren<StaticBattleEffectPreview>(true)) Object.DestroyImmediate(old);
                var systems = item.GetComponentsInChildren<ParticleSystem>(true);
                for (int i = 0; i < systems.Length; i++)
                    if (systems[i].useAutoRandomSeed) { systems[i].useAutoRandomSeed = false; systems[i].randomSeed = 13217 + (uint)i; }
                float delay=(float)effect["delay"]+(float)effect["sourceMonoEffect"]["parameters"]["Delay"];
                float life=(float)effect["sourceMonoEffect"]["parameters"]["MaxLifeTime"];
                effects.Add(new StaticSkillSequencePreview.Effect { root = item, delay = delay, lifeTime=life, systems = systems });
                clock.cycleDuration=Mathf.Max(clock.cycleDuration,delay+life+0.5f);
            }
            clock.effects = effects.ToArray(); clock.Sample(0);
            var camera = new GameObject("Skill overview - fixture camera").AddComponent<Camera>(); camera.enabled = false;
            camera.orthographic = true; camera.orthographicSize = 1.65f; camera.nearClipPlane = 0.01f; camera.farClipPlane = 100;
            camera.transform.position = new Vector3(4, 2.5f, 5); camera.transform.LookAt(new Vector3(0, 0.9f, 1));
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.08f, 0.09f, 0.12f);
            camera.overrideSceneCullingMask = mask;
            foreach (float time in ((JArray)plan["previewTimes"]).Values<float>())
            {
                clock.Sample(time);
                string image = Path.Combine(Path.GetDirectoryName(reportPath), "skill-t" + time.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ".png");
                var rt = new RenderTexture(800, 600, 24); var prior = RenderTexture.active;
                try
                {
                    camera.targetTexture = rt; camera.Render(); RenderTexture.active = rt;
                    var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBA32, false);
                    try { tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0); tex.Apply(); File.WriteAllBytes(image, tex.EncodeToPNG()); }
                    finally { Object.DestroyImmediate(tex); }
                }
                finally { camera.targetTexture = null; RenderTexture.active = prior; rt.Release(); Object.DestroyImmediate(rt); }
                frames.Add(new JObject { ["time"] = time, ["image"] = image, ["activeEffects"] = clock.effects.Count(e => e.root.activeSelf),
                    ["aliveParticles"] = clock.effects.Sum(e => e.systems.Sum(ps => ps.particleCount)) });
            }
            clock.Sample(0); clock.animate = true; camera.enabled = true; camera.overrideSceneCullingMask = 0;
            string prefabPath = output + "/_Preview/Skill01Preview.prefab", scenePath = output + "/_Preview/Skill01Preview.unity";
            PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
            EditorSceneManager.SetSceneCullingMask(scene, SceneCullingMasks.DefaultSceneCullingMask);
            EditorSceneManager.SaveScene(scene, scenePath);
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            var owned = new JObject(); foreach (string file in Directory.GetFiles(output, "*", SearchOption.AllDirectories))
                if(file.Replace('\\','/').StartsWith(output+"/_Preview/")||file.Replace('\\','/').StartsWith(output+"/Animations/"))owned[file.Replace('\\', '/')] = Hash(file);
            if (Hash((string)plan["characterPrefab"]) != prefabHash) throw new InvalidDataException("Source character was modified.");
            report = new JObject { ["status"] = "bounded_skill_fixture_built_not_full_game_reproduction", ["scene"] = scenePath, ["prefab"] = prefabPath,
                ["clipLength"] = clip.length, ["transformBindings"] = bindings.Count(b => b.type == typeof(Transform)), ["unresolvedTransformBindings"] = missing.Length,
                ["effectCount"] = clock.effects.Length, ["unresolvedSourceEffects"] = plan["unresolvedEffects"], ["frames"] = frames,
                ["sourceCharacterUnchanged"] = true, ["sourceCharacterSha256"] = prefabHash, ["limitations"] = plan["limitations"], ["ownedAssets"] = owned };
            File.WriteAllText(reportPath, report.ToString());
        }
        finally
        {
            if (previous.IsValid()) SceneManager.SetActiveScene(previous);
            EditorSceneManager.CloseScene(scene, true);
        }
        return new { status = (string)report["status"], scene = (string)report["scene"], effects = (int)report["effectCount"], frames = frames.Count };
    }
}
