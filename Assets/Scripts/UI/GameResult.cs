// 인게임 씬 → 엔딩 씬으로 결과를 넘겨주는 보관함
// MonoBehaviour가 아니라서 씬에 붙일 필요 없음
public static class GameResult
{
    public static ResultData Data;

    public static void Clear()
    {
        Data = null;
    }
}
