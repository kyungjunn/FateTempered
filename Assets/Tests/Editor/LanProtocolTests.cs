using System;
using UnityEditor;
using UnityEngine;

// Executable in the editor or with -executeMethod LanProtocolTests.RunBatch.
public static class LanProtocolTests
{
    [MenuItem("Tools/Validation/LAN Protocol and Names")]
    public static void Run()
    {
        TestNames();
        TestQuery();
        TestAdvertisement();
        TestMalformedPackets();
        Debug.Log("LAN_PROTOCOL_TESTS_PASSED: names, query, advertisement, bounds and malformed packets");
    }

    public static void RunBatch()
    {
        try { Run(); EditorApplication.Exit(0); }
        catch (Exception error) { Debug.LogException(error); EditorApplication.Exit(1); }
    }

    private static void TestNames()
    {
        Require(LocalPlayerProfile.TryValidateDisplayName("  플레이어  ", out string name, out _)
            && name == "플레이어", "Display name trims without losing Unicode.");
        Require(LocalPlayerProfile.TryValidateDisplayName("e\u0301", out name, out _) && name == "é",
            "Display name is NFC normalized.");
        string[] invalid = { null, "", "   ", "<b>name</b>", "name\nother", "name\u202e", "\ud800", new string('a', 25), new string('한', 22) };
        foreach (string value in invalid)
            Require(!LocalPlayerProfile.TryValidateDisplayName(value, out _, out _), "Reject invalid display name.");
        Require(LocalPlayerProfile.TryValidateDisplayName(new string('a', 24), out _, out _), "Scalar boundary accepted.");
        Require(LocalPlayerProfile.TryValidateRoomName(new string('한', 32), out _, out _), "Room UTF8 boundary accepted.");
        Require(!LocalPlayerProfile.TryValidateRoomName(new string('한', 33), out _, out _), "Room UTF8 overflow rejected.");
    }

    private static void TestQuery()
    {
        byte[] buffer = new byte[LanDiscoveryProtocol.MaxDatagramBytes];
        const ulong nonce = 0xfedcba9876543210;
        Require(LanDiscoveryProtocol.TryEncodeQuery(nonce, buffer, out int length), "Encode query.");
        Require(LanDiscoveryProtocol.TryDecodeQuery(buffer, 0, length, out ulong decoded) && decoded == nonce,
            "Query nonce round trip.");
        Require(!LanDiscoveryProtocol.TryDecodeQuery(buffer, 0, length + 1, out _), "Reject query trailing byte.");
        Require(!LanDiscoveryProtocol.TryDecodeQuery(buffer, 0, length - 1, out _), "Reject truncated query.");
        Require(!LanDiscoveryProtocol.TryEncodeQuery(0, buffer, out _), "Reject zero nonce.");
        buffer[4]++;
        Require(!LanDiscoveryProtocol.TryDecodeQuery(buffer, 0, length, out _), "Reject unsupported protocol.");
    }

    private static void TestAdvertisement()
    {
        byte[] buffer = new byte[LanDiscoveryProtocol.MaxDatagramBytes];
        Guid room = Guid.NewGuid();
        Require(LanDiscoveryProtocol.TryEncodeAdvertisement(42, room, 9, 7777, "함께 플레이", 1, 2, true,
            buffer, out int length), "Encode room advertisement.");
        Require(LanDiscoveryProtocol.TryDecodeAdvertisement(buffer, 0, length, out ulong nonce, out var value)
            && nonce == 42 && value.RoomInstanceId == room && value.Sequence == 9 && value.GamePort == 7777
            && value.RoomName == "함께 플레이" && value.Occupancy == 1 && value.Capacity == 2 && value.Accepting,
            "Advertisement preserves every field.");
        byte[] offsetBuffer = new byte[length + 4];
        Buffer.BlockCopy(buffer, 0, offsetBuffer, 3, length);
        Require(LanDiscoveryProtocol.TryDecodeAdvertisement(offsetBuffer, 3, length, out _, out value)
            && value.RoomInstanceId == room, "Decode nonzero offset.");
        Require(!LanDiscoveryProtocol.TryDecodeAdvertisement(buffer, 0, length + 1, out _, out _), "Reject trailing advertisement byte.");
        for (int size = 0; size < length; size++)
            Require(!LanDiscoveryProtocol.TryDecodeAdvertisement(buffer, 0, size, out _, out _), "Reject every truncated advertisement.");
        buffer[length - 1] = 2;
        Require(!LanDiscoveryProtocol.TryDecodeAdvertisement(buffer, 0, length, out _, out _), "Reject nonboolean accepting flag.");
        Require(!LanDiscoveryProtocol.TryEncodeAdvertisement(42, room, 9, 7777, "Full", 2, 2, true,
            buffer, out _), "Full rooms cannot advertise accepting.");
        Require(LanDiscoveryProtocol.TryEncodeAdvertisement(42, room, 10, 7777, "Full", 2, 2, false,
            buffer, out length), "Full rooms can advertise nonjoinable.");
        Require(LanDiscoveryProtocol.TryDecodeAdvertisement(buffer, 0, length, out _, out value)
            && !value.Accepting && value.Occupancy == 2, "Full room status round trip.");
        Require(!LanDiscoveryProtocol.TryEncodeAdvertisement(42, Guid.Empty, 9, 7777, "Room", 1, 2, true,
            buffer, out _), "Reject missing room identity.");
    }

    private static void TestMalformedPackets()
    {
        byte[] buffer = new byte[LanDiscoveryProtocol.MaxDatagramBytes + 1];
        int[] offsets = { -1, 0, buffer.Length, int.MaxValue };
        int[] counts = { -1, 0, 1, buffer.Length, int.MaxValue };
        foreach (int offset in offsets)
            foreach (int count in counts)
            {
                Require(!LanDiscoveryProtocol.TryDecodeQuery(buffer, offset, count, out _), "Reject invalid query bounds/header.");
                Require(!LanDiscoveryProtocol.TryDecodeAdvertisement(buffer, offset, count, out _, out _), "Reject invalid response bounds/header.");
            }
        Require(!LanDiscoveryProtocol.TryDecodeQuery(null, 0, 14, out _), "Reject null query.");
        Require(!LanDiscoveryProtocol.TryDecodeAdvertisement(null, 0, 50, out _, out _), "Reject null advertisement.");
        var random = new System.Random(5189);
        for (int i = 0; i < 2048; i++)
        {
            random.NextBytes(buffer);
            int count = random.Next(buffer.Length + 1);
            // Random noise must never throw, allocate based on untrusted lengths or accept a bad magic.
            buffer[0] = 0;
            Require(!LanDiscoveryProtocol.TryDecodeQuery(buffer, 0, count, out _), "Reject malformed query.");
            Require(!LanDiscoveryProtocol.TryDecodeAdvertisement(buffer, 0, count, out _, out _), "Reject malformed response.");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("LAN protocol regression: " + message);
    }
}
