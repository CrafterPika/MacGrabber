/*
    * This is a straight port of LinuxGrabber to MacOS
    * By CrafterPika
    * Original src: https://github.com/c8ff/LinuxGrabber/blob/main/LinuxGrabber.cs
*/

using System;
using System.Text;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Net.Http;
using System.Threading.Tasks;
using System.Xml.Linq;

public class MacGrabber {
    // vars
    const int KERN_SUCCESS = 0;
    const string LIBSYSTEM = "/usr/lib/libSystem.B.dylib";
    static ulong cbase = 0;
    static int pid = 0;

    // https://github.com/attilathedud/macos_task_for_pid#overview
    // Well documenated apis, thanks Apple.
    [DllImport(LIBSYSTEM)]
    static extern int task_for_pid(IntPtr task,
        int pid,
        out IntPtr targetTask);

    [DllImport(LIBSYSTEM)]
    static extern IntPtr mach_task_self();

    [DllImport(LIBSYSTEM)]
    static extern int mach_vm_read(IntPtr target_task,
        ulong address,
        ulong size,
        out IntPtr data,
        out ulong data_count);

    [DllImport(LIBSYSTEM)]
    static extern int vm_deallocate(IntPtr task,
        IntPtr address,
        ulong size);

    static byte[] ReadProcessMemory(int pid, ulong address, ulong length) {
        IntPtr task;
        IntPtr localTask = mach_task_self();

        int result = task_for_pid(localTask, pid, out task);
        if (result != KERN_SUCCESS) {
            Console.WriteLine($"task_for_pid failed with code {result}");
            return new byte[0];
        }

        IntPtr bufferPtr;
        ulong size;
        result = mach_vm_read(task, address, length, out bufferPtr, out size);
        if (result != KERN_SUCCESS) {
            Console.WriteLine($"mach_vm_read failed with code {result}");
            return new byte[0];
        }

        byte[] buffer = new byte[size];
        Marshal.Copy(bufferPtr, buffer, 0, (int)size);

        // Clean up
        vm_deallocate(localTask, bufferPtr, size);

        return buffer;
    }

    static byte[] readBytes(uint address, uint length) {
        return ReadProcessMemory(pid, cbase + address, length);
    }

    static uint readUInt32(uint address) {
        return BitConverter.ToUInt32(ReadProcessMemory(pid, cbase + address, 4).Reverse().ToArray(), 0);
    }

    static async Task<string> GetPNID(int pid) {
        using (HttpClient client = new HttpClient()) {
            client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0");
            // Thanks @tombun2 for pointing this out
            // src: https://github.com/Milk-Cool/PretendoLookup/blob/932263e2c5e7f2e7527ff5dc690a8701dbf39885/miidata.js
            client.DefaultRequestHeaders.Add("X-Nintendo-Client-ID", "a2efa818a34fa16b8afbc8a74eba3eda");
            client.DefaultRequestHeaders.Add("X-Nintendo-Client-Secret", "c91cdb5658bd4954ade78533a339cf9a");

            try {
                // src: https://github.com/kinnay/NintendoClients/wiki/Account-Server
                HttpResponseMessage response = await client.GetAsync($"http://account.pretendo.cc/v1/api/miis?pids={pid}");
                if (response.IsSuccessStatusCode) {
                    string content = await response.Content.ReadAsStringAsync();
                    XDocument doc = XDocument.Parse(content);
                    string userId = doc.Root?.Element("mii")?.Element("user_id")?.Value;
                    return userId;
                } else {
                    return "0";
                }
            } catch (Exception ex) {
                Console.WriteLine(ex.Message);
                return "0";
            }
        }
    }

    static void Main(string[] args) {
        Console.OutputEncoding = System.Text.Encoding.UTF8;

        // get the cemu process
        string[] processNames = {"cemu", "cemu_release"};
        Process targetProcess = Process.GetProcesses()
            .FirstOrDefault(p => processNames.Contains(p.ProcessName.ToLower()));
        if (targetProcess == null) {
            Console.WriteLine("Could not find Cemu process.");
            return;
        }
        pid = targetProcess.Id;

        // This is kind of janky but idk how else to reliably do it
        var startInfo = new ProcessStartInfo {
            FileName = "ps",
            Arguments = $"-o user= -p {pid}",
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        var ps = Process.Start(startInfo);
        string username = ps.StandardOutput.ReadToEnd().Trim();
        ps.WaitForExit();

        // Find Cemu's base address
        string cemu_log = $"/Users/{username}/Library/Application Support/Cemu/log.txt";
        string pattern = @"base:\s*(0x[0-9A-Fa-f]+)";
        string patternMatch = null;

        if (!File.Exists(cemu_log)) {
            Console.WriteLine("Could not find cemu log.txt in: " + cemu_log);
            return;
        }
        foreach (string line in File.ReadLines(cemu_log)) {
            Match match = Regex.Match(line, pattern);
            if (match.Success) {
                patternMatch = match.Groups[1].Value;
                break;
            }
        }
        if (patternMatch == null) {
            Console.WriteLine("Could not find cemu base address.");
            return;
        }
        cbase = Convert.ToUInt64(patternMatch, 16);

        Console.WriteLine("MacGrabber by CrafterPika.");
        Console.WriteLine("Special Thanks: Javi, Tombuntu and Winterberry.\n");
        Console.WriteLine($"Found Cemu base: {patternMatch}");
        // Alr now it's getting intresting
        Console.WriteLine("Player X: PID (Hex)| PID (Dec)  | PNID             | Name");
        Console.WriteLine("---------------------------------------------------------");
        for (var i = 0; i < 8; i++) {
            var p = readUInt32((uint)(readUInt32((uint)(readUInt32(0x101DD330) + 0x10)) + i * 4)); //PlayerInfo Pointer
            var p2 = readBytes((uint)(p + 0xd0), 4).Reverse().ToArray(); // PID
            string name = Encoding.BigEndianUnicode.GetString(readBytes((uint)(p + 0x6), 32)).Replace("\n", "").Replace("\r", ""); // Player Name

            string nnidHex = BitConverter.ToString(p2).Replace("-", "");
            string nnidDec = BitConverter.ToUInt32(p2, 0).ToString().PadRight(10, ' ');
            string nnidStr = GetPNID(Int32.Parse(nnidDec)).GetAwaiter().GetResult().PadRight(16, ' ');
            Console.WriteLine($"Player {i}: {nnidHex} | {nnidDec} | {nnidStr} | {name}");
        }

        // Session ID Grab
        var id_ptr = readUInt32(0x101E8980);
        if (id_ptr != 0) {
            var index = readBytes((uint)(id_ptr + 0xBD), 1)[0];
            var sessionID = readUInt32((uint)(id_ptr + index + 0xCC));
            Console.WriteLine($"\nSession ID: {sessionID:X8} (Dec: {sessionID})"); 
        } else {
            Console.WriteLine($"\nSession ID: None");
        }

        string now = DateTime.Now.ToString();
        Console.WriteLine($"\nFetched at: {now}");

        return;
    }
}
