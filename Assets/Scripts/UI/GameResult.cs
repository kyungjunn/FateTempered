using System;

// 마지막 판의 결과 보관함. 결과 화면이 읽는다.
public static class GameResult
{
    // 네트워크·오프라인 공용 결과 스냅샷·미수신 시 null
    private static MatchResult? data;

    // 결과 수신·초기화 시 패널 갱신 알림
    public static event Action Changed;

    public static MatchResult? Data
    {
        get => data;
        set
        {
            data = value;
            Changed?.Invoke();
        }
    }

    public static void Clear()
    {
        Data = null;
    }
}
