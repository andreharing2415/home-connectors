using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using HomeConnectors.Adapters;

namespace HomeConnectors.Tests;

/// <summary>
/// HOME-03. The Xiaomi cloud's request crypto, checked against an independent
/// Python implementation (pycryptodome, the same one that first read the M40's
/// map), and the Dreame map file, round-tripped through a file built here the
/// way the robot builds it.
/// </summary>
[TestClass]
public sealed class XiaomiCloudAndMapTests
{
    [TestMethod]
    public void Rc4_drops_1024_bytes_of_keystream_like_the_app()
    {
        var key = Convert.ToBase64String([.. Enumerable.Range(0, 16).Select(i => (byte)i)]);

        var cipher = XiaomiCloudClient.Rc4(key, Encoding.UTF8.GetBytes("""{"did":"1"}"""));

        Assert.AreEqual("f123cc7f5d0c4bbb90f2fb", Convert.ToHexStringLower(cipher));
    }

    [TestMethod]
    public void Signed_nonce_and_signature_match_the_reference()
    {
        var ssecurity = Convert.ToBase64String([.. Enumerable.Range(16, 16).Select(i => (byte)i)]);
        var nonce = Convert.ToBase64String([.. Enumerable.Range(0, 12).Select(i => (byte)i)]);

        var signed = XiaomiCloudClient.SignedNonce(ssecurity, nonce);
        var signature = XiaomiCloudClient.Signature("/v2/home/rpc/1", signed, [new("data", """{"a":1}""")]);

        Assert.AreEqual("5pOocRFEqw9sw2m1grepXQ3mN2XPtxNysvT+1h26jP8=", signed);
        Assert.AreEqual("s0IsT4Tw8UF2FObhIFJFahHkbl0=", signature);
    }

    [TestMethod]
    public void A_map_file_decrypts_to_its_rooms_names_zones_and_robot()
    {
        // A 4×3 saved map: room 1 on the left, room 2 on the right, a wall cell.
        byte[] savedPixels = [1, 1, 2, 2, 1, 0x80, 2, 2, 1, 1, 2, 2];
        var savedJson = $$$"""
            {"seg_inf":{"1":{"name":"{{{B64("Sala")}}}"},"2":{"name":"{{{B64("Cozinha")}}}"}},
             "vw":{"line":[[0,0,100,0]],"rect":[[-500,-400,-300,-200,0]],"mop":[]},
             "charger":[120,-80]}
            """;
        var saved = Frame(mapId: 3, robot: (0, 0, 0), grid: 50, width: 4, height: 3, left: -1000, top: -500, savedPixels, savedJson);

        var rism = Convert.ToBase64String(Zlib(saved)).Replace('+', '-').Replace('/', '_');
        var liveJson = $$"""{"rism":"{{rism}}","tr":"M100,200L50,0L0,50","sa":[[2,1,1]]}""";
        var live = Frame(mapId: 1, robot: (-900, -450, 90), grid: 50, width: 1, height: 1, left: 0, top: 0, [0], liveJson);

        const string key = "wKSsIPMMJfzs5JD4";
        var file = Encrypt(Zlib(live), key);

        var frame = DreameMapDecoder.Unwrap(file, key);
        var map = DreameMapDecoder.ReadSaved(frame, DateTimeOffset.UnixEpoch)!;
        var now = DreameMapDecoder.ReadLive(frame);

        Assert.AreEqual(3, map.MapId, "the saved map's own id, which edits must name");
        Assert.AreEqual((4, 3, -1000, -500, 50), (map.Width, map.Height, map.Left, map.Top, map.Grid));
        Assert.AreEqual(VacuumMap.Wall, map.At(1, 1));
        CollectionAssert.AreEqual(new[] { "Sala", "Cozinha" }, map.Rooms.Select(r => r.Name).ToArray());
        Assert.AreEqual(5, map.Room(1)!.Cells);
        Assert.AreEqual(new MapRect(-500, -400, -300, -200), map.Zones.NoGo.Single());
        Assert.AreEqual(new MapLine(0, 0, 100, 0), map.Zones.Walls.Single());
        Assert.AreEqual(new MapPoint(120, -80), map.Charger);

        Assert.AreEqual(new MapPoint(-900, -450), now.Robot);
        Assert.AreEqual(90, now.Angle);
        CollectionAssert.AreEqual(new[] { new MapPoint(100, 200), new MapPoint(150, 200), new MapPoint(150, 250) }, now.Path.Single().ToArray());
        CollectionAssert.AreEqual(new[] { 2 }, now.ActiveRooms.ToArray());
    }

    [TestMethod]
    public void The_backup_list_is_read_newest_first()
    {
        var json = Encoding.UTF8.GetBytes("""
            [{"id":3,"info":[{"first":1,"time":1790510997,"thb":"x","objname":"u/d/100"},
                             {"first":0,"time":1790514901,"thb":"x","objname":"u/d/103"}]}]
            """);

        var backups = XiaomiVacuumMapService.ParseBackups(json);

        CollectionAssert.AreEqual(new[] { "u/d/103", "u/d/100" }, backups.Select(b => b.ObjectName).ToArray());
        Assert.IsTrue(backups[1].First);
        Assert.AreEqual(3, backups[0].MapId);
    }

    private static string B64(string text) => Convert.ToBase64String(Encoding.UTF8.GetBytes(text));

    private static byte[] Frame(short mapId, (short X, short Y, short A) robot, short grid, short width, short height, short left, short top, byte[] pixels, string json)
    {
        var header = new byte[27];
        void Put(int offset, short value) => BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(offset), value);
        Put(0, mapId);
        header[4] = (byte)'I';
        Put(5, robot.X);
        Put(7, robot.Y);
        Put(9, robot.A);
        Put(17, grid);
        Put(19, width);
        Put(21, height);
        Put(23, left);
        Put(25, top);
        return [.. header, .. pixels, .. Encoding.UTF8.GetBytes(json)];
    }

    private static byte[] Zlib(byte[] data)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionLevel.Optimal))
        {
            zlib.Write(data);
        }

        return output.ToArray();
    }

    private static string Encrypt(byte[] data, string key)
    {
        using var aes = Aes.Create();
        aes.Key = Encoding.ASCII.GetBytes(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..32]);
        var cipher = aes.EncryptCbc(data, Encoding.ASCII.GetBytes(DreameMapDecoder.M40Iv), PaddingMode.PKCS7);
        return Convert.ToBase64String(cipher).Replace('+', '-').Replace('/', '_');
    }
}
