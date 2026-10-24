using UnityEngine;

// Added only to wizard basic projectiles; keeps all visual work off shared projectile owners.
public sealed class WizardProjectileVisual : MonoBehaviour
{
    private const int SourceParticleLimit = 32;
    private const int CoreParticleLimit = 2;
    private const int HitParticleLimit = 24;
    private const float HitParticleLifetime = 0.2f;
    private const float HitSparkCleanupDelay = 0.35f;

    private static readonly Gradient BasicPurple = CreateGradient(new Color(0.64f, 0.38f, 1f));
    private static readonly Gradient BasicCyan = CreateGradient(new Color(0.28f, 0.88f, 1f));
    private static readonly Gradient EmpoweredPurple = CreateGradient(new Color(0.76f, 0.5f, 1f));
    private static readonly Gradient EmpoweredCyan = CreateGradient(new Color(0.42f, 0.96f, 1f));
    private static readonly Gradient BasicFlame = CreateGradient(new Color(0.64f, 0.42f, 1f), 0.2f);
    private static readonly Gradient EmpoweredFlame = CreateGradient(new Color(0.76f, 0.52f, 1f), 0.3f);
    private static readonly Gradient BasicGlow = CreateGradient(new Color(0.36f, 0.78f, 1f), 0.12f);
    private static readonly Gradient EmpoweredGlow = CreateGradient(new Color(0.46f, 0.88f, 1f), 0.18f);
    private static readonly Gradient BasicDust = CreateGradient(new Color(0.28f, 0.82f, 1f), 0.42f);
    private static readonly Gradient EmpoweredDust = CreateGradient(new Color(0.38f, 0.9f, 1f), 0.52f);

    private TrailRenderer trail;
    private ParticleSystem hitSpark;
    private bool empowered;
    private bool configured;
    private bool hitSparkReleased;

    public void Configure(bool empoweredAttack)
    {
        if (configured)
            return;

        configured = true;
        empowered = empoweredAttack;

        ParticleSystem[] sourceParticles = GetComponentsInChildren<ParticleSystem>(true);
        ParticleSystemRenderer[] sourceRenderers = GetComponentsInChildren<ParticleSystemRenderer>(true);
        Material glowMaterial = FindGlowMaterial(sourceRenderers);
        Material fallbackMaterial = FindAnyMaterial(sourceRenderers);

        for (int i = 0; i < sourceParticles.Length; i++)
        {
            ParticleSystem particle = sourceParticles[i];
            particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);

            var main = particle.main;
            main.maxParticles = Mathf.Min(main.maxParticles, SourceParticleLimit);
            main.startColor = Color.white;

            bool sourceFlame = particle.gameObject == gameObject;
            bool cyanGlow = particle.gameObject.name == "glow";
            bool cyanDust = particle.gameObject.name == "dust";
            if (sourceFlame)
            {
                main.startSizeMultiplier *= empowered ? 0.38f : 0.3f;
                main.startLifetimeMultiplier *= empowered ? 0.62f : 0.52f;
            }
            else if (cyanGlow)
            {
                main.startSizeMultiplier *= empowered ? 0.22f : 0.18f;
                main.startLifetimeMultiplier *= 0.48f;
            }
            else if (cyanDust)
            {
                main.startSizeMultiplier *= 0.8f;
                main.startLifetimeMultiplier *= 0.6f;
                var emission = particle.emission;
                emission.rateOverTimeMultiplier *= 0.35f;
            }

            var colorOverLifetime = particle.colorOverLifetime;
            Gradient sourceGradient = sourceFlame
                ? (empowered ? EmpoweredFlame : BasicFlame)
                : cyanGlow
                    ? (empowered ? EmpoweredGlow : BasicGlow)
                    : cyanDust
                        ? (empowered ? EmpoweredDust : BasicDust)
                        : GetColorGradient(false);
            colorOverLifetime.color = new ParticleSystem.MinMaxGradient(sourceGradient);
            colorOverLifetime.enabled = true;

            particle.Play(false);
        }

        if (glowMaterial == null)
            glowMaterial = fallbackMaterial;

        CreateCore(glowMaterial);
        CreateTrail(glowMaterial);
        CreateHitSpark(glowMaterial);
    }

    // Called only after DamageProjectile accepts a new, living enemy hit.
    public void OnSuccessfulHit(Vector3 hitPosition, bool projectileFinishes = false)
    {
        if (hitSpark == null || hitSparkReleased)
            return;

        hitSpark.Play(false);

        Vector3 forward = transform.forward;
        Vector3 right = Vector3.Cross(forward, Vector3.up);
        if (right.sqrMagnitude < 0.001f)
            right = Vector3.Cross(forward, Vector3.right);
        right.Normalize();
        Vector3 up = Vector3.Cross(right, forward).normalized;

        Color purple = empowered ? new Color(0.78f, 0.52f, 1f) : new Color(0.64f, 0.4f, 1f);
        Color cyan = empowered ? new Color(0.5f, 0.98f, 1f) : new Color(0.32f, 0.9f, 1f);
        EmitSpark(hitPosition, forward * 0.9f, cyan);
        EmitSpark(hitPosition, -forward * 0.3f + right * 0.7f, purple);
        EmitSpark(hitPosition, -forward * 0.3f - right * 0.7f, cyan);
        EmitSpark(hitPosition, up * 0.65f + right * 0.35f, purple);
        EmitSpark(hitPosition, -up * 0.65f - right * 0.35f, cyan);

        if (projectileFinishes)
            ReleaseHitSpark();
    }

    private void OnDisable()
    {
        if (trail != null)
        {
            trail.emitting = false;
            trail.Clear();
        }

        if (!hitSparkReleased && hitSpark != null)
            hitSpark.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
    }

    private Gradient GetColorGradient(bool cyanAccent)
    {
        if (empowered)
            return cyanAccent ? EmpoweredCyan : EmpoweredPurple;

        return cyanAccent ? BasicCyan : BasicPurple;
    }

    private void CreateCore(Material material)
    {
        if (material == null)
            return;

        GameObject coreObject = new GameObject("WizardProjectileCore");
        coreObject.transform.SetParent(transform, false);

        ParticleSystem core = coreObject.AddComponent<ParticleSystem>();
        core.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = core.main;
        main.playOnAwake = false;
        main.loop = false;
        main.duration = 100f;
        main.startLifetime = 100f;
        main.startSpeed = 0f;
        main.startSize = 0.22f;
        main.simulationSpace = ParticleSystemSimulationSpace.Local;
        main.maxParticles = CoreParticleLimit;

        var emission = core.emission;
        emission.enabled = false;
        var shape = core.shape;
        shape.enabled = false;

        ParticleSystemRenderer coreRenderer = core.GetComponent<ParticleSystemRenderer>();
        coreRenderer.sharedMaterial = material;
        coreRenderer.renderMode = ParticleSystemRenderMode.Billboard;

        core.Play(false);
        ParticleSystem.EmitParams particle = new ParticleSystem.EmitParams
        {
            position = Vector3.zero,
            velocity = Vector3.zero,
            startLifetime = 100f,
            startColor = empowered ? EmpoweredPurple.Evaluate(0.5f) : BasicPurple.Evaluate(0.5f),
            startSize = empowered ? 0.25f : 0.21f
        };
        core.Emit(particle, 1);
        particle.startColor = empowered ? EmpoweredCyan.Evaluate(0.5f) : BasicCyan.Evaluate(0.5f);
        particle.startSize = empowered ? 0.13f : 0.11f;
        core.Emit(particle, 1);
    }

    private void CreateTrail(Material material)
    {
        if (material == null)
            return;

        GameObject trailObject = new GameObject("WizardProjectileTrail");
        trailObject.transform.SetParent(transform, false);
        trail = trailObject.AddComponent<TrailRenderer>();
        trail.sharedMaterial = material;
        trail.alignment = LineAlignment.View;
        trail.textureMode = LineTextureMode.Stretch;
        trail.numCornerVertices = 2;
        trail.numCapVertices = 2;
        trail.minVertexDistance = 0.04f;
        trail.time = empowered ? 0.2f : 0.14f;
        trail.startWidth = empowered ? 0.2f : 0.15f;
        trail.endWidth = 0.015f;
        trail.startColor = empowered ? new Color(0.7f, 0.95f, 1f, 0.88f) : new Color(0.62f, 0.82f, 1f, 0.78f);
        trail.endColor = new Color(0.56f, 0.32f, 1f, 0f);
    }

    private void CreateHitSpark(Material material)
    {
        GameObject sparkObject = new GameObject("WizardProjectileHitSpark");
        sparkObject.transform.SetParent(transform, false);

        hitSpark = sparkObject.AddComponent<ParticleSystem>();
        hitSpark.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
        var main = hitSpark.main;
        main.playOnAwake = false;
        main.loop = false;
        main.duration = HitParticleLifetime;
        main.startLifetime = HitParticleLifetime;
        main.startSpeed = 0f;
        main.startSize = empowered ? 0.09f : 0.075f;
        main.simulationSpace = ParticleSystemSimulationSpace.World;
        main.maxParticles = HitParticleLimit;

        var emission = hitSpark.emission;
        emission.enabled = false;
        var shape = hitSpark.shape;
        shape.enabled = false;

        ParticleSystemRenderer sparkRenderer = hitSpark.GetComponent<ParticleSystemRenderer>();
        sparkRenderer.renderMode = ParticleSystemRenderMode.Billboard;
        if (material != null)
            sparkRenderer.sharedMaterial = material;
    }

    private void EmitSpark(Vector3 position, Vector3 velocity, Color color)
    {
        ParticleSystem.EmitParams particle = new ParticleSystem.EmitParams
        {
            applyShapeToPosition = false,
            position = position,
            velocity = velocity,
            startColor = color,
            startLifetime = HitParticleLifetime,
            startSize = empowered ? 0.09f : 0.075f
        };
        hitSpark.Emit(particle, 1);
    }

    private void ReleaseHitSpark()
    {
        if (hitSpark == null || hitSparkReleased)
            return;

        hitSparkReleased = true;
        hitSpark.transform.SetParent(null, true);
        Destroy(hitSpark.gameObject, HitSparkCleanupDelay);
    }

    private static Material FindGlowMaterial(ParticleSystemRenderer[] renderers)
    {
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i].gameObject.name == "glow" && renderers[i].sharedMaterial != null)
                return renderers[i].sharedMaterial;
        }

        return null;
    }

    private static Material FindAnyMaterial(ParticleSystemRenderer[] renderers)
    {
        for (int i = 0; i < renderers.Length; i++)
        {
            if (renderers[i].sharedMaterial != null)
                return renderers[i].sharedMaterial;
        }

        return null;
    }

    private static Gradient CreateGradient(Color color)
    {
        return CreateGradient(color, 1f);
    }

    private static Gradient CreateGradient(Color color, float maxAlpha)
    {
        Gradient gradient = new Gradient();
        gradient.SetKeys(
            new[]
            {
                new GradientColorKey(color, 0f),
                new GradientColorKey(color, 1f)
            },
            new[]
            {
                new GradientAlphaKey(0f, 0f),
                new GradientAlphaKey(maxAlpha, 0.15f),
                new GradientAlphaKey(maxAlpha, 0.78f),
                new GradientAlphaKey(0f, 1f)
            });
        return gradient;
    }
}
