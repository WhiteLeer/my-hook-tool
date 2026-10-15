using UnityEngine;

// Display adapter only: authored renderers stay disabled, as in the UI source.
// This is not a reconstruction of Coffee.UIParticle, SmoothMask or Canvas layout.
[ExecuteAlways]
public sealed class StaticUiParticlePreview : MonoBehaviour
{
    public Camera previewCamera;
    public ParticleSystem[] systems;
    public float sampleTime = 2f;
    public uint previewSeed = 13217;
    public bool animate;
    private Mesh[] meshes;
    private MeshRenderer[] displays;
    private double epoch;

    private void OnEnable()
    {
        epoch = Time.realtimeSinceStartupAsDouble;
        EnsureDisplays();
        Sample(sampleTime);
    }

    private void Update()
    {
        if (Application.isPlaying || animate) Sample((float)((Time.realtimeSinceStartupAsDouble - epoch) % 5.0));
    }

    public void Sample(float time)
    {
        EnsureDisplays();
        if (previewCamera == null || systems == null) return;
        for (int i = 0; i < systems.Length; i++)
        {
            var ps = systems[i];
            if (ps == null) continue;
            ps.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            // Determinism belongs to the preview instance, not the source prefab.
            ps.useAutoRandomSeed = false;
            ps.randomSeed = previewSeed + (uint)i;
            ps.Simulate(time, false, true, true);
            var source = ps.GetComponent<ParticleSystemRenderer>();
            source.BakeMesh(meshes[i], previewCamera, ParticleSystemBakeMeshOptions.Default);
            displays[i].sharedMaterials = source.sharedMaterials;
            displays[i].sortingOrder = source.sortingOrder;
        }
    }

    private void EnsureDisplays()
    {
        if (systems == null || previewCamera == null) return;
        if (meshes != null && meshes.Length == systems.Length && displays != null
            && displays.Length == systems.Length && System.Array.TrueForAll(displays, d => d != null)) return;
        // Serialized references can be restored after OnEnable during scene/domain reload.
        OnDisable();
        meshes = new Mesh[systems.Length];
        displays = new MeshRenderer[systems.Length];
        for (int i = 0; i < systems.Length; i++)
        {
            var child = new GameObject("_PreviewBakedGeometry", typeof(MeshFilter), typeof(MeshRenderer));
            child.hideFlags = HideFlags.HideAndDontSave;
            child.layer = systems[i].gameObject.layer;
            child.transform.SetParent(systems[i].transform, false);
            meshes[i] = new Mesh { name = "Transient UI particle mesh", hideFlags = HideFlags.HideAndDontSave };
            child.GetComponent<MeshFilter>().sharedMesh = meshes[i];
            displays[i] = child.GetComponent<MeshRenderer>();
            displays[i].shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            displays[i].receiveShadows = false;
        }
    }

    private void OnDisable()
    {
        if (meshes == null) return;
        for (int i = 0; i < meshes.Length; i++)
        {
            if (displays != null && i < displays.Length && displays[i] != null) Dispose(displays[i].gameObject);
            if (meshes[i] != null) Dispose(meshes[i]);
        }
        meshes = null;
        displays = null;
    }

    private static void Dispose(Object obj)
    {
        if (Application.isPlaying) Destroy(obj);
        else DestroyImmediate(obj);
    }
}
