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
    const int VM_REGION_BASIC_INFO_64 = 9;
    const int VM_REGION_BASIC_INFO_COUNT_64 = 10;
    const int VM_PROT_READ = 0x01;
    static ulong cbase = 0;
    static int pid = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct vm_region_basic_info_64 {
        public int protection;
        public int max_protection;
        public int inheritance;
        public int shared;
        public int reserved;
        public ulong offset;
        public int behavior;
        public short user_wired_count;
        public short user_tag;
    }

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
        
    [DllImport(LIBSYSTEM)]
    public static extern int mach_vm_region(IntPtr task,
        ref ulong address,
        out ulong size,
        int flavor,
        IntPtr info,
        ref uint count,
        out ulong object_name
    );

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
            //Console.WriteLine($"mach_vm_read failed with code {result}");
            return new byte[0];
        }

        byte[] buffer = new byte[size];
        Marshal.Copy(bufferPtr, buffer, 0, (int)size);

        // Clean up
        vm_deallocate(localTask, bufferPtr, size);

        return buffer;
    }
    
    public static ulong FindCemuBase(int pid, ulong minSize) {
        IntPtr task;
        IntPtr localTask = mach_task_self();
        byte?[] patternBytes = new byte?[] { 0x02, 0xD4, 0xE7 };

        int result = task_for_pid(localTask, pid, out task);
        if (result != KERN_SUCCESS) {
            Console.WriteLine($"task_for_pid failed: {result}");
            return 0;
        }

        ulong address = 0;
        while (true) {
            ulong size;
            uint count = VM_REGION_BASIC_INFO_COUNT_64;
            IntPtr infoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<vm_region_basic_info_64>());
            ulong objectName;

            result = mach_vm_region(task,
                ref address,
                out size,
                VM_REGION_BASIC_INFO_64,
                infoPtr,
                ref count,
                out objectName
            );

            if (result != KERN_SUCCESS) {
                Marshal.FreeHGlobal(infoPtr);
                break;
            }

            var regionInfo = Marshal.PtrToStructure<vm_region_basic_info_64>(infoPtr);
            Marshal.FreeHGlobal(infoPtr);

            bool readable = (regionInfo.protection & VM_PROT_READ) != 0;

            if (readable && size < minSize) {
                var bytes = ReadProcessMemory(pid, (ulong)(address + 0xE000000), 20);

                if (bytes != null && bytes.Length > 0) {
                    int patternLen = patternBytes.Length;
                    for (int i = 0, j = 0; i + patternLen <= 20; i++) {
                        if (patternBytes[j] == null || bytes[i] == patternBytes[j]) {
                            j++;
                        } else {
                            j = 0;
                        }

                        if (j >= patternLen) {
                            return address;
                        }
                    }
                }
            }

            address += size;
        }
        return 0;
    }

    static byte[] readBytes(uint address, uint length) {
        return ReadProcessMemory(pid, (ulong)(cbase + 0xE000000) + address - 0x10000000, length);
    }

    static uint readUInt32(uint address) {
        return BitConverter.ToUInt32(ReadProcessMemory(pid, (ulong)(cbase + 0xE000000) + address - 0x10000000, 4).Reverse().ToArray(), 0);
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

        cbase = FindCemuBase(pid, 1308622848);
        if (cbase == 0) {
            Console.WriteLine("Could not find Cemu base...");
            return;
        }

        Console.WriteLine("MacGrabber by CrafterPika.");
        Console.WriteLine("Special Thanks: Javi, Tombuntu and Winterberry.\n");
        Console.WriteLine($"Found Cemu base: 0x{cbase:X}");
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
