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
            return;
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
                    if (snapshot.Ok && snapshot.NeedsOcr && OcrReader.CanCapture(snapshot.Bounds))
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

    public void Dispose()
    {
        _run = false;
        _thread.Join(500);
    }
}
