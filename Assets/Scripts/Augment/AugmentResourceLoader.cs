using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

// 증강 자산. 로드·캐시·drain. 세션·플레이어 비소유.
public class AugmentResourceLoader : MonoBehaviour
{
    private static AugmentResourceLoader instance;
    private readonly List<AugmentSO> catalog = new List<AugmentSO>();
    private readonly Dictionary<string, GameObject> prefabCache = new Dictionary<string, GameObject>();
    private readonly Dictionary<string, ResourceRequest> inFlight = new Dictionary<string, ResourceRequest>();
    private Coroutine releaseRoutine;
    private int resourceGeneration;
    private bool ready;

    public static AugmentResourceLoader Instance => instance;
    public IReadOnlyList<AugmentSO> Catalog => catalog;
    public bool IsReleasing => releaseRoutine != null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        instance = null;
    }

    private void Awake()
    {
        if (instance != null && instance != this)
        {
            Destroy(gameObject);
            return;
        }
        instance = this;
        DontDestroyOnLoad(gameObject); // 씬 파괴 후에도 drain 완료
    }

    private void OnDestroy()
    {
        if (instance == this) instance = null;
    }

    public IEnumerator WaitForRelease()
    {
        while (IsReleasing) yield return null;
    }

    // 카탈로그 1회 적재
    public void InitializeMetadata()
    {
        if (IsReleasing || ready)
            throw new InvalidOperationException("증강 자산 정리가 끝난 뒤 한 번만 초기화해야 합니다.");

        catalog.Clear();
        AugmentSO[] loaded = Resources.LoadAll<AugmentSO>("Augments/Data");
        Array.Sort(loaded, (a, b) => string.CompareOrdinal(a != null ? a.name : string.Empty,
            b != null ? b.name : string.Empty));
        foreach (AugmentSO augment in loaded)
        {
            if (augment == null) continue;
            if (augment is SkillAugmentSO skill && string.IsNullOrEmpty(skill.SkillResourcePath))
            {
                Debug.LogError($"[Augment] 스킬 경로가 비어 있어 제외합니다: {skill.name}");
                continue;
            }
            catalog.Add(augment);
        }
        ready = true;
    }

    public IEnumerator LoadSkillPrefab(string path, Action<GameObject, string> completed)
    {
        if (!ready || IsReleasing)
        {
            completed?.Invoke(null, null);
            yield break;
        }
        if (string.IsNullOrEmpty(path))
        {
            completed?.Invoke(null, "스킬 리소스 경로가 비어 있습니다.");
            yield break;
        }
        if (prefabCache.TryGetValue(path, out GameObject cached) && cached != null)
        {
            completed?.Invoke(cached, null);
            yield break;
        }

        int generation = resourceGeneration;
        if (!inFlight.TryGetValue(path, out ResourceRequest request))
        {
            request = Resources.LoadAsync<GameObject>(path);
            inFlight.Add(path, request); // 경로별 요청 공유
        }
        yield return request;

        // 세대 불일치: 이전 수명 콜백 차단
        if (generation != resourceGeneration || !ready)
        {
            completed?.Invoke(null, null);
            yield break;
        }
        if (inFlight.TryGetValue(path, out ResourceRequest current) && current == request)
            inFlight.Remove(path);

        GameObject prefab = request.asset as GameObject;
        if (prefab == null || prefab.GetComponent<AugmentSkill>() == null)
        {
            completed?.Invoke(null, $"스킬 프리팹이 없거나 AugmentSkill이 없습니다: {path}");
            yield break;
        }
        prefabCache[path] = prefab;
        completed?.Invoke(prefab, null);
    }

    public void ReleaseResources()
    {
        if (IsReleasing || (!ready && inFlight.Count == 0)) return;
        ready = false;
        resourceGeneration++;
        releaseRoutine = StartCoroutine(ReleaseRoutine());
    }

    private IEnumerator ReleaseRoutine()
    {
        var requests = new List<ResourceRequest>(inFlight.Values);
        inFlight.Clear();
        prefabCache.Clear();
        catalog.Clear();
        yield return null; // 호출자 Destroy/핸들 할당 여유
        foreach (ResourceRequest request in requests)
            if (request != null && !request.isDone) yield return request;
        requests.Clear();
        yield return Resources.UnloadUnusedAssets();
        releaseRoutine = null;
    }
}
