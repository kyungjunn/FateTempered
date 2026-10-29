using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

// 결과 패킷 증강 구분용 바이트 열거형
public enum AugmentResultKind : byte
{
    Stat,
    Skill
}

// 증강 결과 패킷: 스탯별 누적치 또는 스킬 에셋 이름·중첩
public struct AugmentResult : INetworkSerializable
{
    // 스탯·스킬 증강 구분
    public AugmentResultKind Kind;
    // 스탯 종류·종류별 누적 보너스
    public AugmentType StatType;
    public float Value;
    // 고정 64바이트 문자열 형식의 스킬 에셋 이름
    public FixedString64Bytes SkillId;
    // 스킬 중첩 수: 캡처 시 0~255 제한
    public byte Stacks;

    // 송수신 고정 순서: 종류 → 스탯 종류 → 값 → 스킬 식별자 → 중첩
    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref Kind);
        serializer.SerializeValue(ref StatType);
        serializer.SerializeValue(ref Value);
        serializer.SerializeValue(ref SkillId);
        serializer.SerializeValue(ref Stacks);
    }
}

// 참가자 결과 패킷: 경기 종료 시 시뮬레이션 인스턴스 상태의 단일 캡처
public struct PlayerResult : INetworkSerializable
{
    // 캐릭터 식별자
    public PlayerCharacterId Champion;
    // 고정 64바이트 문자열 형식의 표시 이름
    public FixedString64Bytes DisplayName;
    // 참가 클라이언트 식별자
    public ulong ClientId;
    // 누적 피해량·처치 수·최종 레벨·생존 시간(초)
    public long DamageDealt;
    public int Kills;
    public int Level;
    public float AliveSeconds;
    // 종료 시점 능력치: 공격력·최대 체력·방어력·이동속도·공격속도
    public float AttackDamage;
    public float MaxHp;
    public float Defense;
    public float MoveSpeed;
    public float AttackSpeed;
    // 증강 결과 목록: 수신 배열 길이 0~64 제한
    public AugmentResult[] Augments;

    // 시뮬레이션 상태 캡처, 플레이어 부재 시 기본 결과
    public static PlayerResult Capture(GameObject player, string displayName, ulong clientId)
    {
        var result = new PlayerResult
        {
            DisplayName = new FixedString64Bytes(displayName ?? string.Empty),
            ClientId = clientId,
            Augments = System.Array.Empty<AugmentResult>()
        };
        if (player == null) return result;

        if (player.TryGetComponent(out PlayerCharacter character)) result.Champion = character.CharacterId;
        if (player.TryGetComponent(out PlayerRunStats run))
        {
            result.DamageDealt = run.DamageDealt;
            result.Kills = run.Kills;
            result.AliveSeconds = run.AliveSeconds;
        }
        if (player.TryGetComponent(out PlayerLevelSystem level)) result.Level = level.level;

        var augments = new List<AugmentResult>();
        if (player.TryGetComponent(out PlayerStats stats))
        {
            result.AttackDamage = stats.AttackDamage;
            result.MaxHp = stats.HP;
            result.Defense = stats.Defense;
            result.MoveSpeed = stats.MoveSpeed;
            result.AttackSpeed = stats.AttackSpeed;
            foreach (AugmentType type in stats.GetAugmentedStatTypes())
                augments.Add(new AugmentResult
                {
                    Kind = AugmentResultKind.Stat,
                    StatType = type,
                    Value = stats.GetAugmentBonus(type)
                });
        }
        if (player.TryGetComponent(out PlayerAugments owned))
        {
            foreach (SkillAugmentSO skill in owned.OwnedSkills)
            {
                if (skill == null) continue;
                augments.Add(new AugmentResult
                {
                    Kind = AugmentResultKind.Skill,
                    SkillId = new FixedString64Bytes(skill.name),
                    Stacks = (byte)Mathf.Clamp(owned.GetSkillStackCount(skill), 0, byte.MaxValue)
                });
            }
        }
        result.Augments = augments.ToArray();
        return result;
    }

    // 송수신 고정 순서: 캐릭터·이름·클라이언트 → 전적 → 능력치 → 증강 수·항목
    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref Champion);
        serializer.SerializeValue(ref DisplayName);
        serializer.SerializeValue(ref ClientId);
        serializer.SerializeValue(ref DamageDealt);
        serializer.SerializeValue(ref Kills);
        serializer.SerializeValue(ref Level);
        serializer.SerializeValue(ref AliveSeconds);
        serializer.SerializeValue(ref AttackDamage);
        serializer.SerializeValue(ref MaxHp);
        serializer.SerializeValue(ref Defense);
        serializer.SerializeValue(ref MoveSpeed);
        serializer.SerializeValue(ref AttackSpeed);

        int count = Augments != null ? Augments.Length : 0;
        serializer.SerializeValue(ref count);
        if (serializer.IsReader) Augments = new AugmentResult[Mathf.Clamp(count, 0, 64)];
        for (int i = 0; i < Augments.Length; i++) serializer.SerializeValue(ref Augments[i]);
    }
}

// 경기 결과 패킷: 승패 구분 없이 전원 체력 소진 시 종료
public struct MatchResult : INetworkSerializable
{
    // 경기 진행 시간(초)
    public float MatchSeconds;
    // 참가자 결과 목록: 수신 배열 길이 0~2 제한
    public PlayerResult[] Players;

    // 송수신 고정 순서: 경기 시간 → 참가자 수 → 참가자별 결과
    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref MatchSeconds);
        int count = Players != null ? Players.Length : 0;
        serializer.SerializeValue(ref count);
        if (serializer.IsReader) Players = new PlayerResult[Mathf.Clamp(count, 0, 2)];
        for (int i = 0; i < Players.Length; i++) serializer.SerializeValue(ref Players[i]);
    }
}
