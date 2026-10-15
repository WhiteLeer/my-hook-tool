using System;
using UnityEngine;

// A deterministic preview scheduler, not a replacement for the game's ability runtime.
[ExecuteAlways]
public sealed class StaticSkillSequencePreview : MonoBehaviour
{
    [Serializable] public sealed class Effect
    {
        public GameObject root;
        public float delay;
        public float lifeTime;
        public ParticleSystem[] systems;
    }
    public GameObject actor;
    public AnimationClip clip;
    public Effect[] effects = Array.Empty<Effect>();
    public bool animate = true;
    public float sampleTime;
    public float cycleDuration = 3.4f;
    public float CurrentSampleTime { get; private set; }
    private double epoch, nextTick;

    private void OnEnable()
    {
        epoch = Time.realtimeSinceStartupAsDouble;
        foreach (var renderer in GetComponentsInChildren<SkinnedMeshRenderer>(true))
            renderer.forceMatrixRecalculationPerRender = true;
        Sample(sampleTime);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.update -= EditorTick;
        UnityEditor.EditorApplication.update += EditorTick;
#endif
    }
    private void OnDisable()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.update -= EditorTick;
#endif
    }
#if UNITY_EDITOR
    private void EditorTick()
    {
        if (Application.isPlaying || UnityEditor.EditorApplication.isCompiling) return;
        Tick();
    }
#endif
    private void Update() { if (Application.isPlaying) Tick(); }
    private void Tick()
    {
        double now = Time.realtimeSinceStartupAsDouble;
        if (!animate || now < nextTick) return;
        nextTick = now + 1.0 / 30.0;
        Sample((float)((now - epoch) % Math.Max(cycleDuration, 0.01f)));
#if UNITY_EDITOR
        UnityEditor.EditorApplication.QueuePlayerLoopUpdate();
        UnityEditor.SceneView.RepaintAll();
#endif
    }
    public void Sample(float time)
    {
        CurrentSampleTime = time;
        if (actor != null && clip != null) clip.SampleAnimation(actor, Mathf.Clamp(time, 0, clip.length));
        foreach (var effect in effects)
        {
            if (effect.root == null) continue;
            bool started = time >= effect.delay && time < effect.delay+effect.lifeTime;
            effect.root.SetActive(started);
            if (!started) continue;
            StaticParticlePreviewClock.Sample(effect.systems,time-effect.delay,time,13217);
        }
    }
}
