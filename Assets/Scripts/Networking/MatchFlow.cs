using System;
using System.Collections.Generic;

// 협동 판의 쓰러짐·부활·종료 규칙. 서버만 실행한다. 시간은 호출자가 넣어 테스트할 수 있게 한다.
// 규칙: 쓰러진 사람은 다른 팀원이 reviveDelay 동안 살아 있으면 그 자리에서 부활한다.
//       살아 있는 멤버가 0명이 되는 순간 판이 끝난다 (1인은 쓰러지는 즉시 종료).
public sealed class MatchFlow
{
    public interface IMember
    {
        bool IsDead { get; }
        bool Revive(float healthRatio, float invulnerableSeconds);
    }

    private sealed class Entry
    {
        public ulong ClientId;
        public IMember Member;
        public bool Downed;
        public double ReviveAt;
    }

    private readonly List<Entry> entries = new List<Entry>(2);
    private readonly float reviveDelay;
    private readonly float reviveHealthRatio;
    private readonly float reviveInvulnerableSeconds;

    public bool Ended { get; private set; }
    public double EndedAt { get; private set; }
    public event Action<ulong> Downed;
    public event Action<ulong> Revived;
    public event Action MatchEnded;

    public MatchFlow(float reviveDelay, float reviveHealthRatio, float reviveInvulnerableSeconds)
    {
        this.reviveDelay = Math.Max(0f, reviveDelay);
        this.reviveHealthRatio = reviveHealthRatio;
        this.reviveInvulnerableSeconds = Math.Max(0f, reviveInvulnerableSeconds);
    }

    public int MemberCount => entries.Count;

    public void Add(ulong clientId, IMember member)
    {
        if (member == null || Find(clientId) != null) return;
        entries.Add(new Entry { ClientId = clientId, Member = member });
    }

    // 이탈: 남은 사람만으로 다시 판정한다.
    public void Remove(ulong clientId, double now)
    {
        Entry entry = Find(clientId);
        if (entry == null) return;
        entries.Remove(entry);
        Evaluate(now);
    }

    // 쓰러진 멤버의 부활 예정 시각. 없으면 false.
    public bool TryGetReviveTime(ulong clientId, out double reviveAt)
    {
        Entry entry = Find(clientId);
        reviveAt = entry != null && entry.Downed ? entry.ReviveAt : 0d;
        return entry != null && entry.Downed && !Ended;
    }

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

    private Entry Find(ulong clientId)
    {
        for (int i = 0; i < entries.Count; i++)
            if (entries[i].ClientId == clientId) return entries[i];
        return null;
    }
}
