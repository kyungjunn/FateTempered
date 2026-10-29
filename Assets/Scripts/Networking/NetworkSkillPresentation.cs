using System;
using System.Collections.Generic;
using DigitalRuby.PyroParticles;
using Unity.Cinemachine;
using Unity.Netcode;
using UnityEngine;
using UnityEngine.Rendering;

// 서버 효과 관찰·클라이언트 시각 복제 전용, 스킬 실행 제외
[RequireComponent(typeof(NetworkObject))]
public sealed class NetworkSkillPresentation : NetworkBehaviour
{
    // 서버·클라이언트 공통 순서의 효과·음향 카탈로그
    [SerializeField] private GameObject[] effectPrefabs = Array.Empty<GameObject>();
    [SerializeField] private AudioClip[] soundClips = Array.Empty<AudioClip>();
    // 효과·전송 배열 상한과 서버 관찰 주기
    private const int MaxEffects = 512;
    private const int MaxParticles = 62;
    private const int MaxPoints = 65;
    private const int MaxVolumes = 32;
    private const float SnapshotInterval = 0.05f;

    // 파티클 재생·방출 상태 묶음
    private sealed class ParticleSnapshot
    {
        // 비트: 방출 1·일시정지 2·정지 4·방출 활성 8·객체 활성 16·반복 32
        public byte Flags;
        // 재생 시각·시간당 및 거리당 방출 배율
        public float Time;
        public float Rate;
        public float DistanceRate;
    }

    // 피해·시전 로직 없는 효과 전송 상태
    private sealed class EffectSnapshot
    {
        // 효과 식별자·카탈로그 인덱스, 음수는 선·궤적
        public int Id;
        public int Prefab;
        // 지속형·소유자 추종·활성 상태
        public bool Continuous;
        public bool FollowsOwner;
        public bool Active;
        // 추종 시 소유자 상대 좌표, 그 외 월드 좌표
        public Vector3 Position;
        public Quaternion Rotation;
        public Vector3 Scale;
        // 파티클 상태·볼륨 활성값
        public readonly List<ParticleSnapshot> Particles = new List<ParticleSnapshot>();
        public readonly List<byte> Volumes = new List<byte>();
        // 선·궤적 표시 속성과 정점 목록
        public bool LineEnabled;
        public bool WorldSpace;
        public bool Loop;
        public float StartWidth;
        public float EndWidth;
        public Color StartColor;
        public Color EndColor;
        public readonly List<Vector3> Points = new List<Vector3>();
    }

    // 서버 관찰 원본·렌더러 참조·최근 캡처
    private sealed class Observed
    {
        public GameObject Object;
        public LineRenderer Line;
        public TrailRenderer Trail;
        public ParticleSystem[] Particles;
        public Volume[] Volumes;
        public EffectSnapshot State;
    }

    // 클라이언트 렌더링 전용 객체·해제 대상 재질
    private sealed class Replica
    {
        public GameObject Object;
        public LineRenderer Line;
        public ParticleSystem[] Particles;
        public Volume[] Volumes;
        public Material Material;
    }

    // 서버 관찰 목록·클라이언트 복제 목록·제거 작업 버퍼
    private readonly Dictionary<int, Observed> observed = new Dictionary<int, Observed>();
    private readonly Dictionary<int, Replica> replicas = new Dictionary<int, Replica>();
    private readonly List<int> removed = new List<int>();
    // 비활성 생성 공간·효과 ID·다음 관찰 시각
    private GameObject staging;
    private int nextId;
    private float nextSnapshot;
    // 현재 클라이언트 복제 객체 수
    public int ActiveReplicaCount => replicas.Count;

    // 관찰 목록·복제 객체·생성 공간 정리
    public override void OnNetworkDespawn()
    {
        foreach (Replica replica in replicas.Values) DestroyReplica(replica);
        replicas.Clear();
        observed.Clear();
        if (staging != null) Destroy(staging);
        staging = null;
    }

    // 소유자 상위 계층의 연출 동기화 구성 탐색
    private static NetworkSkillPresentation Find(GameObject owner) =>
        owner != null ? owner.GetComponentInParent<NetworkSkillPresentation>() : null;

    // 서버 프리팹 효과 등록·공통 카탈로그 검증
    public static void Track(GameObject owner, GameObject instance, GameObject prefab,
        bool continuous, bool followsOwner = false)
    {
        NetworkSkillPresentation presentation = Find(owner);
        if (presentation == null || !presentation.IsSpawned || !presentation.IsServer || instance == null) return;
        int index = Array.IndexOf(presentation.effectPrefabs, prefab);
        if (prefab == null || index < 0)
            throw new InvalidOperationException("NetworkSkillPresentation effect prefab is missing from the catalog: " +
                (prefab != null ? prefab.name : "null"));
        presentation.Add(instance, null, index, continuous, followsOwner);
    }

    // 서버 선 렌더러 관찰 등록
    public static void TrackLine(GameObject owner, LineRenderer line)
    {
        NetworkSkillPresentation presentation = Find(owner);
        if (presentation == null || !presentation.IsSpawned || !presentation.IsServer || line == null) return;
        presentation.Add(line.gameObject, line, -1, false, false);
    }

    // 서버 궤적 렌더러 관찰 등록
    public static void TrackTrail(GameObject owner, TrailRenderer trail)
    {
        var presentation = Find(owner);
        if (presentation == null || !presentation.IsSpawned || !presentation.IsServer || trail == null) return;
        presentation.Add(trail.gameObject, null, -1, false, false, trail);
    }

    // 중복 등록·구성 개수 검증 후 초기 상태 전송
    private void Add(GameObject instance, LineRenderer line, int prefab, bool continuous, bool followsOwner,
        TrailRenderer trail = null)
    {
        foreach (Observed entry in observed.Values)
            if (entry.Object == instance && entry.Line == line && entry.Trail == trail) return;
        if (observed.Count >= MaxEffects)
            throw new InvalidOperationException("NetworkSkillPresentation exceeded 512 simultaneous effects on " + name);
        var entryToAdd = new Observed
        {
            Object = instance,
            Line = line,
            Trail = trail,
            Particles = instance.GetComponentsInChildren<ParticleSystem>(true),
            Volumes = instance.GetComponentsInChildren<Volume>(true),
            State = new EffectSnapshot { Id = ++nextId, Prefab = prefab, Continuous = continuous, FollowsOwner = followsOwner }
        };
        if (entryToAdd.Particles.Length > MaxParticles || entryToAdd.Volumes.Length > MaxVolumes)
            throw new InvalidOperationException("Skill effect exceeds 62 particles or 32 volumes: " + instance.name);
        Capture(entryToAdd);
        observed.Add(entryToAdd.State.Id, entryToAdd);
        SendSnapshot(entryToAdd.State, true);
    }

    // 서버 주기적 캡처·전송·소멸 효과 제거 통지
    private void Update()
    {
        if (!IsSpawned || !IsServer || Time.unscaledTime < nextSnapshot) return;
        nextSnapshot = Time.unscaledTime + SnapshotInterval;
        removed.Clear();
        foreach (KeyValuePair<int, Observed> pair in observed)
        {
            Observed entry = pair.Value;
            if (entry.Object == null || (entry.State.Prefab < 0 && entry.Line == null && entry.Trail == null))
            {
                removed.Add(pair.Key);
                continue;
            }
            Capture(entry);
            SendSnapshot(entry.State, false);
        }
        foreach (int id in removed)
        {
            observed.Remove(id);
            RemoveSnapshotRpc(id);
        }
    }

    // 원본 좌표·파티클·볼륨·선 표시 상태 캡처
    private void Capture(Observed entry)
    {
        EffectSnapshot state = entry.State;
        Transform effect = entry.Object.transform;
        state.Active = entry.Object.activeInHierarchy;
        state.Position = state.FollowsOwner ? transform.InverseTransformPoint(effect.position) : effect.position;
        state.Rotation = state.FollowsOwner ? Quaternion.Inverse(transform.rotation) * effect.rotation : effect.rotation;
        Vector3 scale = effect.lossyScale;
        Vector3 ownerScale = transform.lossyScale;
        state.Scale = state.FollowsOwner ? new Vector3(Divide(scale.x, ownerScale.x),
            Divide(scale.y, ownerScale.y), Divide(scale.z, ownerScale.z)) : scale;
        state.Particles.Clear();
        foreach (ParticleSystem particle in entry.Particles)
        {
            if (particle == null)
            {
                state.Particles.Add(new ParticleSnapshot());
                continue;
            }
            var emission = particle.emission;
            byte flags = 0;
            if (particle.isEmitting) flags |= 1;
            if (particle.isPaused) flags |= 2;
            if (particle.isStopped) flags |= 4;
            if (emission.enabled) flags |= 8;
            if (particle.gameObject.activeInHierarchy) flags |= 16;
            if (particle.main.loop) flags |= 32;
            state.Particles.Add(new ParticleSnapshot
            {
                Flags = flags,
                Time = particle.time,
                Rate = emission.rateOverTimeMultiplier,
                DistanceRate = emission.rateOverDistanceMultiplier
            });
        }
        state.Volumes.Clear();
        foreach (Volume volume in entry.Volumes)
            state.Volumes.Add(volume != null && volume.isActiveAndEnabled ? (byte)1 : (byte)0);
        if (entry.Line != null)
        {
            LineRenderer line = entry.Line;
            if (line.positionCount > MaxPoints)
                throw new InvalidOperationException("NetworkSkillPresentation line exceeds 65 points: " + line.name);
            state.LineEnabled = line.enabled;
            state.WorldSpace = line.useWorldSpace;
            state.Loop = line.loop;
            state.StartWidth = line.startWidth;
            state.EndWidth = line.endWidth;
            state.StartColor = line.startColor;
            state.EndColor = line.endColor;
            state.Points.Clear();
            for (int i = 0; i < line.positionCount; i++) state.Points.Add(line.GetPosition(i));
        }
        if (entry.Trail != null)
        {
            // 전송 정점 상한 내 궤적 균등 표본화
            var trail = entry.Trail;
            state.LineEnabled = trail.enabled;
            state.WorldSpace = true;
            state.StartWidth = trail.startWidth;
            state.EndWidth = trail.endWidth;
            state.StartColor = trail.startColor;
            state.EndColor = trail.endColor;
            state.Points.Clear();
            int count = Mathf.Min(trail.positionCount, MaxPoints);
            for (int i = 0; i < count; i++)
            {
                int source = count <= 1 ? 0 : Mathf.RoundToInt(i * (trail.positionCount - 1f) / (count - 1));
                state.Points.Add(trail.GetPosition(source));
            }
        }
    }

    // 상대 크기 계산용 영분모 방지
    private static float Divide(float value, float divisor) => Mathf.Abs(divisor) > 0.00001f ? value / divisor : 0f;

    // 서버 효과 상태의 RPC 인자 배열 구성·전송
    private void SendSnapshot(EffectSnapshot state, bool created)
    {
        var particles = new ParticleSnapshot[state.Particles.Count];
        for (int i = 0; i < particles.Length; i++) particles[i] = state.Particles[i];
        ApplySnapshotRpc(state.Id, state.Prefab, state.Continuous, state.FollowsOwner, state.Active,
            state.Position, state.Rotation, state.Scale, created,
            EncodeFlags(particles), EncodeTimes(particles), EncodeRates(particles), EncodeDistanceRates(particles),
            state.Volumes.ToArray(), state.LineEnabled, state.WorldSpace, state.Loop,
            state.StartWidth, state.EndWidth, state.StartColor, state.EndColor, state.Points.ToArray());
    }

    // 파티클 상태 비트 전송 배열
    private static byte[] EncodeFlags(ParticleSnapshot[] particles)
    {
        var values = new byte[particles.Length];
        for (int i = 0; i < particles.Length; i++) values[i] = particles[i].Flags;
        return values;
    }

    // 파티클 재생 시각 전송 배열
    private static float[] EncodeTimes(ParticleSnapshot[] particles)
    {
        var values = new float[particles.Length];
        for (int i = 0; i < particles.Length; i++) values[i] = particles[i].Time;
        return values;
    }

    // 시간당 방출 배율 전송 배열
    private static float[] EncodeRates(ParticleSnapshot[] particles)
    {
        var values = new float[particles.Length];
        for (int i = 0; i < particles.Length; i++) values[i] = particles[i].Rate;
        return values;
    }

    // 거리당 방출 배율 전송 배열
    private static float[] EncodeDistanceRates(ParticleSnapshot[] particles)
    {
        var values = new float[particles.Length];
        for (int i = 0; i < particles.Length; i++) values[i] = particles[i].DistanceRate;
        return values;
    }

    // 서버 → 비서버 효과 상태·배열 길이 및 상한 검증
    [Rpc(SendTo.NotServer, InvokePermission = RpcInvokePermission.Server)]
    private void ApplySnapshotRpc(int id, int prefab, bool continuous, bool followsOwner, bool active,
        Vector3 position, Quaternion rotation, Vector3 scale, bool created,
        byte[] flags, float[] times, float[] rates, float[] distanceRates, byte[] volumes,
        bool lineEnabled, bool worldSpace, bool loop, float startWidth, float endWidth,
        Color startColor, Color endColor, Vector3[] points)
    {
        if (IsServer) return;
        if (flags == null || times == null || rates == null || distanceRates == null ||
            flags.Length != times.Length || flags.Length != rates.Length || flags.Length != distanceRates.Length)
            throw new InvalidOperationException("Invalid skill particle snapshot.");
        if (flags.Length > MaxParticles || (volumes != null && volumes.Length > MaxVolumes) ||
            (points != null && points.Length > MaxPoints))
            throw new InvalidOperationException("Skill snapshot exceeded presentation bounds.");

        var state = new EffectSnapshot
        {
            Id = id,
            Prefab = prefab,
            Continuous = continuous,
            FollowsOwner = followsOwner,
            Active = active,
            Position = position,
            Rotation = rotation,
            Scale = scale,
            LineEnabled = lineEnabled,
            WorldSpace = worldSpace,
            Loop = loop,
            StartWidth = startWidth,
            EndWidth = endWidth,
            StartColor = startColor,
            EndColor = endColor
        };
        for (int i = 0; i < flags.Length; i++)
            state.Particles.Add(new ParticleSnapshot
            {
                Flags = flags[i],
                Time = times[i],
                Rate = rates[i],
                DistanceRate = distanceRates[i]
            });
        if (volumes != null) state.Volumes.AddRange(volumes);
        if (points != null) state.Points.AddRange(points);
        ApplyState(state, created);
    }

    // 서버 → 비서버 효과 제거 통지
    [Rpc(SendTo.NotServer, InvokePermission = RpcInvokePermission.Server)]
    private void RemoveSnapshotRpc(int id)
    {
        if (IsServer) return;
        if (!replicas.TryGetValue(id, out Replica replica)) return;
        DestroyReplica(replica);
        replicas.Remove(id);
    }

    // 복제 개수 상한 검증·필요 시 생성 후 표시 반영
    private void ApplyState(EffectSnapshot state, bool created)
    {
        bool exists = replicas.TryGetValue(state.Id, out Replica replica);
        if (!exists)
        {
            if (replicas.Count >= MaxEffects)
                throw new InvalidOperationException("Client skill replica limit exceeded.");
            replica = CreateReplica(state);
            replicas.Add(state.Id, replica);
            created = true;
        }
        Apply(replica, state, created);
    }

    // 카탈로그 검증·물리와 게임 로직 없는 시각 복제본 생성
    private Replica CreateReplica(EffectSnapshot state)
    {
        if (staging == null)
        {
            staging = new GameObject("Skill replica staging");
            staging.SetActive(false);
        }
        GameObject instance;
        var replica = new Replica();
        if (state.Prefab < 0)
        {
            instance = new GameObject("Skill line replica");
            instance.SetActive(false);
            replica.Line = instance.AddComponent<LineRenderer>();
            Shader shader = Shader.Find("Universal Render Pipeline/Particles/Unlit");
            if (shader == null) shader = Shader.Find("Sprites/Default");
            if (shader == null) throw new InvalidOperationException("No unlit skill line shader available.");
            replica.Material = new Material(shader);
            replica.Line.sharedMaterial = replica.Material;
        }
        else
        {
            if (state.Prefab >= effectPrefabs.Length || effectPrefabs[state.Prefab] == null)
                throw new InvalidOperationException("Client skill effect catalog does not match server.");
            instance = Instantiate(effectPrefabs[state.Prefab], staging.transform);
            instance.SetActive(false);
            // 복제본의 충돌·힘·스킬 실행 차단
            foreach (Collider collider in instance.GetComponentsInChildren<Collider>(true)) collider.enabled = false;
            foreach (Collider2D collider in instance.GetComponentsInChildren<Collider2D>(true)) collider.enabled = false;
            foreach (Rigidbody body in instance.GetComponentsInChildren<Rigidbody>(true))
            { body.isKinematic = true; body.detectCollisions = false; }
            foreach (Rigidbody2D body in instance.GetComponentsInChildren<Rigidbody2D>(true)) body.simulated = false;
            foreach (MonoBehaviour behaviour in instance.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (behaviour is FireBaseScript fire)
                {
                    fire.Duration = float.MaxValue;
                    fire.ForceAmount = 0f;
                    fire.ForceRadius = 0f;
                    fire.enabled = false;
                }
                else if (!(behaviour is WizardSkillVisual) && !(behaviour is Volume))
                    DestroyImmediate(behaviour);
            }
            foreach (NetworkObject networkObject in instance.GetComponentsInChildren<NetworkObject>(true))
                DestroyImmediate(networkObject);
            // 음향은 별도 RPC 경로로만 재생
            foreach (AudioSource audioSource in instance.GetComponentsInChildren<AudioSource>(true))
            { audioSource.playOnAwake = false; audioSource.Stop(); audioSource.enabled = false; }
        }
        replica.Object = instance;
        replica.Particles = instance.GetComponentsInChildren<ParticleSystem>(true);
        replica.Volumes = instance.GetComponentsInChildren<Volume>(true);
        foreach (ParticleSystem particle in replica.Particles)
        {
            var main = particle.main;
            main.stopAction = ParticleSystemStopAction.None;
            main.playOnAwake = false;
            var collision = particle.collision;
            collision.enabled = false;
            var trigger = particle.trigger;
            trigger.enabled = false;
        }
        instance.transform.SetParent(state.FollowsOwner ? transform : null, false);
        return replica;
    }

    // 복제 좌표·파티클 재생·볼륨·선 표시 반영
    private void Apply(Replica replica, EffectSnapshot state, bool created)
    {
        Transform effect = replica.Object.transform;
        if (state.FollowsOwner)
        {
            effect.localPosition = state.Position;
            effect.localRotation = state.Rotation;
            effect.localScale = state.Scale;
        }
        else
        {
            effect.SetPositionAndRotation(state.Position, state.Rotation);
            effect.localScale = state.Scale;
        }
        replica.Object.SetActive(state.Active);
        if (created)
        {
            foreach (WizardSkillVisual visual in replica.Object.GetComponentsInChildren<WizardSkillVisual>(true))
                visual.Initialize(state.Continuous, state.FollowsOwner);
        }
        for (int i = 0; i < replica.Particles.Length && i < state.Particles.Count; i++)
        {
            ParticleSystem particle = replica.Particles[i];
            ParticleSnapshot snapshot = state.Particles[i];
            bool active = (snapshot.Flags & 16) != 0;
            if (particle.gameObject != replica.Object) particle.gameObject.SetActive(active);
            var main = particle.main;
            main.loop = (snapshot.Flags & 32) != 0;
            var emission = particle.emission;
            emission.enabled = (snapshot.Flags & 8) != 0;
            emission.rateOverTimeMultiplier = snapshot.Rate;
            emission.rateOverDistanceMultiplier = snapshot.DistanceRate;
            if (created && snapshot.Time > 0f)
                particle.Simulate(snapshot.Time, false, true, false);
            if ((snapshot.Flags & 2) != 0) particle.Pause(false);
            else if ((snapshot.Flags & 1) != 0)
            {
                if (!particle.isPlaying || !particle.isEmitting) particle.Play(false);
            }
            else if (particle.isPlaying || created)
                particle.Stop(false, ParticleSystemStopBehavior.StopEmitting);
        }
        for (int i = 0; i < replica.Volumes.Length && i < state.Volumes.Count; i++)
            replica.Volumes[i].enabled = state.Volumes[i] != 0;
        if (replica.Line != null)
        {
            replica.Line.enabled = state.LineEnabled;
            replica.Line.useWorldSpace = state.WorldSpace;
            replica.Line.loop = state.Loop;
            replica.Line.startWidth = state.StartWidth;
            replica.Line.endWidth = state.EndWidth;
            replica.Line.startColor = state.StartColor;
            replica.Line.endColor = state.EndColor;
            replica.Line.positionCount = state.Points.Count;
            for (int i = 0; i < state.Points.Count; i++) replica.Line.SetPosition(i, state.Points[i]);
        }
    }

    // 복제 객체와 전용 재질 해제
    private static void DestroyReplica(Replica replica)
    {
        if (replica.Object != null) Destroy(replica.Object);
        if (replica.Material != null) Destroy(replica.Material);
    }

    // 서버 음향 전파·비네트워크 로컬 재생·카탈로그 검증
    public static void PlaySound(GameObject owner, AudioClip clip)
    {
        if (owner == null || clip == null) return;
        NetworkSkillPresentation presentation = Find(owner);
        if (presentation == null || !presentation.IsSpawned)
        { AudioSource.PlayClipAtPoint(clip, owner.transform.position); return; }
        if (!presentation.IsServer) return;
        int index = Array.IndexOf(presentation.soundClips, clip);
        if (index < 0) throw new InvalidOperationException("Skill audio clip missing from catalog: " + clip.name);
        presentation.SoundRpc(index, owner.transform.position);
    }

    // 서버 발신 경로 → 클라이언트·호스트 음향 재생, 수신 카탈로그 검증
    [Rpc(SendTo.ClientsAndHost)]
    private void SoundRpc(int index, Vector3 position)
    {
        if (index < 0 || index >= soundClips.Length || soundClips[index] == null)
            throw new InvalidOperationException("Client skill sound catalog does not match server.");
        AudioSource.PlayClipAtPoint(soundClips[index], position);
    }

    // 유효한 양수 강도 검증·서버 소유자 흔들림 요청
    public static void Shake(GameObject owner, float strength)
    {
        if (owner == null || float.IsNaN(strength) || float.IsInfinity(strength) || strength <= 0f) return;
        NetworkSkillPresentation presentation = Find(owner);
        if (presentation == null || !presentation.IsSpawned) { ShakeLocal(owner, strength); return; }
        if (presentation.IsServer) presentation.ShakeRpc(strength);
    }

    // 서버 발신 경로 → 소유자 전용 카메라 연출
    [Rpc(SendTo.Owner)]
    private void ShakeRpc(float strength) => ShakeLocal(gameObject, strength);

    // 로컬 카메라 임펄스 재생·게임 상태 변경 없음
    private static void ShakeLocal(GameObject owner, float strength)
    {
        CinemachineImpulseSource source = owner.GetComponentInChildren<CinemachineImpulseSource>(true);
        if (source != null) source.GenerateImpulseWithForce(strength);
    }
}
