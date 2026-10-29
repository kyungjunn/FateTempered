using System;
using System.Collections.Generic;

// 서버 전용 협동 판 규칙; 외부 시각 입력, 팀원 생존 시 지연 부활, 전원 쓰러짐 시 종료.
public sealed class MatchFlow
{
    // 생존 조회·실제 부활을 담당하는 플레이어 계약.
    public interface IMember
    {
        // 현재 사망 상태.
        bool IsDead { get; }
        // 체력 비율·무적 시간을 적용한 부활 시도.
        bool Revive(float healthRatio, float invulnerableSeconds);
    }

    // 서버 내부 참가자별 쓰러짐·부활 예약; 직접 복제하지 않는 상태.
    private sealed class Entry
    {
        public ulong ClientId;
        public IMember Member;
        public bool Downed;
        public double ReviveAt;
    }

    // 현재 참가 명단과 판 전체의 부활 규칙.
    private readonly List<Entry> entries = new List<Entry>(2);
    private readonly float reviveDelay;
    private readonly float reviveHealthRatio;
    private readonly float reviveInvulnerableSeconds;

    // 종료 확정 상태·시각 및 세션 계층으로 전달할 전이 알림.
    public bool Ended { get; private set; }
    public double EndedAt { get; private set; }
    public event Action<ulong> Downed;
    public event Action<ulong> Revived;
    public event Action MatchEnded;

    // 판별 부활 규칙 고정; 음수 대기·무적 시간 방지.
    public MatchFlow(float reviveDelay, float reviveHealthRatio, float reviveInvulnerableSeconds)
    {
        this.reviveDelay = Math.Max(0f, reviveDelay);
        this.reviveHealthRatio = reviveHealthRatio;
        this.reviveInvulnerableSeconds = Math.Max(0f, reviveInvulnerableSeconds);
    }

    public int MemberCount => entries.Count;

    // 유효한 참가자 중복 없이 등록.
    public void Add(ulong clientId, IMember member)
    {
        if (member == null || Find(clientId) != null) return;
        entries.Add(new Entry { ClientId = clientId, Member = member });
    }

    // 이탈자 제거 후 남은 참가자의 전멸 재판정.
    public void Remove(ulong clientId, double now)
    {
        Entry entry = Find(clientId);
        if (entry == null) return;
        entries.Remove(entry);
        Evaluate(now);
    }

    // 진행 중인 판의 참가자별 부활 예약 시각 조회.
    public bool TryGetReviveTime(ulong clientId, out double reviveAt)
    {
        Entry entry = Find(clientId);
        reviveAt = entry != null && entry.Downed ? entry.ReviveAt : 0d;
        return entry != null && entry.Downed && !Ended;
    }

    // 사망 감지 → 전멸 판정 → 예약 시각 도달자의 부활.
    public void Tick(double now)
    {
        if (Ended) return;

        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            if (!entry.Downed && entry.Member.IsDead)
            {
                entry.Downed = true;
                entry.ReviveAt = now + reviveDelay;
                Downed?.Invoke(entry.ClientId);
            }
        }

        // 전멸 판정을 부활보다 우선하여 마지막 생존자 사망 시 즉시 종료.
        if (Evaluate(now)) return;

        for (int i = 0; i < entries.Count; i++)
        {
            Entry entry = entries[i];
            if (!entry.Downed || now < entry.ReviveAt) continue;
            if (!entry.Member.Revive(reviveHealthRatio, reviveInvulnerableSeconds) && entry.Member.IsDead) continue;
            entry.Downed = false;
            Revived?.Invoke(entry.ClientId);
        }
    }

    // 비어 있지 않은 명단의 생존자 부재 확인·종료 단발 알림.
    private bool Evaluate(double now)
    {
        if (Ended || entries.Count == 0) return Ended;
        for (int i = 0; i < entries.Count; i++)
            if (!entries[i].Downed && !entries[i].Member.IsDead) return false;

        Ended = true;
        EndedAt = now;
        MatchEnded?.Invoke();
        return true;
    }

    // 네트워크 참가자 ID로 서버 내부 항목 검색.
    private Entry Find(ulong clientId)
    {
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].ClientId == clientId) return entries[i];
        return null;
    }
}
