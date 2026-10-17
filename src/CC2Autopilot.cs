// CC2 Autopilot — desktop toggle for Carrier Command 2 (prototype).
// Switches the player's carrier between manual and the game's own auto (AI) control by writing
// the control-mode field of the server-side carrier object in the running game's memory.
//
// Carrier record (offsets from the hitpoints field, found by comparing player and AI carriers):
//   -28 vehicle id, -24 team id, -20/-16/-12 zero links, +0 hitpoints, +24 control mode
//   (1 = manual, 0 = auto), +140 fuel remaining (float), +144 fuel capacity 50000 (float).
// The game keeps several copies of the record; all copies of our carrier are written.
//
// Build: csc /target:winexe /out:CC2Autopilot.exe CC2Autopilot.cs

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

static class Native
{
    public const uint PROCESS_VM_READ = 0x0010, PROCESS_VM_WRITE = 0x0020, PROCESS_VM_OPERATION = 0x0008,
        PROCESS_QUERY_INFORMATION = 0x0400;
    public const uint MEM_COMMIT = 0x1000, MEM_PRIVATE = 0x20000, MEM_IMAGE = 0x1000000, PAGE_READWRITE = 0x04,
        PAGE_WRITECOPY = 0x08;

    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORY_BASIC_INFORMATION
    {
        public IntPtr BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public IntPtr RegionSize;
        public uint State, Protect, Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")]
    public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")]
    public static extern IntPtr VirtualQueryEx(IntPtr h, IntPtr addr, out MEMORY_BASIC_INFORMATION mbi, IntPtr len);
    [DllImport("kernel32.dll")]
    public static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
    [DllImport("kernel32.dll")]
    public static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
}

class CarrierRecord
{
    public long Address;      // address of the hitpoints field
    public int VehicleId, TeamId, Mode;
}

static class GameMemory
{
    const int ModeOffset = 24, CapOffset = 144, FuelOffset = 140, IdOffset = -28, TeamOffset = -24;
    static readonly byte[] Cap = BitConverter.GetBytes(50000.0f);

    public static Process FindGame()
    {
        Process[] list = Process.GetProcessesByName("carrier_command");
        return list.Length > 0 ? list[0] : null;
    }

    static bool IsRecord(byte[] d, int i)
    {
        if (i + IdOffset < 0 || i + CapOffset + 4 > d.Length) return false;
        for (int k = 0; k < 4; k++) if (d[i + CapOffset + k] != Cap[k]) return false;
        int hp = BitConverter.ToInt32(d, i);
        float fuel = BitConverter.ToSingle(d, i + FuelOffset);
        int mode = BitConverter.ToInt32(d, i + ModeOffset);
        int id = BitConverter.ToInt32(d, i + IdOffset);
        int team = BitConverter.ToInt32(d, i + TeamOffset);
        if (hp <= 0 || hp > 100000 || fuel < 0f || fuel > 50000f || mode < 0 || mode > 3) return false;
        if (id <= 0 || id > 1000000 || team < 0 || team > 64) return false;
        return BitConverter.ToInt32(d, i - 20) == 0 && BitConverter.ToInt32(d, i - 16) == 0
            && BitConverter.ToInt32(d, i - 12) == 0;
    }

    public static List<CarrierRecord> Scan(IntPtr h)
    {
        var result = new List<CarrierRecord>();
        long addr = 0;
        Native.MEMORY_BASIC_INFORMATION mbi;
        IntPtr mbiSize = (IntPtr)Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION));
        const int Chunk = 16 << 20, Overlap = 256;
        while (addr < 0x7FFFFFFFFFFF && Native.VirtualQueryEx(h, (IntPtr)addr, out mbi, mbiSize) != IntPtr.Zero)
        {
            long baseAddr = mbi.BaseAddress.ToInt64(), size = mbi.RegionSize.ToInt64();
            if (mbi.State == Native.MEM_COMMIT && mbi.Type == Native.MEM_PRIVATE && mbi.Protect == Native.PAGE_READWRITE)
            {
                for (long off = 0; off < size; off += Chunk)
                {
                    long start = Math.Max(0, off - Overlap);
                    int len = (int)Math.Min(Chunk + Overlap, size - start);
                    byte[] d = new byte[len];
                    IntPtr read;
                    if (!Native.ReadProcessMemory(h, (IntPtr)(baseAddr + start), d, (IntPtr)len, out read)) continue;
                    int n = (int)read;
                    for (int j = 0; j + 4 <= n; j += 4)
                    {
                        if (d[j] != Cap[0] || d[j + 1] != Cap[1] || d[j + 2] != Cap[2] || d[j + 3] != Cap[3]) continue;
                        int i = j - CapOffset;
                        if (off > 0 && start + j < off) continue; // already handled by the previous chunk
                        if (!IsRecord(d, i)) continue;
                        result.Add(new CarrierRecord {
                            Address = baseAddr + start + i,
                            VehicleId = BitConverter.ToInt32(d, i + IdOffset),
                            TeamId = BitConverter.ToInt32(d, i + TeamOffset),
                            Mode = BitConverter.ToInt32(d, i + ModeOffset) });
                    }
                }
            }
            addr = baseAddr + size;
        }
        return result;
    }

    // Server-side vehicle object (offsets taken from the game's own save serializer):
    //   +0x08 team id, +0x80 vehicle id (also at +0x108 / +0x19C), +0x180 hitpoints,
    //   +0x188 preferred_bay_index (255), +0x18C special_id (0),
    //   +0x1D0 control_mode (1 manual, 0 auto). Writing the client copies does nothing: the server
    //   object is the one the simulation uses.
    public const int SrvTeam = 0x08, SrvId = 0x80, SrvHp = 0x180, SrvBay = 0x188, SrvSpecial = 0x18C, SrvMode = 0x1D0, SrvSize = 0x230;

    // First field of the server vehicle object = class pointer (vtable) inside carrier_command.exe:
    // RVA 0x9596CC (seen as 0xA796CC with the module loaded at 0x120000). Computed from the real module base.
    public const long VtableRva = 0x9596CC;

    public static uint ExpectedVtable(Process game)
    {
        return (uint)(game.MainModule.BaseAddress.ToInt64() + VtableRva);
    }

    public static List<long> FindServerObjects(IntPtr h, int vehicleId, int teamId, uint vtable)
    {
        var result = new List<long>();
        long addr = 0;
        Native.MEMORY_BASIC_INFORMATION mbi;
        IntPtr mbiSize = (IntPtr)Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION));
        const int Chunk = 16 << 20;
        while (addr < 0x7FFFFFFFFFFF && Native.VirtualQueryEx(h, (IntPtr)addr, out mbi, mbiSize) != IntPtr.Zero)
        {
            long baseAddr = mbi.BaseAddress.ToInt64(), size = mbi.RegionSize.ToInt64();
            if (mbi.State == Native.MEM_COMMIT && mbi.Type == Native.MEM_PRIVATE && mbi.Protect == Native.PAGE_READWRITE)
            {
                for (long off = 0; off < size; off += Chunk)
                {
                    int len = (int)Math.Min(Chunk + SrvSize, size - off);
                    byte[] d = new byte[len];
                    IntPtr read;
                    if (!Native.ReadProcessMemory(h, (IntPtr)(baseAddr + off), d, (IntPtr)len, out read)) continue;
                    int n = (int)read;
                    int last = Math.Min(n - SrvSize, Chunk);
                    for (int i = 0; i < last; i += 4)
                    {
                        if (BitConverter.ToUInt32(d, i) != vtable) continue;
                        if (d[i + SrvBay] != 255) continue;
                        if (BitConverter.ToInt32(d, i + SrvId) != vehicleId) continue;
                        if (BitConverter.ToInt32(d, i + SrvTeam) != teamId) continue;
                        if (BitConverter.ToInt32(d, i + SrvSpecial) != 0) continue;
                        int mode = BitConverter.ToInt32(d, i + SrvMode);
                        int hp = BitConverter.ToInt32(d, i + SrvHp);
                        if (mode < 0 || mode > 3 || hp <= 0 || hp > 100000) continue;
                        result.Add(baseAddr + off + i);
                    }
                }
            }
            addr = baseAddr + size;
        }
        return result;
    }

    public static bool WriteServerMode(IntPtr h, long obj, int mode)
    {
        byte[] buf = BitConverter.GetBytes(mode);
        IntPtr written;
        return Native.WriteProcessMemory(h, (IntPtr)(obj + SrvMode), buf, (IntPtr)4, out written)
            && written.ToInt64() == 4;
    }

    public static int ReadServerMode(IntPtr h, long obj)
    {
        byte[] buf = new byte[4];
        IntPtr read;
        if (!Native.ReadProcessMemory(h, (IntPtr)(obj + SrvMode), buf, (IntPtr)4, out read)) return -1;
        return BitConverter.ToInt32(buf, 0);
    }
}

// Carrier main gun salvo size = game constant turret_carrier_main_gun_salvo_count (5) from
// rom_0/scripts/constants.txt, loaded into memory. Located by its neighbours in that file:
// heavy_cannon 12, carrier_main_gun_capacity 20, SALVO, plane_chaingun 400, ciws 400, missile 4,
// carrier_ciws 100, carrier_missile 4, silo 6, flare 10, rocket_pod 19, torpedo 2, decoy 6.
// Stored either as int32 or float32; the salvo slot itself is a wildcard (it may already be changed).
static class SalvoMemory
{
    static readonly int[] Pattern = { 12, 20, -1, 400, 400, 4, 100, 4, 6, 10, 19, 2, 6 };
    const int SalvoIndex = 2;

    static bool Match(byte[] d, int i, bool asFloat)
    {
        for (int k = 0; k < Pattern.Length; k++)
        {
            int at = i + 4 * k;
            if (k == SalvoIndex)
            {
                double v = asFloat ? BitConverter.ToSingle(d, at) : BitConverter.ToInt32(d, at);
                if (v < 1 || v > 50) return false;
                continue;
            }
            if (asFloat ? BitConverter.ToSingle(d, at) != Pattern[k] : BitConverter.ToInt32(d, at) != Pattern[k]) return false;
        }
        return true;
    }

    // returns address of the salvo value and whether it is stored as float; -1 if not found
    public static long Find(IntPtr h, out bool isFloat)
    {
        isFloat = false;
        long addr = 0;
        Native.MEMORY_BASIC_INFORMATION mbi;
        IntPtr mbiSize = (IntPtr)Marshal.SizeOf(typeof(Native.MEMORY_BASIC_INFORMATION));
        int patBytes = Pattern.Length * 4;
        byte[] first = BitConverter.GetBytes(Pattern[0]);
        byte[] firstF = BitConverter.GetBytes((float)Pattern[0]);
        while (addr < 0x7FFFFFFFFFFF && Native.VirtualQueryEx(h, (IntPtr)addr, out mbi, mbiSize) != IntPtr.Zero)
        {
            long baseAddr = mbi.BaseAddress.ToInt64(), size = mbi.RegionSize.ToInt64();
            bool writable = mbi.Protect == Native.PAGE_READWRITE || mbi.Protect == Native.PAGE_WRITECOPY;
            if (mbi.State == Native.MEM_COMMIT && writable && size <= (256L << 20)
                && (mbi.Type == Native.MEM_PRIVATE || mbi.Type == Native.MEM_IMAGE))
            {
                byte[] d = new byte[size];
                IntPtr read;
                if (Native.ReadProcessMemory(h, (IntPtr)baseAddr, d, (IntPtr)size, out read))
                {
                    int n = (int)read;
                    for (int i = 0; i + patBytes <= n; i += 4)
                    {
                        if (d[i] == first[0] && d[i + 1] == first[1] && d[i + 2] == first[2] && d[i + 3] == first[3]
                            && Match(d, i, false))
                        { isFloat = false; return baseAddr + i + 4 * SalvoIndex; }
                        if (d[i] == firstF[0] && d[i + 1] == firstF[1] && d[i + 2] == firstF[2] && d[i + 3] == firstF[3]
                            && Match(d, i, true))
                        { isFloat = true; return baseAddr + i + 4 * SalvoIndex; }
                    }
                }
            }
            addr = baseAddr + size;
        }
        return -1;
    }

    public static bool Write(IntPtr h, long at, bool isFloat, int value)
    {
        byte[] buf = isFloat ? BitConverter.GetBytes((float)value) : BitConverter.GetBytes(value);
        IntPtr written;
        return Native.WriteProcessMemory(h, (IntPtr)at, buf, (IntPtr)4, out written) && written.ToInt64() == 4;
    }

    public static int Read(IntPtr h, long at, bool isFloat)
    {
        byte[] buf = new byte[4];
        IntPtr read;
        if (!Native.ReadProcessMemory(h, (IntPtr)at, buf, (IntPtr)4, out read)) return -1;
        return isFloat ? (int)BitConverter.ToSingle(buf, 0) : BitConverter.ToInt32(buf, 0);
    }
}

static class L
{
    public static readonly bool Ru = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru";
    public static string T(string ru, string en) { return Ru ? ru : en; }
}

class AutopilotForm : Form
{
    // salvo selector (1..5) under the autopilot button
    int salvo = 0;                // 0 = unknown until read from the game
    long salvoAddr = -1;
    bool salvoIsFloat;
    Rectangle[] salvoRects = new Rectangle[5];
    string salvoStatus = "";
    bool showSalvo;               // off by default: changes combat, single-player only
    const int HeightCompact = 170, HeightWithSalvo = 222;
    const int ModeAuto = 0, ModeManual = 1;
    bool isOn;
    string status = L.T("нажмите, чтобы включить", "click to engage");
    int myVehicleId = -1, myTeamId = -1;
    List<long> myObjects = new List<long>();
    Rectangle buttonRect = new Rectangle(20, 34, 180, 92);
    Point dragStart;
    bool dragging;
    readonly Timer pollTimer = new Timer();
    readonly string statePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "CC2Autopilot", "state.txt");

    public AutopilotForm()
    {
        Text = "CC2 Autopilot";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Location = new Point(Screen.PrimaryScreen.WorkingArea.Right - 240, Screen.PrimaryScreen.WorkingArea.Bottom - HeightWithSalvo - 20);
        Size = new Size(220, HeightCompact);
        for (int k = 0; k < 5; k++) salvoRects[k] = new Rectangle(20 + k * 37, 178, 32, 32);
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.FromArgb(38, 42, 46);
        LoadState();
        ApplySalvoVisibility();

        var menu = new ContextMenu();
        var salvoItem = new MenuItem(L.T("Залп главного калибра (только одиночная игра)", "Main gun salvo (single-player only)"));
        salvoItem.Checked = showSalvo;
        salvoItem.Click += delegate { showSalvo = !showSalvo; salvoItem.Checked = showSalvo; ApplySalvoVisibility(); SaveState(); };
        menu.MenuItems.Add(L.T("Поверх всех окон", "Always on top"), delegate(object s, EventArgs e) {
            TopMost = !TopMost; ((MenuItem)s).Checked = TopMost; });
        menu.MenuItems[0].Checked = true;
        menu.MenuItems.Add(salvoItem);
        menu.MenuItems.Add(L.T("Выход", "Exit"), delegate { Close(); });
        ContextMenu = menu;

        pollTimer.Interval = 1500;
        pollTimer.Tick += delegate { Poll(); };
        pollTimer.Start();
    }

    void ApplySalvoVisibility()
    {
        Size = new Size(220, showSalvo ? HeightWithSalvo : HeightCompact);
        Invalidate();
    }

    void LoadState()
    {
        try
        {
            if (!File.Exists(statePath)) return;
            string[] parts = File.ReadAllText(statePath).Split(' ');
            myVehicleId = int.Parse(parts[0]);
            myTeamId = int.Parse(parts[1]);
            showSalvo = parts.Length > 2 && parts[2] == "salvo";
        }
        catch { myVehicleId = myTeamId = -1; }
    }

    void SaveState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(statePath));
            File.WriteAllText(statePath, myVehicleId + " " + myTeamId + (showSalvo ? " salvo" : ""));
        }
        catch { }
    }

    IntPtr OpenGame(bool write)
    {
        Process p = GameMemory.FindGame();
        if (p == null) { status = L.T("игра не запущена", "game is not running"); return IntPtr.Zero; }
        uint access = Native.PROCESS_VM_READ | Native.PROCESS_QUERY_INFORMATION;
        if (write) access |= Native.PROCESS_VM_WRITE | Native.PROCESS_VM_OPERATION;
        IntPtr h = Native.OpenProcess(access, false, p.Id);
        if (h == IntPtr.Zero) status = L.T("нет доступа к игре", "no access to the game");
        return h;
    }

    void Toggle()
    {
        Cursor = Cursors.WaitCursor;
        status = L.T("ищу авианосец…", "looking for the carrier…");
        Invalidate(); Update();
        IntPtr h = OpenGame(true);
        if (h == IntPtr.Zero) { Cursor = Cursors.Default; Invalidate(); return; }
        try
        {
            // the client record tells which vehicle id is our carrier (ours = manual, AI = auto)
            List<CarrierRecord> clients = GameMemory.Scan(h);
            var mine = clients.FindAll(r => r.Mode == ModeManual);
            var distinct = new HashSet<long>();
            foreach (var r in mine) distinct.Add(((long)r.VehicleId << 8) | (uint)r.TeamId);
            if (distinct.Count > 1)
            {
                // several human carriers in manual mode (multiplayer host): refuse instead of guessing
                status = L.T("несколько авианосцев игроков — не трогаю", "several player carriers — not touching");
                return;
            }
            if (mine.Count == 0 && myVehicleId > 0)
                mine = clients.FindAll(r => r.VehicleId == myVehicleId && r.TeamId == myTeamId);
            if (mine.Count == 0) { status = L.T("авианосец не найден", "carrier not found"); return; }
            myVehicleId = mine[0].VehicleId;
            myTeamId = mine[0].TeamId;
            SaveState();

            myObjects = GameMemory.FindServerObjects(h, myVehicleId, myTeamId, GameMemory.ExpectedVtable(GameMemory.FindGame()));
            if (myObjects.Count == 0) { status = L.T("объект авианосца не найден", "carrier object not found"); return; }
            int current = GameMemory.ReadServerMode(h, myObjects[0]);
            int target = current == ModeAuto ? ModeManual : ModeAuto;
            int ok = 0;
            foreach (long obj in myObjects) if (GameMemory.WriteServerMode(h, obj, target)) ok++;
            if (ok == 0) { status = L.T("не удалось записать", "write failed"); return; }
            isOn = target == ModeAuto;
            status = isOn ? L.T("АВТОПИЛОТ", "AUTOPILOT") : L.T("ручное управление", "manual control");
        }
        finally
        {
            Native.CloseHandle(h);
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    // Launcher mode (Steam launch options: "<path>\CC2Autopilot.exe" %command%): the panel lives as
    // long as the game. Closes after the game process has been gone for a few polls.
    public bool CloseWithGame;
    bool gameSeen;
    int gameMissingPolls;

    void WatchGame()
    {
        if (!CloseWithGame) return;
        if (GameMemory.FindGame() != null) { gameSeen = true; gameMissingPolls = 0; return; }
        if (gameSeen && ++gameMissingPolls >= 4) Close();
    }

    // Background lookup of our carrier (and the salvo constant) so the lamps show the real state
    // without a click: runs when the game is up and nothing is cached yet; retried every ~15 s.
    bool lookupRunning;
    DateTime lastLookup = DateTime.MinValue;

    void StartBackgroundLookup()
    {
        if (lookupRunning || myVehicleId <= 0 || (DateTime.Now - lastLookup).TotalSeconds < 15) return;
        if (myObjects.Count > 0 && (salvoAddr >= 0 || !showSalvo)) return;
        Process game = GameMemory.FindGame();
        if (game == null) return;
        lookupRunning = true;
        lastLookup = DateTime.Now;
        int id = myVehicleId, team = myTeamId;
        System.Threading.ThreadPool.QueueUserWorkItem(delegate
        {
            List<long> objs = new List<long>();
            long sAddr = -1; bool sFloat = false;
            IntPtr h = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_QUERY_INFORMATION, false, game.Id);
            if (h != IntPtr.Zero)
            {
                try
                {
                    objs = GameMemory.FindServerObjects(h, id, team, GameMemory.ExpectedVtable(game));
                    if (showSalvo) sAddr = SalvoMemory.Find(h, out sFloat);
                }
                catch { }
                finally { Native.CloseHandle(h); }
            }
            try
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    if (myObjects.Count == 0 && objs.Count > 0) { myObjects = objs; status = L.T("авианосец найден", "carrier found"); }
                    if (salvoAddr < 0 && sAddr >= 0) { salvoAddr = sAddr; salvoIsFloat = sFloat; }
                    lookupRunning = false;
                    Invalidate();
                });
            }
            catch { lookupRunning = false; }
        });
    }

    void Poll()
    {
        WatchGame();
        StartBackgroundLookup();
        if (salvoAddr >= 0)
        {
            IntPtr hs = OpenGame(false);
            if (hs != IntPtr.Zero)
            {
                int v = SalvoMemory.Read(hs, salvoAddr, salvoIsFloat);
                if (v >= 1 && v <= 50) salvo = v; else { salvoAddr = -1; salvo = 0; }
                Native.CloseHandle(hs);
                Invalidate();
            }
        }
        if (myObjects.Count == 0) return;
        IntPtr h = OpenGame(false);
        if (h == IntPtr.Zero) { isOn = false; myObjects.Clear(); Invalidate(); return; }
        try
        {
            int mode = GameMemory.ReadServerMode(h, myObjects[0]);
            if (mode == ModeAuto && !isOn) { isOn = true; status = L.T("АВТОПИЛОТ", "AUTOPILOT"); }
            else if (mode == ModeManual && isOn) { isOn = false; status = L.T("ручное управление", "manual control"); }
            else if (mode < 0 || mode > 3) { myObjects.Clear(); isOn = false; status = L.T("игра перезагружена — нажмите", "game reloaded — click"); }
        }
        finally { Native.CloseHandle(h); Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var panel = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var pen = new Pen(Color.FromArgb(70, 76, 82), 2)) g.DrawRectangle(pen, panel);

        // hazard stripes like the bridge panels
        using (var stripe = new HatchBrush(HatchStyle.WideDownwardDiagonal, Color.FromArgb(90, 90, 80), Color.FromArgb(38, 42, 46)))
            g.FillRectangle(stripe, 8, 8, 64, 16);
        using (var font = new Font("Consolas", 9f, FontStyle.Bold))
        using (var b = new SolidBrush(Color.FromArgb(200, 205, 210)))
            g.DrawString("AUTOPILOT", font, b, 80, 8);
        using (var font = new Font("Consolas", 9f, FontStyle.Bold))
        using (var b = new SolidBrush(Color.FromArgb(150, 155, 160)))
            g.DrawString("×", font, b, Width - 20, 6);

        // button body
        using (var body = new LinearGradientBrush(buttonRect, Color.FromArgb(70, 75, 80), Color.FromArgb(45, 49, 53), 90f))
            g.FillRectangle(body, buttonRect);
        using (var pen = new Pen(Color.FromArgb(20, 22, 24), 3)) g.DrawRectangle(pen, buttonRect);

        // lamp strip
        var lamp = new Rectangle(buttonRect.X + 30, buttonRect.Y + 18, buttonRect.Width - 60, 14);
        Color lampColor = isOn ? Color.FromArgb(60, 255, 90) : Color.FromArgb(150, 30, 30);
        if (isOn)
            using (var glow = new SolidBrush(Color.FromArgb(70, 60, 255, 90)))
                g.FillRectangle(glow, Rectangle.Inflate(lamp, 6, 6));
        using (var b = new SolidBrush(lampColor)) g.FillRectangle(b, lamp);

        using (var font = new Font("Consolas", 14f, FontStyle.Bold))
        using (var b = new SolidBrush(Color.FromArgb(220, 225, 230)))
        {
            var fmt = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString(isOn ? "AUTO" : "MANUAL", font, b,
                new RectangleF(buttonRect.X, buttonRect.Y + 46, buttonRect.Width, 30), fmt);
        }
        using (var font = new Font("Consolas", 8.5f))
        using (var b = new SolidBrush(isOn ? Color.FromArgb(120, 230, 140) : Color.FromArgb(170, 175, 180)))
        {
            var fmt = new StringFormat { Alignment = StringAlignment.Center };
            g.DrawString(status, font, b, new RectangleF(0, 136, Width, 20), fmt);
        }

        // salvo selector (opt-in)
        if (!showSalvo) return;
        using (var font = new Font("Consolas", 8.5f, FontStyle.Bold))
        using (var b = new SolidBrush(Color.FromArgb(170, 175, 180)))
            g.DrawString("CRR GUN SALVO" + (salvoStatus != "" ? "  " + salvoStatus : ""), font, b, 18, 160);
        for (int k = 0; k < 5; k++)
        {
            Rectangle r = salvoRects[k];
            bool sel = salvo == k + 1;
            using (var body = new LinearGradientBrush(r, Color.FromArgb(70, 75, 80), Color.FromArgb(45, 49, 53), 90f))
                g.FillRectangle(body, r);
            using (var pen = new Pen(Color.FromArgb(20, 22, 24), 2)) g.DrawRectangle(pen, r);
            var lampS = new Rectangle(r.X + 6, r.Y + 4, r.Width - 12, 5);
            using (var lb = new SolidBrush(sel ? Color.FromArgb(60, 255, 90) : Color.FromArgb(60, 64, 68)))
                g.FillRectangle(lb, lampS);
            using (var font = new Font("Consolas", 10f, FontStyle.Bold))
            using (var tb = new SolidBrush(sel ? Color.FromArgb(220, 255, 225) : Color.FromArgb(190, 195, 200)))
            {
                var fmt = new StringFormat { Alignment = StringAlignment.Center };
                g.DrawString((k + 1).ToString(), font, tb, new RectangleF(r.X, r.Y + 11, r.Width, 18), fmt);
            }
        }
    }

    void SetSalvo(int value)
    {
        Cursor = Cursors.WaitCursor;
        IntPtr h = OpenGame(true);
        if (h == IntPtr.Zero) { Cursor = Cursors.Default; salvoStatus = L.T("нет игры", "no game"); Invalidate(); return; }
        try
        {
            if (salvoAddr < 0 || SalvoMemory.Read(h, salvoAddr, salvoIsFloat) < 1)
            {
                salvoStatus = L.T("поиск…", "searching…");
                Invalidate(); Update();
                salvoAddr = SalvoMemory.Find(h, out salvoIsFloat);
            }
            if (salvoAddr < 0) { salvoStatus = L.T("не найдено", "not found"); return; }
            if (SalvoMemory.Write(h, salvoAddr, salvoIsFloat, value)) { salvo = value; salvoStatus = ""; }
            else salvoStatus = L.T("ошибка записи", "write error");
        }
        finally
        {
            Native.CloseHandle(h);
            Cursor = Cursors.Default;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        if (e.X > Width - 24 && e.Y < 24) { Close(); return; }
        if (buttonRect.Contains(e.Location)) { Toggle(); return; }
        if (showSalvo)
            for (int k = 0; k < 5; k++) if (salvoRects[k].Contains(e.Location)) { SetSalvo(k + 1); return; }
        dragging = true;
        dragStart = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (dragging) Location = new Point(Location.X + e.X - dragStart.X, Location.Y + e.Y - dragStart.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e) { dragging = false; }

    // README screenshots: --render <file.png> <manual|auto|salvo> [en|ru]
    public void SetPreview(bool on, bool salvoRow, int salvoValue, string text)
    {
        isOn = on; showSalvo = salvoRow; salvo = salvoValue; status = text;
        ApplySalvoVisibility();
    }

    [STAThread]
    static void Main(string[] args)
    {
        if (args.Length >= 3 && args[0] == "--render")
        {
            var f = new AutopilotForm();
            bool ru = args.Length > 3 && args[3] == "ru";
            if (args[2] == "manual") f.SetPreview(false, false, 0, ru ? "ручное управление" : "manual control");
            else if (args[2] == "auto") f.SetPreview(true, false, 0, ru ? "АВТОПИЛОТ" : "AUTOPILOT");
            else f.SetPreview(true, true, 3, ru ? "АВТОПИЛОТ" : "AUTOPILOT");
            using (var bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
                bmp.Save(args[1], System.Drawing.Imaging.ImageFormat.Png);
            }
            return;
        }
        if (args.Length == 2 && args[0] == "--scan")
        {
            // read-only diagnostics: list carrier records into a text file
            var lines = new List<string>();
            Process p = GameMemory.FindGame();
            if (p == null) lines.Add("game not running");
            else
            {
                IntPtr h = Native.OpenProcess(Native.PROCESS_VM_READ | Native.PROCESS_QUERY_INFORMATION, false, p.Id);
                try { lines.Add("module " + p.MainModule.ModuleName + " base 0x" + p.MainModule.BaseAddress.ToInt64().ToString("x") + " vtable 0x" + GameMemory.ExpectedVtable(p).ToString("x")); } catch (Exception ex) { lines.Add("module error " + ex.Message); }
                var ids = new Dictionary<int, int>();
                foreach (var r in GameMemory.Scan(h))
                {
                    lines.Add("client 0x" + r.Address.ToString("x") + " id " + r.VehicleId + " team " + r.TeamId + " mode " + r.Mode);
                    ids[r.VehicleId] = r.TeamId;
                }
                foreach (var kv in ids)
                    foreach (long obj in GameMemory.FindServerObjects(h, kv.Key, kv.Value, GameMemory.ExpectedVtable(p)))
                        lines.Add("server 0x" + obj.ToString("x") + " id " + kv.Key + " team " + kv.Value + " mode " + GameMemory.ReadServerMode(h, obj));
                bool sf; long sa = SalvoMemory.Find(h, out sf);
                lines.Add(sa < 0 ? "salvo constant not found" : "salvo 0x" + sa.ToString("x") + (sf ? " float " : " int ") + SalvoMemory.Read(h, sa, sf));
                Native.CloseHandle(h);
            }
            File.WriteAllLines(args[1], lines.ToArray());
            return;
        }
        Application.EnableVisualStyles();
        var form = new AutopilotForm();
        if (args.Length > 0)
        {
            // launcher mode: start the game command that Steam passes in place of %command%
            var rest = new List<string>();
            for (int k = 1; k < args.Length; k++)
                rest.Add(args[k].Contains(" ") ? "\"" + args[k] + "\"" : args[k]);
            try
            {
                var psi = new ProcessStartInfo(args[0], string.Join(" ", rest.ToArray()));
                psi.UseShellExecute = false;
                psi.WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(args[0]));
                Process.Start(psi);
                form.CloseWithGame = true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Не удалось запустить игру:\n" + args[0] + "\n\n" + ex.Message, "CC2 Autopilot");
            }
        }
        Application.Run(form);
    }
}
