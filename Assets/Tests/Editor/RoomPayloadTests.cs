using System;
using UnityEditor;
using UnityEngine;

public static class RoomPayloadTests
{
    [MenuItem("Tools/Validation/LAN Room Approval Payload")]
    public static void Run()
    {
        Guid room = Guid.NewGuid();
        Require(RoomConnectionPayload.TryEncode(room, "팀원", out byte[] payload, out _), "Encode valid profile.");
        Require(RoomConnectionPayload.TryDecode(payload, out Guid decodedRoom, out string name, out _)
            && decodedRoom == room && name == "팀원", "Preserve room identity and profile.");
        Require(!RoomConnectionPayload.TryEncode(Guid.Empty, "Player", out _, out _), "Reject missing room identity.");
        Require(!RoomConnectionPayload.TryEncode(room, "<b>Player</b>", out _, out _), "Reject profile markup.");
        Require(!RoomConnectionPayload.TryEncode(room, new string('한', 22), out _, out _), "Reject excessive UTF8 profile.");
        for (int length = 0; length < payload.Length; length++)
        {
            var truncated = new byte[length];
            Buffer.BlockCopy(payload, 0, truncated, 0, length);
            Require(!RoomConnectionPayload.TryDecode(truncated, out _, out _, out _), "Reject truncated payload.");
        }
        var trailing = new byte[payload.Length + 1];
        Buffer.BlockCopy(payload, 0, trailing, 0, payload.Length);
        Require(!RoomConnectionPayload.TryDecode(trailing, out _, out _, out _), "Reject trailing data.");
        var wrongVersion = (byte[])payload.Clone();
        wrongVersion[0]++;
        Require(!RoomConnectionPayload.TryDecode(wrongVersion, out _, out _, out _), "Reject incompatible version.");
        var corruptUtf8 = (byte[])payload.Clone();
        corruptUtf8[RoomConnectionPayload.HeaderLength] = 0xff;
        Require(!RoomConnectionPayload.TryDecode(corruptUtf8, out _, out _, out _), "Reject invalid UTF8.");
        Require(!RoomConnectionPayload.TryDecode(null, out _, out _, out _), "Reject null payload.");
        Require(!RoomConnectionPayload.TryDecode(new byte[RoomConnectionPayload.MaximumPayloadBytes + 1], out _, out _, out _),
            "Reject oversized payload.");
        var random = new System.Random(5190);
        for (int i = 0; i < 1024; i++)
        {
            var noise = new byte[random.Next(0, 130)];
            random.NextBytes(noise);
            if (noise.Length > 0) noise[0] = 0;
            Require(!RoomConnectionPayload.TryDecode(noise, out _, out _, out _), "Reject malformed payload without exception.");
        }
        Debug.Log("ROOM_PAYLOAD_TESTS_PASSED: roundtrip, identity, names, truncation, version, UTF8 and malformed input");
    }

    private static void Require(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException("Room approval payload regression: " + reason);
    }
}
