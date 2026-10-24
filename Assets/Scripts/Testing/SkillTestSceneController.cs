using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;

// Scene-only test controls; production spawning and skill rules remain unchanged.
public sealed class SkillTestSceneController : MonoBehaviour
{
    [SerializeField] private GameObject[] characterPrefabs;
    [SerializeField] private Transform spawnPoint;
    [SerializeField] private GameObject hudRoot;
    [SerializeField] private TMP_Text instructions;

    private GameObject currentPlayer;
    private int selectedIndex;

    public GameObject CurrentPlayer => currentPlayer;

    private void Start()
    {
        SpawnCharacter(0);
    }

    private void Update()
    {
        var keyboard = Keyboard.current;
        if (keyboard == null) return;
        if (keyboard.f1Key.wasPressedThisFrame) SpawnCharacter(0);
        else if (keyboard.f2Key.wasPressedThisFrame) SpawnCharacter(1);
        else if (keyboard.f3Key.wasPressedThisFrame) SpawnCharacter(2);
        else if (keyboard.f5Key.wasPressedThisFrame) SpawnCharacter(selectedIndex);
        if (keyboard.f4Key.wasPressedThisFrame) FillUltimate();
    }

    public void SpawnCharacter(int index)
    {
        if (index < 0 || index >= characterPrefabs.Length || characterPrefabs[index] == null)
            return;

        hudRoot.SetActive(false);
        if (currentPlayer != null)
        {
            currentPlayer.SetActive(false);
            Destroy(currentPlayer);
        }

        selectedIndex = index;
        currentPlayer = Instantiate(characterPrefabs[index], spawnPoint.position, spawnPoint.rotation);
        currentPlayer.name = characterPrefabs[index].name;
        FillUltimate();
        // Existing HUD components bind the sole active Player on OnEnable.
        hudRoot.SetActive(true);
        instructions.SetText("SKILL TEST  |  " + currentPlayer.name +
            "\nF1 Wizard   F2 Ninja   F3 Knight\nF4 Fill R   F5 Respawn / Reset\nWASD Move   Mouse Aim   LMB Attack   Q / E / R Skills   Tab Stats");
    }

    public void FillUltimate()
    {
        if (currentPlayer != null && currentPlayer.TryGetComponent(out PlayerUltimateGauge gauge))
            gauge.Add(float.MaxValue);
    }
}
