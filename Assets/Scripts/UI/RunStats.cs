// 한 판 동안의 기록(킬 수)을 세는 곳. 씬에 붙일 필요 없음.
public static class RunStats
{
    public static int Kills { get; private set; }

    public static void Reset() => Kills = 0;

    // 몬스터가 죽을 때 한 번 호출
    public static void AddKill() => Kills++;
}
