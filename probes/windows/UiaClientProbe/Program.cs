using System.IO;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using System.Windows.Forms;
using Interop.UIAutomationClient;

// A probe, not product code: it measures the UI Automation clients on this machine and writes nothing to the
// product. Every command prints one line per measurement so a row can be copied into FINDINGS.md.
var command = args.Length > 0 ? args[0] : "compare";
var match = args.Length > 1 ? args[1] : string.Empty;

switch (command)
{
    case "monitors":
        PrintMonitors();
        break;

    case "fixture":
        RunFixture(match.Length > 0 ? match : "uia-probe-fixture");
        break;

    case "stabilize":
        Stabilize(match);
        break;

    case "nodes":
        Nodes(match);
        break;

    case "local":
        CompareLocal();
        break;

    case "product":
        ProductObserve(match);
        break;

    default:
        Compare(match);
        break;
}

static void Compare(string match)
{
    using var child = match.Length == 0 ? FixtureProcess.Start() : null;
    var window = match.Length == 0 ? WaitForWindow("uia-probe-fixture") : FindWindow(match);
    if (window is null)
    {
        Console.WriteLine($"no window matching '{match}'");
        return;
    }

    Console.WriteLine($"window '{window.Value.Title}' pid={window.Value.ProcessId} handle=0x{window.Value.Handle:x}");
    Measure("com+cache", () => CountCom(window.Value.Handle, cache: true));
    Measure("com", () => CountCom(window.Value.Handle, cache: false));
    Measure("managed", () => CountManaged(window.Value.Handle));
}

static void Nodes(string match)
{
    foreach (var window in EnumerateWindows().Where(w => match.Length == 0
                 || w.Title.Contains(match, StringComparison.OrdinalIgnoreCase)))
    {
        Console.WriteLine($"window '{window.Title}' pid={window.ProcessId} handle=0x{window.Handle:x}");
        try
        {
            Measure("  com+cache", () => CountCom(window.Handle, cache: true));
            Measure("  com", () => CountCom(window.Handle, cache: false));
            Measure("  managed", () => CountManaged(window.Handle));
        }
        catch (Exception error)
        {
            Console.WriteLine($"  error: {error.GetType().Name}: {error.Message}");
        }
    }
}

static void Stabilize(string match)
{
    var window = FindWindow(match);
    if (window is null)
    {
        Console.WriteLine($"no window matching '{match}'");
        return;
    }

    Console.WriteLine($"window '{window.Value.Title}' pid={window.Value.ProcessId}");
    for (var i = 0; i < 12; i++)
    {
        var count = CountCom(window.Value.Handle, cache: true);
        Console.WriteLine($"  t={i * 250,4}ms nodes={count}");
        Thread.Sleep(250);
    }
}

static void Measure(string label, Func<int> count)
{
    var watch = Stopwatch.StartNew();
    var nodes = count();
    watch.Stop();
    Console.WriteLine($"  {label,-10} nodes={nodes,5} elapsed={watch.Elapsed.TotalMilliseconds,8:0.0}ms");
}

static int CountCom(nint handle, bool cache)
{
    var uia = (IUIAutomation)new CUIAutomation8();
    if (uia is IUIAutomation2 settings)
    {
        settings.ConnectionTimeout = 2000;
        settings.TransactionTimeout = 20000;
    }

    var request = cache ? BuildCache(uia) : null;
    var root = cache
        ? uia.ElementFromHandleBuildCache(handle, request!)
        : uia.ElementFromHandle(handle);
    if (root is null)
    {
        return 0;
    }

    var walker = uia.ControlViewWalker;
    var count = 0;

    void Walk(IUIAutomationElement element, int depth)
    {
        if (depth > 12 || count > 5000)
        {
            return;
        }

        var child = cache
            ? walker.GetFirstChildElementBuildCache(element, request!)
            : walker.GetFirstChildElement(element);

        while (child is not null)
        {
            count++;
            ReadProperties(child, cache);
            Walk(child, depth + 1);
            child = cache
                ? walker.GetNextSiblingElementBuildCache(child, request!)
                : walker.GetNextSiblingElement(child);
        }
    }

    Walk(root, 0);
    return count;
}

static int CountManaged(nint handle)
{
    var root = AutomationElement.FromHandle(handle);
    if (root is null)
    {
        return 0;
    }

    var walker = TreeWalker.ControlViewWalker;
    var count = 0;

    void Walk(AutomationElement element, int depth)
    {
        if (depth > 12 || count > 5000)
        {
            return;
        }

        var child = walker.GetFirstChild(element);
        while (child is not null)
        {
            count++;
            _ = child.Current.Name;
            _ = child.Current.ControlType;
            _ = child.Current.BoundingRectangle;
            Walk(child, depth + 1);
            child = walker.GetNextSibling(child);
        }
    }

    Walk(root, 0);
    return count;
}

static IUIAutomationCacheRequest BuildCache(IUIAutomation uia)
{
    var cache = uia.CreateCacheRequest();
    cache.AutomationElementMode = Interop.UIAutomationClient.AutomationElementMode.AutomationElementMode_Full;
    cache.TreeScope = Interop.UIAutomationClient.TreeScope.TreeScope_Element;
    foreach (var id in new[]
             {
                 30000, 30001, 30002, 30003, 30005, 30008, 30010, 30011, 30012, 30019, 30022,
                 30031, 30036, 30041, 30043, 30028, 30034, 30035, 30040, 30107,
             })
    {
        cache.AddProperty(id);
    }

    return cache;
}

static void ReadProperties(IUIAutomationElement element, bool cache)
{
    foreach (var id in new[] { 30003, 30005, 30010, 30011, 30012, 30019, 30022, 30001 })
    {
        _ = cache ? element.GetCachedPropertyValue(id) : element.GetCurrentPropertyValue(id);
    }
}

static void PrintMonitors()
{
    var desktop = GetDesktopWindow();
    Console.WriteLine($"virtual screen: {SystemInformation.VirtualScreen}");
    foreach (var screen in Screen.AllScreens)
    {
        Console.WriteLine(
            $"screen '{screen.DeviceName}' primary={screen.Primary} bounds={screen.Bounds} working={screen.WorkingArea}");
    }

    Console.WriteLine($"desktop dpi: {GetDpiForWindow(desktop)}");
}

static void ProductObserve(string match)
{
    // The product's own observe path, one window at a time: the acceptance question is whether the COM client
    // kept the observation latency where the managed client had it.
    var window = FindWindow(match.Length == 0 ? "文件资源管理器" : match);
    if (window is null)
    {
        Console.WriteLine($"no window matching '{match}'");
        return;
    }

    using var automation = new Partner.Desktop.DesktopAutomation();
    var latencies = new List<double>();
    var elements = 0;

    for (var round = 0; round < 5; round++)
    {
        var watch = Stopwatch.StartNew();
        var observation = automation.Observe(elementsFor: window.Value.Handle.ToString("x"));
        watch.Stop();
        latencies.Add(watch.Elapsed.TotalMilliseconds);
        elements = observation.Elements.Count;
    }

    latencies.Sort();
    Console.WriteLine(
        $"product observe '{window.Value.Title}': p50={latencies[latencies.Count / 2]:0.0}ms "
        + $"min={latencies[0]:0.0}ms max={latencies[^1]:0.0}ms elements={elements}");
}

static Form BuildFixture(string title)
{
    var form = new Form
    {
        Text = title,
        ClientSize = new Size(520, 420),
        StartPosition = FormStartPosition.Manual,
        Location = new Point(80, 80),
        TopMost = true,
    };

    for (var i = 0; i < 14; i++)
    {
        form.Controls.Add(new TextBox
        {
            Name = $"field{i}",
            AccessibleName = $"field{i}",
            Location = new Point(20, 20 + (i * 26)),
            Size = new Size(300, 23),
        });
    }

    form.Controls.Add(new Button
    {
        Name = "submit",
        AccessibleName = "submit",
        Text = "提交",
        Location = new Point(360, 20),
        Size = new Size(120, 32),
    });

    return form;
}

static void RunFixture(string title)
{
    var ready = new ManualResetEventSlim();
    var thread = new Thread(() =>
    {
        var form = BuildFixture(title);
        form.Shown += (_, _) => ready.Set();
        Application.Run(form);
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = false;
    thread.Start();
    ready.Wait(TimeSpan.FromSeconds(10));
}

static void CompareLocal()
{
    // Same-process provider: this is the case where a cache request only adds a copy.
    var ready = new ManualResetEventSlim();
    Form? form = null;
    nint handle = 0;
    var thread = new Thread(() =>
    {
        form = BuildFixture("uia-probe-local");
        form.Shown += (_, _) =>
        {
            handle = form.Handle;
            ready.Set();
        };
        Application.Run(form);
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.IsBackground = true;
    thread.Start();
    ready.Wait(TimeSpan.FromSeconds(10));

    Console.WriteLine($"window 'uia-probe-local' same-process handle=0x{handle:x}");
    Measure("com+cache", () => CountCom(handle, cache: true));
    Measure("com", () => CountCom(handle, cache: false));
    Measure("managed", () => CountManaged(handle));

    form?.BeginInvoke(new Action(() => form.Close()));
    thread.Join(TimeSpan.FromSeconds(5));
}

static (nint Handle, int ProcessId, string Title)? WaitForWindow(string match)
{
    var deadline = Stopwatch.StartNew();
    while (deadline.Elapsed < TimeSpan.FromSeconds(10))
    {
        if (FindWindow(match) is { } found)
        {
            return found;
        }

        Thread.Sleep(100);
    }

    return null;
}

static (nint Handle, int ProcessId, string Title)? FindWindow(string match)
{
    foreach (var window in EnumerateWindows())
    {
        if (window.Title.Contains(match, StringComparison.OrdinalIgnoreCase))
        {
            return window;
        }
    }

    return null;
}

static IEnumerable<(nint Handle, int ProcessId, string Title)> EnumerateWindows()
{
    var windows = new List<(nint, int, string)>();
    EnumWindows((handle, _) =>
    {
        if (!IsWindowVisible(handle))
        {
            return true;
        }

        var length = GetWindowTextLength(handle);
        if (length <= 0)
        {
            return true;
        }

        var text = new char[length + 1];
        GetWindowText(handle, text, text.Length);
        GetWindowThreadProcessId(handle, out var processId);
        windows.Add((handle, (int)processId, new string(text, 0, length)));
        return true;
    }, 0);

    return windows;
}

static bool IsWindowVisible(nint handle) => NativeIsWindowVisible(handle);

[DllImport("user32.dll", EntryPoint = "IsWindowVisible")]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool NativeIsWindowVisible(nint hWnd);

[DllImport("user32.dll")]
[return: MarshalAs(UnmanagedType.Bool)]
static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

[DllImport("user32.dll", CharSet = CharSet.Unicode)]
static extern int GetWindowText(nint hWnd, char[] text, int maxCount);

[DllImport("user32.dll")]
static extern int GetWindowTextLength(nint hWnd);

[DllImport("user32.dll")]
static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);

[DllImport("user32.dll")]
static extern nint GetDesktopWindow();

[DllImport("user32.dll")]
static extern uint GetDpiForWindow(nint hWnd);

/// <summary>Spawns this same probe as a fixture window, so the measurement is cross-process like a real app.</summary>
sealed class FixtureProcess : IDisposable
{
    private readonly Process _process;

    private FixtureProcess(Process process) => _process = process;

    public static FixtureProcess Start()
    {
        // Under `dotnet run` the process image is dotnet.exe and the probe is the first argument; under the
        // apphost it is the probe itself. Both are supported so the measurement runs the same way by hand.
        var image = Environment.ProcessPath!;
        var entry = Environment.GetCommandLineArgs()[0];
        var probe = Path.GetFileNameWithoutExtension(image).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? $"\"{entry}\" fixture uia-probe-fixture"
            : "fixture uia-probe-fixture";

        var process = Process.Start(new ProcessStartInfo
        {
            FileName = image,
            Arguments = probe,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("could not start the fixture process");

        return new FixtureProcess(process);
    }

    public void Dispose()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill();
                _process.WaitForExit(3000);
            }
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            _process.Dispose();
        }
    }
}

delegate bool EnumWindowsProc(nint hWnd, nint lParam);
