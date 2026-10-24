using System;
using System.Collections.Generic;
using UnityEngine;

public enum AugmentRarity { Normal, Legendary }

// 결과 화면에 표시할 증강 1개
[Serializable]
public class AugmentData
{
    public Sprite icon;
    public string valueText;      // 예: "+20", "+1000"
    public string displayName;    // 예: "Move Speed", "Legendary Damage"
    public AugmentRarity rarity;
}

// 결과 화면에 넘겨줄 데이터 묶음 (게임 매니저에서 채워서 전달)
[Serializable]
public class ResultData
{
    public bool isVictory = true;

    public float survivalTime;    // 초 단위
    public int monsterKills;
    public int finalLevel;

    public float attackDamage;
    public float hp;
    public float maxHp;
    public float speed;
    public float attackSpeed;
    public float defense;

    public List<AugmentData> augments = new List<AugmentData>();
}
