using UnityEngine;

// Isolated clip sampling; this does not reconstruct the game's Animator state machine.
[ExecuteAlways]
public sealed class StaticCharacterMotionPreview : MonoBehaviour
{
    public GameObject model;
    public AnimationClip clip;
    public float sampleTime = 1;
    public bool animate = true;
    public float CurrentSampleTime { get; private set; }
    private double epoch;

    private void OnEnable()
    {
        foreach(var renderer in GetComponentsInChildren<SkinnedMeshRenderer>(true))renderer.forceMatrixRecalculationPerRender=true;
        epoch=Time.realtimeSinceStartupAsDouble;Sample(sampleTime);
#if UNITY_EDITOR
        UnityEditor.EditorApplication.update-=EditorTick;
        UnityEditor.EditorApplication.update+=EditorTick;
#endif
    }
    private void OnDisable()
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.update-=EditorTick;
#endif
    }
#if UNITY_EDITOR
    private void EditorTick()
    {
        if(Application.isPlaying||!animate||UnityEditor.EditorApplication.isCompiling)return;
        Tick();UnityEditor.EditorApplication.QueuePlayerLoopUpdate();UnityEditor.SceneView.RepaintAll();
    }
#endif
    private void Update()
    {
        if(Application.isPlaying&&animate)Tick();
    }
    private void Tick()
    {
        if(clip!=null)
            Sample((float)((Time.realtimeSinceStartupAsDouble-epoch)%Mathf.Max(clip.length,0.01f)));
    }
    public void Sample(float time)
    {
        if(model!=null && clip!=null){clip.SampleAnimation(model,time);CurrentSampleTime=time;}
    }
}
