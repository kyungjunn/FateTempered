using UnityEngine;

public enum WizardSkillVisualRole
{
    GravityCore,
    GravityVortex,
    GravityImpact,
    ResonanceField,
    ResonanceImpact,
    OverdriveAura,
    OverdriveWave,
    OverdriveFinal
}

// Appearance belongs to the prefab; runtime owns playback and cleanup only.
public sealed class WizardSkillVisual : MonoBehaviour
{
    private ParticleSystem[] systems;
    private Material groundBoundaryMaterial;

    public void Initialize(bool continuous, bool followsOwner)
    {
        systems = GetComponentsInChildren<ParticleSystem>(true);
        foreach (ParticleSystem system in systems)
        {
            system.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = system.main;
            main.loop = continuous;
            main.playOnAwake = false;
            main.stopAction = ParticleSystemStopAction.None;
            main.useUnscaledTime = false;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
            if (followsOwner)
                main.simulationSpace = ParticleSystemSimulationSpace.Local;
            system.Play(false);
        }
    }

    internal void OwnGroundBoundaryMaterial(Material material)
    {
        groundBoundaryMaterial = material;
    }

    public bool IsAlive()
    {
        if (systems == null) return false;
        foreach (ParticleSystem system in systems)
            if (system != null && system.IsAlive(false)) return true;
        return false;
    }

    private void OnDestroy()
    {
        if (groundBoundaryMaterial != null)
        {
            if (Application.isPlaying)
                Destroy(groundBoundaryMaterial);
            else
                DestroyImmediate(groundBoundaryMaterial);
        }
    }
}
