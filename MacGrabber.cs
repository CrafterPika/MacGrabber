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
    const int KERN_SUCCESS = 0;
    const string LIBSYSTEM = "/usr/lib/libSystem.B.dylib";

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
        int pid;
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
        string cbase = null;

        if (!File.Exists(cemu_log)) {
            Console.WriteLine("Could not find cemu log.txt in: " + cemu_log);
            return;
        }
        foreach (string line in File.ReadLines(cemu_log)) {
            Match match = Regex.Match(line, pattern);
            if (match.Success) {
                cbase = match.Groups[1].Value;
                break;
            }
        }
        if (cbase == "0") {
            Console.WriteLine("Could not find cemu base address.");
            return;
        }
        ulong cbase2 = Convert.ToUInt64(cbase, 16);

        Console.WriteLine("MacGrabber by CrafterPika.");
        Console.WriteLine("Special Thanks: Javi, Tombuntu and Winterberry.\n");
        Console.WriteLine($"Found Cemu base: {cbase}");
        // Alr now it's getting intresting
        Console.WriteLine("Player X: PID (Hex)| PID (Dec)  | PNID             | Name");
        Console.WriteLine("---------------------------------------------------------");
        for (var i = 0; i < 8; i++) {
            var p = BitConverter.ToUInt32(ReadProcessMemory(pid, cbase2 + 0x101DD330, 4).Reverse().ToArray());
            var p1 = BitConverter.ToUInt32(ReadProcessMemory(pid, cbase2 + (uint)(p + 0x10), 4).Reverse().ToArray());
            var p2 = BitConverter.ToUInt32(ReadProcessMemory(pid, cbase2 + (uint)(p1 + i * 4), 4).Reverse().ToArray());
            var p3 = ReadProcessMemory(pid, cbase2 + (uint)(p2 + 0xd0), 4).Reverse().ToArray(); // PID

            // get name
            var nameBytes = ReadProcessMemory(pid, cbase2 + (uint)(p2 + 0x6), 32);
            var name = Encoding.BigEndianUnicode.GetString(nameBytes);
            name = name.Replace("\n", "").Replace("\r", "");

            string nnidHex = BitConverter.ToString(p3).Replace("-", "");
            string nnidDec = BitConverter.ToInt32(p3, 0).ToString().PadRight(10, ' ');
            string nnidStr = GetPNID(Int32.Parse(nnidDec)).GetAwaiter().GetResult();
            nnidStr = nnidStr.PadRight(16, ' ');
            Console.WriteLine($"Player {i}: {nnidHex} | {nnidDec} | {nnidStr} | {name}");

        }

        // Session ID Grab
        var id_ptr = BitConverter.ToUInt32(ReadProcessMemory(pid, cbase2 + 0x101E8980, 4).Reverse().ToArray());
        if (id_ptr != 0) {
            var index = ReadProcessMemory(pid, cbase2 + (uint)(id_ptr + 0xBD), 1)[0];
            var sessionID = BitConverter.ToUInt32(ReadProcessMemory(pid, cbase2 + (uint)(id_ptr + index + 0xCC), 4).Reverse().ToArray());
            Console.WriteLine($"\nSession ID: {sessionID:X8} (Dec: {sessionID})"); 
        } else {
            Console.WriteLine($"\nSession ID: None");
        }

        string now = DateTime.Now.ToString();
        Console.WriteLine($"\nFetched at: {now}");

        return;
    }
}