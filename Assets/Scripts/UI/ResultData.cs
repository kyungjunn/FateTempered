using System.Collections.Generic;
using Unity.Collections;
using Unity.Netcode;
using UnityEngine;

public enum AugmentResultKind : byte
{
    Stat,
    Skill
}

// 결과 화면의 증강 1개. 스탯 증강은 종류별 누적치, 스킬 증강은 에셋 이름과 중첩.
public struct AugmentResult : INetworkSerializable
{
    public AugmentResultKind Kind;
    public AugmentType StatType;
    public float Value;
    public FixedString64Bytes SkillId;
    public byte Stacks;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref Kind);
        serializer.SerializeValue(ref StatType);
        serializer.SerializeValue(ref Value);
        serializer.SerializeValue(ref SkillId);
        serializer.SerializeValue(ref Stacks);
    }
}

// 플레이어 한 명의 판 기록. 판이 끝나는 순간 시뮬레이션 인스턴스에서 한 번 캡처한다.
public struct PlayerResult : INetworkSerializable
{
    public PlayerCharacterId Champion;
    public FixedString64Bytes DisplayName;
    public ulong ClientId;
    public long DamageDealt;
    public int Kills;
    public int Level;
    public float AliveSeconds;
    public float AttackDamage;
    public float MaxHp;
    public float Defense;
    public float MoveSpeed;
    public float AttackSpeed;
    public AugmentResult[] Augments;

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

// 한 판의 결과. 승패 없음: HP 0(2인은 전원 쓰러짐)이 판의 끝이다.
public struct MatchResult : INetworkSerializable
{
    public float MatchSeconds;
    public PlayerResult[] Players;

    public void NetworkSerialize<T>(BufferSerializer<T> serializer) where T : IReaderWriter
    {
        serializer.SerializeValue(ref MatchSeconds);
        int count = Players != null ? Players.Length : 0;
        serializer.SerializeValue(ref count);
        if (serializer.IsReader) Players = new PlayerResult[Mathf.Clamp(count, 0, 2)];
        for (int i = 0; i < Players.Length; i++) serializer.SerializeValue(ref Players[i]);
    }
}
