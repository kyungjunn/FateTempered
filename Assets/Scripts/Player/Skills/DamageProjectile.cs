// Shared flight with piercing direct hits or a one-shot area explosion.
using System;
using System.Collections.Generic;
using Unity.Netcode;
using Unity.Netcode.Components;
using UnityEngine;

[RequireComponent(typeof(Rigidbody), typeof(CapsuleCollider))]
public sealed class DamageProjectile : MonoBehaviour
{
    [Header("Launch height correction")]
    [SerializeField, Range(0f, 30f)] private float horizontalAimAngle = 8f;
    [SerializeField, Range(0f, 60f)] private float verticalAimAngle = 35f;
    private static readonly ProjectileHeightAim HeightAim = new ProjectileHeightAim();

    private PlayerDamageDealer damageDealer;
    private PlayerSkillController skillOwner;
    private NetworkObject networkObject;
    private NetworkProjectile networkProjectile;
    private PlayerDamageType damageType;
    private Vector3 spawnPosition;
    private float maxDistance;
    private float moveSpeed;
    private int damage;
    private Vector3 moveDirection;
    private Rigidbody body;
    private bool initialized;
    private float explosionRadius;
    private ParticleSystem[] particles;
    private WizardProjectileVisual wizardProjectileVisual;
    private int remainingPierces;
    private readonly HashSet<EnemyHealth> directHits = new HashSet<EnemyHealth>();
    private readonly SkillAreaQuery explosionQuery = new SkillAreaQuery();

    // 발사 설정
    public void Initialize(PlayerDamageDealer dealer, PlayerDamageType type, int value,
        float speed, float distance, Vector3 direction, float blastRadius = 0f, float visualScale = 1f,
        int additionalPierces = 0, bool empoweredWizardAttack = false)
    {
        NetworkObject dealerNetworkObject = dealer != null ? dealer.GetComponentInParent<NetworkObject>() : null;
        bool dealerIsNetworkSpawned = dealerNetworkObject != null && dealerNetworkObject.IsSpawned;
        NetworkManager networkManager = dealerIsNetworkSpawned
            ? dealerNetworkObject.NetworkManager
            : null;
        bool networkedShot = dealerIsNetworkSpawned;
        NetworkObject projectileNetworkObject = null;
        NetworkProjectile projectileNetwork = null;

        if (networkedShot)
        {
            if (networkManager == null || !networkManager.IsListening || !networkManager.IsServer)
                FailNetworkedInitialization("Initialize must run on the server for a spawned dealer.");

            projectileNetworkObject = GetComponent<NetworkObject>();
            NetworkTransform networkTransform = GetComponent<NetworkTransform>();
            projectileNetwork = GetComponent<NetworkProjectile>();
            if (projectileNetworkObject == null || networkTransform == null || projectileNetwork == null)
                FailNetworkedInitialization(
                    "the projectile root must have NetworkObject, server-authoritative NetworkTransform, and NetworkProjectile components.");
            if (networkTransform.AuthorityMode != NetworkTransform.AuthorityModes.Server ||
                !networkTransform.IsServerAuthoritative())
                FailNetworkedInitialization("the projectile NetworkTransform must be server-authoritative.");
            if (projectileNetworkObject.NetworkManager != networkManager)
                FailNetworkedInitialization("the projectile and dealer must use the same NetworkManager.");
        }

        damageDealer = dealer;
        if (skillOwner != null)
            skillOwner.ActiveSkillsCancelled -= CancelEndedSession;
        skillOwner = dealer != null ? dealer.GetComponent<PlayerSkillController>() : null;
        if (skillOwner != null)
            skillOwner.ActiveSkillsCancelled += CancelEndedSession;
        networkObject = projectileNetworkObject;
        networkProjectile = projectileNetwork;
        damageType = type;
        damage = Mathf.Max(1, value);
        maxDistance = Mathf.Max(0.1f, distance);
        moveSpeed = Mathf.Max(0f, speed);
        moveDirection = HeightAim.Resolve(transform.position, direction, maxDistance,
            dealer != null ? dealer.transform : null, horizontalAimAngle, verticalAimAngle);
        if (direction.sqrMagnitude > 0.001f)
            transform.rotation = Quaternion.FromToRotation(direction.normalized, moveDirection) * transform.rotation;
        spawnPosition = transform.position;
        explosionRadius = Mathf.Max(0f, blastRadius);
        remainingPierces = Mathf.Max(0, additionalPierces);
        directHits.Clear();
        wizardProjectileVisual = GetComponent<WizardProjectileVisual>();
        if (networkedShot)
            wizardProjectileVisual?.Configure(empoweredWizardAttack);
        // Scale the artwork, not the flight collider: a large R must not hit early.
        ApplyVisualScale(visualScale);
        if (explosionRadius > 0f || !Mathf.Approximately(visualScale, 1f))
        {
            particles = GetComponentsInChildren<ParticleSystem>(true);
            SetParticleHierarchyScaling(particles);
        }
        initialized = true;

        body = GetComponent<Rigidbody>();
        body.useGravity = false;
        body.isKinematic = true;
        body.collisionDetectionMode = CollisionDetectionMode.ContinuousSpeculative;

        if (networkedShot)
        {
            try
            {
                networkProjectile.PrepareForSpawn(networkManager, visualScale,
                    wizardProjectileVisual != null, empoweredWizardAttack, explosionRadius > 0f);
                projectileNetworkObject.Spawn(true);
            }
            catch (Exception exception)
            {
                initialized = false;
                Destroy(gameObject);
                throw new InvalidOperationException(
                    $"Failed to spawn network projectile '{name}'. Check that its prefab is registered with the NetworkManager.",
                    exception);
            }

            if (!projectileNetworkObject.IsSpawned)
            {
                initialized = false;
                Destroy(gameObject);
                throw new InvalidOperationException(
                    $"Failed to spawn network projectile '{name}'. Check that its prefab is registered with the NetworkManager.");
            }
        }
    }

    private void CancelEndedSession()
    {
        // Session cancellation never uses Finish: an explosive projectile must not detonate.
        if (skillOwner == null || !skillOwner.IsSessionEnded || !HasServerSimulationAuthority())
            return;

        initialized = false;
        if (networkObject != null && networkObject.IsSpawned)
            networkProjectile.Despawn();
        else
            Destroy(gameObject);
    }

    private void OnDestroy()
    {
        if (skillOwner != null)
            skillOwner.ActiveSkillsCancelled -= CancelEndedSession;
    }

    // 이동 / 사거리 종료
    private void FixedUpdate()
    {
        if (!initialized || !HasServerSimulationAuthority())
            return;

        float remaining = maxDistance - Vector3.Distance(body.position, spawnPosition);
        if (remaining <= 0.001f)
        {
            Finish(null, Vector3.zero);
            return;
        }

        float step = Mathf.Min(moveSpeed * Time.fixedDeltaTime, remaining);
        body.MovePosition(body.position + moveDirection * step);
    }

    // 적 적중
    private void OnTriggerEnter(Collider other)
    {
        if (!initialized || !HasServerSimulationAuthority() || damageDealer == null ||
            other.transform.root == damageDealer.transform)
            return;

        EnemyHealth enemy = other.GetComponentInParent<EnemyHealth>();
        if (enemy == null || enemy.IsDead || !directHits.Add(enemy))
            return;

        if (explosionRadius <= 0f && remainingPierces > 0)
        {
            remainingPierces--;
            damageDealer.DealDamage(enemy, damage, damageType, spawnPosition);
            Vector3 hitPosition = other.ClosestPoint(transform.position);
            wizardProjectileVisual?.OnSuccessfulHit(hitPosition);
            if (networkObject != null && networkObject.IsSpawned)
                networkProjectile.SendPiercingHitVisual(hitPosition);
            return;
        }

        Finish(enemy, other.ClosestPoint(transform.position));
    }

    private void Finish(EnemyHealth directTarget, Vector3 hitPosition)
    {
        if (!initialized)
            return;

        if (!HasServerSimulationAuthority())
            return;

        initialized = false;
        bool networkedProjectile = networkObject != null && networkObject.IsSpawned;
        if (explosionRadius <= 0f)
        {
            if (directTarget != null && damageDealer != null)
            {
                damageDealer.DealDamage(directTarget, damage, damageType, spawnPosition);
                wizardProjectileVisual?.OnSuccessfulHit(hitPosition, true);
                if (networkedProjectile)
                    networkProjectile.SendFinishVisual(hitPosition, true, false);
            }

            if (networkedProjectile)
                networkProjectile.Despawn();
            else
                Destroy(gameObject);
            return;
        }

        explosionQuery.DealDamage(damageDealer, body.position, explosionRadius, damage,
            damageType, spawnPosition, directTarget);
        if (directTarget != null)
            wizardProjectileVisual?.OnSuccessfulHit(hitPosition, true);

        PlayExplosionBurst();
        if (networkedProjectile)
        {
            networkProjectile.SendFinishVisual(hitPosition, directTarget != null, true);
            networkProjectile.DespawnAfter(0.6f);
        }
        else
        {
            Destroy(gameObject, 0.6f);
        }
    }

    internal void InitializeNetworkReplica(float replicatedVisualScale, bool hasWizardVisual,
        bool empoweredWizardAttack, bool hasExplosion)
    {
        networkObject = GetComponent<NetworkObject>();
        networkProjectile = GetComponent<NetworkProjectile>();

        if (hasWizardVisual)
        {
            wizardProjectileVisual = GetComponent<WizardProjectileVisual>();
            if (wizardProjectileVisual == null)
                wizardProjectileVisual = gameObject.AddComponent<WizardProjectileVisual>();
            wizardProjectileVisual.Configure(empoweredWizardAttack);
        }

        ApplyVisualScale(replicatedVisualScale);
        if (hasExplosion || !Mathf.Approximately(replicatedVisualScale, 1f))
        {
            particles = GetComponentsInChildren<ParticleSystem>(true);
            SetParticleHierarchyScaling(particles);
        }

        body = GetComponent<Rigidbody>();
        body.useGravity = false;
        body.isKinematic = true;
        body.detectCollisions = false;
        foreach (Collider hitbox in GetComponentsInChildren<Collider>(true))
            hitbox.enabled = false;
    }

    internal void PlayNetworkVisual(Vector3 hitPosition, bool hasHit, bool projectileFinishes, bool explodes)
    {
        if (hasHit)
            wizardProjectileVisual?.OnSuccessfulHit(hitPosition, projectileFinishes);

        if (explodes)
            PlayExplosionBurst();
    }

    private void PlayExplosionBurst()
    {
        foreach (Collider hitbox in GetComponentsInChildren<Collider>())
            hitbox.enabled = false;

        if (particles == null)
            particles = GetComponentsInChildren<ParticleSystem>(true);

        // Reuse the projectile's particles as a short, stationary burst.
        foreach (ParticleSystem particle in particles)
        {
            particle.Stop(false, ParticleSystemStopBehavior.StopEmittingAndClear);
            var main = particle.main;
            main.loop = false;
            main.startLifetime = 0.4f;
            main.startSpeed = 3f;
            var emission = particle.emission;
            emission.enabled = false;
            particle.Play(false);
            particle.Emit(24);
        }
    }

    private void ApplyVisualScale(float visualScale)
    {
        float scale = Mathf.Max(0.1f, visualScale);
        foreach (Transform child in transform)
            child.localScale *= scale;
    }

    private static void SetParticleHierarchyScaling(ParticleSystem[] particleSystems)
    {
        foreach (ParticleSystem particle in particleSystems)
        {
            var main = particle.main;
            main.scalingMode = ParticleSystemScalingMode.Hierarchy;
        }
    }

    private bool HasServerSimulationAuthority()
    {
        if (networkObject == null)
            networkObject = GetComponent<NetworkObject>();

        return networkObject == null || !networkObject.IsSpawned ||
               (networkObject.NetworkManager != null && networkObject.NetworkManager.IsServer);
    }

    private void FailNetworkedInitialization(string reason)
    {
        initialized = false;
        Destroy(gameObject);
        throw new InvalidOperationException($"Cannot initialize networked projectile '{name}': {reason}");
    }

}
