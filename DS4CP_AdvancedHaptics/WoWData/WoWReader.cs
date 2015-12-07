﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DS4ConsolePort;
using DS4ConsolePort.WoWData;

namespace DS4ConsolePort.WoWData
{
    public class WoWReader : IDisposable
    {
        [DllImport("kernel32.dll")]
        public static extern IntPtr OpenProcess(int dwDesiredAccess, bool bInheritHandle, int dwProcessId);

        [DllImport("kernel32.dll")]
        public static extern bool ReadProcessMemory(IntPtr hProcess,
            IntPtr lpBaseAddress, byte[] lpBuffer, int dwSize, ref int lpNumberOfBytesRead);

        private const int PROCESS_WM_READ = 0x0010;
        private OffsetReader offsetReader = new OffsetReader();
        private OffsetFile.GameOffset offsets;

        public bool IsAttached { get; private set; }
        public bool OffsetsLoaded { get; private set; }

        private Thread watcherThread;
        private Process wowProcess;
        private IntPtr wowHandle;

        public WoWReader()
        {
            watcherThread = new Thread(WatcherThread);
            watcherThread.Start();
            //Offsets.WriteOffsets();
        }

        public void Dispose()
        {
            watcherThread.Abort();
        }

        public void WatcherThread()
        {
            while (true)
            {
                var wowProcs = Process.GetProcessesByName("WoW-64");
                if (!IsAttached)
                {
                    // No process attached, scan for WoW process
                    if (wowProcs.Length > 0) // found process
                    {
                        wowProcess = wowProcs.First();
                        var handle = OpenProcess(PROCESS_WM_READ, true, wowProcess.Id);
                        if (handle != IntPtr.Zero)
                        {
                            try
                            {
                                var wowBuild = wowProcess.MainModule.FileVersionInfo.ProductPrivatePart;
                                if (offsetReader.BuildExists(wowBuild, 64))
                                {
                                    offsets = offsetReader.LoadOffsets(wowBuild, 64);
                                    OffsetsLoaded = true;
                                }
                                else
                                {
                                    wowHandle = IntPtr.Zero;
                                    IsAttached = false;
                                    OffsetsLoaded = false;
                                }
                                wowHandle = handle;
                                IsAttached = true;
                                Console.WriteLine("WoW Process Attached");
                            } catch(Exception ex)
                            {
                                Console.WriteLine("Failed to attach to process: " + ex.Message);
                                IsAttached = false;
                                wowHandle = IntPtr.Zero;
                            }
                            
                        }
                    }
                }
                else if (wowProcess != null)
                {
                    // Check if process has exited
                    if (wowProcess.HasExited)
                    {
                        wowHandle = IntPtr.Zero;
                        wowProcess = null;
                        IsAttached = false;
                        Console.WriteLine("WoW Process Exited");
                    }
                }
                else if (wowProcess == null)
                {
                    wowHandle = IntPtr.Zero;
                    IsAttached = false;
                }
                Thread.Sleep(100);
            }
        }

        public WoWState GameState
        {
            get
            {
                if (IsAttached)
                {
                    byte[] state = new byte[1];
                    int bytesRead = 0;
                    try
                    {
                        ReadProcessMemory(wowHandle, wowProcess.MainModule.BaseAddress + offsets.ReadOffset("GameState"), state, state.Length, ref bytesRead);
                    } catch
                    {
                        return WoWState.NotRunning;
                    }
                    
                    if (bytesRead > 0)
                    {
                        return (WoWState)state[0];
                    }
                }
                return WoWState.NotRunning;
            }
        }

        public byte[] CurrentTarget
        {
            get
            {
                byte[] targetGuid = new byte[16];
                int bytesRead = 0;
                ReadProcessMemory(wowHandle, ReadPlayerBase() + 0x08 + offsets.ReadOffset("TargetGuid"), targetGuid, targetGuid.Length, ref bytesRead);
                return targetGuid;
            }
        }

        public PlayerInfo ReadPlayerInfo()
        {
            if (IsAttached && GameState == WoWState.LoggedIn)
            {
                var PlayerBase = ReadPlayerBase();
                if(PlayerBase != IntPtr.Zero)
                {
                    int bytesRead = 0;
                    byte[] Name = new byte[12];
                    byte[] Class = new byte[1];
                    byte[] Level = new byte[1];
                    byte[] CurrentHP = new byte[4];
                    byte[] MaxHP = new byte[4];
                    
                    try
                    {
                        ReadProcessMemory(wowHandle, wowProcess.MainModule.BaseAddress + offsets.ReadOffset("Name"), Name, Name.Length, ref bytesRead);
                        ReadProcessMemory(wowHandle, wowProcess.MainModule.BaseAddress + offsets.ReadOffset("Class"), Class, Class.Length, ref bytesRead);
                        ReadProcessMemory(wowHandle, PlayerBase + offsets.ReadOffset("Level"), Level, Level.Length, ref bytesRead);
                        ReadProcessMemory(wowHandle, PlayerBase + offsets.ReadOffset("CurrentHP"), CurrentHP, CurrentHP.Length, ref bytesRead);
                        ReadProcessMemory(wowHandle, PlayerBase + offsets.ReadOffset("MaxHP"), MaxHP, MaxHP.Length, ref bytesRead);
                    }
                    catch
                    {
                        return default(PlayerInfo);
                    }


                    return new PlayerInfo()
                    {
                        Name = Encoding.Default.GetString(Name),
                        Class = (WoWClass)Class[0],
                        Level = Level[0],
                        CurrentHP = BitConverter.ToUInt32(CurrentHP, 0),
                        MaxHP = BitConverter.ToUInt32(MaxHP, 0)
                    };
                }
            }
            return default(PlayerInfo);
        }

        private IntPtr ReadPlayerBase()
        {
            if (IsAttached && GameState == WoWState.LoggedIn)
            {
                byte[] playerBase = new byte[IntPtr.Size];
                int bytesRead = 0;

                // Read playerbase pointer from memory
                try
                {
                    ReadProcessMemory(wowHandle, wowProcess.MainModule.BaseAddress + offsets.ReadOffset("Player"), playerBase, playerBase.Length, ref bytesRead);
                    IntPtr playerPointer = (IntPtr)BitConverter.ToInt64(playerBase, 0);

                    // Read player pointer from memory
                    ReadProcessMemory(wowHandle, playerPointer + 0x08, playerBase, playerBase.Length, ref bytesRead);
                    playerPointer = (IntPtr)BitConverter.ToInt64(playerBase, 0);

                    return playerPointer;
                } catch
                {
                    return IntPtr.Zero;
                }
                
            }
            return IntPtr.Zero;
        }
    }

    public enum WoWState : int
    {
        LoggedOut = 0x00,
        LoggedIn = 0x01,
        NotRunning = 0xFF
    }

    public struct PlayerInfo
    {
        public string Name;
        public WoWClass Class;
        public uint Level;
        public uint CurrentHP;
        public uint MaxHP;
        public string RealmName;
        public string AccountName;
    }

    public enum WoWClass : uint
    {
        None = 0,
        Warrior = 1,
        Paladin = 2,
        Hunter = 3,
        Rogue = 4,
        Priest = 5,
        DeathKnight = 6,
        Shaman = 7,
        Mage = 8,
        Warlock = 9,
        Druid = 11,
    }
}
