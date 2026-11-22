namespace Vibelish;

/// <summary>Reads the focused input box on a background thread so UI Automation can never
/// freeze the tray UI. Also runs OCR for controls that do not expose their text.</summary>
sealed class FieldWatcher : IDisposable
{
    readonly Thread _thread;
    readonly int _selfPid;
    volatile bool _run = true;
    volatile bool _on;
    FocusSnapshot? _latest;
    long _latestAt;
    string _ocrText = "";
    long _ocrAt;
    long _nextOcr;
    // 远程窗口开始听写时原有的行，按窗口分开记；换回来接着比。
    readonly Dictionary<IntPtr, List<OcrReader.OcrLine>> _remoteBaseline = new();
    volatile bool _resetRemote;

    public FieldWatcher(int selfPid)
    {
        _selfPid = selfPid;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Vibelish.FieldWatcher",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public FocusSnapshot? Latest => Volatile.Read(ref _latest);
    public long LatestAt => Interlocked.Read(ref _latestAt);
    public string OcrText => Volatile.Read(ref _ocrText);
    public long OcrAt => Interlocked.Read(ref _ocrAt);

    public void SetEnabled(bool enabled)
    {
        _on = enabled;
        if (enabled)
        {
            _resetRemote = true;
            return;
        }

        Volatile.Write(ref _latest, null);
        Volatile.Write(ref _ocrText, "");
    }

    void Loop()
    {
        while (_run)
        {
            if (_on)
            {
                try
                {
                    var snapshot = FocusReader.Read(_selfPid);
                    Volatile.Write(ref _latest, snapshot);
                    Interlocked.Exchange(ref _latestAt, Environment.TickCount64);
                    if (snapshot.Ok && snapshot.Remote)
                        TryOcrRemote(snapshot);
                    else if (snapshot.Ok && snapshot.NeedsOcr && OcrReader.CanCapture(snapshot.Bounds))
                        TryOcr(snapshot);
                }
                catch (Exception ex)
                {
                    Volatile.Write(ref _latest, FocusSnapshot.Fail(ex.Message));
                    Interlocked.Exchange(ref _latestAt, Environment.TickCount64);
                }
            }

            Thread.Sleep(120);
        }
    }

    void TryOcr(FocusSnapshot snapshot)
    {
        var now = Environment.TickCount64;
        if (now < _nextOcr)
            return;
        _nextOcr = now + 600;
        try
        {
            var raw = OcrReader.ReadAsync(snapshot, CancellationToken.None).GetAwaiter().GetResult();
            Volatile.Write(ref _ocrText, OcrReader.Clean(raw, snapshot.Control));
            Interlocked.Exchange(ref _ocrAt, Environment.TickCount64);
        }
        catch
        {
            // OCR is best effort; UIA/IME remain the primary sources.
        }
    }

    void TryOcrRemote(FocusSnapshot snapshot)
    {
        if (_resetRemote)
        {
            _resetRemote = false;
            _remoteBaseline.Clear();
            _nextOcr = 0;
        }

        var now = Environment.TickCount64;
        if (now < _nextOcr)
            return;
        _nextOcr = now + 500;
        try
        {
            var lines = OcrReader.ReadWindowAsync(snapshot.Hwnd).GetAwaiter().GetResult();
            if (!_remoteBaseline.TryGetValue(snapshot.Hwnd, out var baseline))
            {
                _remoteBaseline[snapshot.Hwnd] = lines;
                Volatile.Write(ref _ocrText, "");
            }
            else
            {
                Volatile.Write(ref _ocrText, RemoteDiff.Pick(baseline, lines));
            }

            Interlocked.Exchange(ref _ocrAt, Environment.TickCount64);
        }
        catch
        {
            // 截图或识别失败就等下一轮。
        }
    }

    public void Dispose()
    {
        _run = false;
        _thread.Join(500);
    }
}
