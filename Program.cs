using Ionic.Zlib;
using JRPC_Client;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XDevkit;

namespace GSCConsoleInjector
{
    class Program
    {
        static IXboxConsole xboxConsole;

        static readonly Dictionary<string, uint> GameOffsets = new Dictionary<string, uint>
        {
            { "bo2",    0x82215798 },
            { "cod2", 0x823F2440 },
            { "nx1",    0x82215798 },
            { "cod4",   0x822A0298 },
            { "wawmp",  0x821E7A70 },
            { "wawsp",  0x82158FB8 },
            { "wawzm",  0x82158FB8 },
            {"COD2",0x823F2440 },
            {"Cbuf_AddText",82370100 }
        };

        static async Task Main(string[] args)
        {
            if (args.Length < 2 || !File.Exists(args[0]))
            {
                Console.WriteLine("❌ Usage: dotnet run -- \"file.gsc\" <game>");
                return;
            }

            string gscPath = args[0];
            string game = args[1].ToLower();

            if (!GameOffsets.ContainsKey(game) && game != "qos")
            {
                Console.WriteLine($"❌ Unknown game: {game}");
                Console.WriteLine("Available: " + string.Join(", ", GameOffsets.Keys) + ", qos");
                return;
            }

            string gscContent = File.ReadAllText(gscPath);

            Console.WriteLine("=== GSC Injector ===");
            Console.WriteLine($"Selected game: {game}");

            while (true)
            {
                try
                {
                    if (!xboxConsole.Connect(out xboxConsole))
                    {
                        Console.WriteLine("❌ Xbox not detected. Retrying in 2s...");
                        Thread.Sleep(2000);
                        continue;
                    }

                    Console.WriteLine("✅ Connected to Xbox!");
                    Console.WriteLine("Xbox IP: " + xboxConsole.IPAddress);

                    // Handle QOS separately
                    if (game == "cod2")
                    {
                        await Cod2(gscContent);
                    }
                    else if (game == "qos")
                    {
                        await QOSMP(gscContent);
                    }
                    else if (game == "cod4" || game.StartsWith("waw") || game == "007")
                    {
                        await InjectSpecialGSC(gscContent, game);
                    }
                    else
                    {
                        if (InjectGSC(gscContent, game))
                            Console.WriteLine("✅ Injection complete!");
                        else
                            Console.WriteLine("❌ Injection failed.");
                    }

                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine("❌ Error: " + ex.Message);
                    Thread.Sleep(2000);
                }
            }

            Console.WriteLine("Press any key to exit...");
            Console.ReadKey();
        }

        // ==========================
        // QOS-specific injection
        // ==========================


        public static async Task QOSMP(string input)
        {
            if (string.IsNullOrEmpty(input)) return;

            try
            {
                uint COD4Offset = await Task.Run(() =>
                    xboxConsole.Call<uint>(0x821F4750, new object[] { 33, "maps/mp/gametypes/_clientids.gsc" }));

                if (COD4Offset == 0)
                {
                    Console.WriteLine("❌ QOS asset not found!");
                    return;
                }

                uint injectAddress = 0x84000000;
                xboxConsole.WriteUInt32(COD4Offset + 8, injectAddress);
                xboxConsole.WriteString(injectAddress, input);

                Console.WriteLine("✅ QOS GSC injected successfully!");
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ QOS injection error: " + ex.Message);
            }
        }

        public static async Task Cod2(string rawText)
        {
            if (string.IsNullOrEmpty(rawText)) return;

            try
            {
                // Find the GSC asset
                uint COD2Offset = await Task.Run(() =>
                    xboxConsole.Call<uint>(0x823F2440, new object[] { 23, "maps/mp/gametypes/_clientids.gsc" }));

                if (COD2Offset == 0)
                {
                    Console.WriteLine("❌ COD2 asset not found!");
                    return;
                }

                // Prepare memory buffer
                byte[] bytes = System.Text.Encoding.ASCII.GetBytes(rawText + "\0");
                uint buf = 0x83200000;
                xboxConsole.SetMemory(buf, bytes);
                int newLen = bytes.Length - 1;

                // Patch size + buffer pointer
                xboxConsole.WriteUInt32(COD2Offset + 0x4, (uint)newLen);
                xboxConsole.WriteUInt32(COD2Offset + 0x8, buf);

                Console.WriteLine("✅ COD2 GSC injected successfully!");

                // 🔹 Run cbuf_addtext right after injection
                //SendCbuf("xpartygo");
                SendCbuf("xpartygo");// or any command you want

            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ Injection error: " + ex.Message);
            }
        }
        static void SendCbuf(string command)
        {
            if (string.IsNullOrEmpty(command)) return;

            try
            {
                // Call the engine's cbuf_addtext
                // First param = local client (0 = host), second = command string
                xboxConsole.Call<uint>(0x82370100, new object[] {command });


                Console.WriteLine($"✅ Sent to CBUF: {command}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"❌ cbuf_addtext failed: {ex.Message}");
            }
        }


        // Normal inject
        static bool InjectGSC(string rawText, string game)
        {
            if (string.IsNullOrEmpty(rawText)) return false;

            uint gameOffset = GameOffsets[game];
            uint NX1Offset = xboxConsole.Call<uint>(gameOffset, new object[] { 36, "maps/mp/gametypes/_dev.gsc" });
            if (NX1Offset == 0)
            {
                Console.WriteLine("❌ Asset not found!");
                return false;
            }

            uint injectAddress = 0x84000000;
            byte[] data;

            if (game == "bo2" || game == "nx1")
                data = ZlibCompressString(rawText);
            else
                data = Encoding.UTF8.GetBytes(rawText);

            try
            {
                xboxConsole.WriteUInt32(NX1Offset + 4, (uint)data.Length);
                xboxConsole.WriteUInt32(NX1Offset + 8, (uint)rawText.Length);
                xboxConsole.WriteUInt32(NX1Offset + 12, injectAddress);

                byte[] zeroData = new byte[data.Length + 2];
                xboxConsole.SetMemory(injectAddress, zeroData);
                xboxConsole.SetMemory(injectAddress, data);

                Console.WriteLine($"Raw: {rawText.Length} bytes\nInjected: {data.Length} bytes\nAddr: 0x{injectAddress:X8}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ Injection error: " + ex.Message);
                return false;
            }
        }

        // COD4/WAW/007 injection
        public static async Task InjectSpecialGSC(string input, string game)
        {
            if (string.IsNullOrEmpty(input)) return;

            uint offset = GameOffsets[game];
            int callParam = (game.StartsWith("waw") || game == "007") ? 33 : 32;

            uint assetOffset = await Task.Run(() =>
                xboxConsole.Call<uint>(offset, new object[] { callParam, "maps/mp/gametypes/_clientids.gsc" }));

            if (assetOffset == 0)
            {
                Console.WriteLine("❌ Asset not found!");
                return;
            }

            uint injectAddress = 0x84000000;

            try
            {
                xboxConsole.WriteUInt32(assetOffset + 8, injectAddress);
                xboxConsole.WriteString(injectAddress, input);
                Console.WriteLine("✅ Special injection complete!");
            }
            catch (Exception ex)
            {
                Console.WriteLine("❌ Special injection error: " + ex.Message);
            }
        }

        static byte[] ZlibCompressString(string input)
        {
            using (var ms = new MemoryStream())
            {
                using (var zlib = new ZlibStream(ms, CompressionMode.Compress, CompressionLevel.BestCompression))
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(input);
                    zlib.Write(bytes, 0, bytes.Length);
                }
                return ms.ToArray();
            }
        }
    }
}