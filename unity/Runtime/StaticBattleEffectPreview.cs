using UnityEngine;

// Preview-only clock. Authored fixed seeds are retained; auto-seeded previews are stabilized.
[ExecuteAlways]
public sealed class StaticBattleEffectPreview : MonoBehaviour
{
    public Camera previewCamera;
    public ParticleSystem[] systems;
    public float sampleTime = 0.08f;
    public float cycleDuration = 1.6f;
    public uint previewSeed = 13217;
    public bool animate = true;
    private double epoch;

    private void OnEnable()
    {
        epoch = Time.realtimeSinceStartupAsDouble;
        Sample(sampleTime);
    }

    private void Update()
    {
        if (Application.isPlaying || animate)
            Sample((float)((Time.realtimeSinceStartupAsDouble - epoch) % Mathf.Max(0.01f, cycleDuration)));
    }

    public void Sample(float time)
    {
        if (systems == null) return;
        StaticParticlePreviewClock.Sample(systems,time,time,previewSeed);
    }
}
