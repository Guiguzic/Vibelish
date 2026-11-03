#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <imm.h>
#include <msctf.h>
#include <wchar.h>

// Shared with Vibelish. Offsets are part of the contract.
// 0 lock is unused; a named mutex guards the block.
// 4  LONG active
// 8  LONG reset
// 12 LONG appliedReset
// 16 LONG seq
// 20 LONG committedLen
// 24 LONG compLen
// 28 wchar committed[2000]
// 4028 wchar comp[400]
// 4828 LONG noteCount   messages seen while a session is active
// 4832 LONG composing   1 while an IME composition is open

#pragma pack(push, 4)
struct SharedState {
    LONG lockUnused;
    LONG active;
    LONG reset;
    LONG appliedReset;
    LONG seq;
    LONG committedLen;
    LONG compLen;
    wchar_t committed[2000];
    wchar_t comp[400];
    LONG noteCount;
    LONG composing;
};
#pragma pack(pop)

static const DWORD GCS_COMPSTR_W = 0x0008;
static const DWORD GCS_RESULTSTR_W = 0x0800;
static const UINT WM_IME_CHAR_MSG = 0x0286;
static const UINT WM_IME_COMPOSITION_MSG = 0x010F;
static const UINT WM_IME_ENDCOMPOSITION_MSG = 0x010E;

static HANDLE g_map = NULL;
static SharedState* g_state = NULL;
static HANDLE g_mutex = NULL;
static wchar_t g_lastResult[400];
static wchar_t g_skip[64];
static int g_skipLen = 0;
static int g_skipPos = 0;
static UINT g_lastMsg = 0;
static WPARAM g_lastWParam = 0;

static SharedState* State() {
    if (g_state) return g_state;
    g_map = CreateFileMappingW(INVALID_HANDLE_VALUE, NULL, PAGE_READWRITE, 0, 8192, L"Local\\Vibelish.Ime.v1");
    if (!g_map) return NULL;
    g_state = (SharedState*)MapViewOfFile(g_map, FILE_MAP_ALL_ACCESS, 0, 0, 8192);
    return g_state;
}

static HANDLE Mutex() {
    if (!g_mutex) g_mutex = CreateMutexW(NULL, FALSE, L"Local\\Vibelish.Ime.Lock");
    return g_mutex;
}

static bool IsForegroundProcess() {
    DWORD pid = 0;
    GetWindowThreadProcessId(GetForegroundWindow(), &pid);
    return pid == GetCurrentProcessId();
}

static bool LockState() {
    HANDLE mutex = Mutex();
    if (!mutex) return false;
    DWORD wait = WaitForSingleObject(mutex, 15);
    return wait == WAIT_OBJECT_0 || wait == WAIT_ABANDONED;
}

static void UnlockState() {
    if (g_mutex) ReleaseMutex(g_mutex);
}

static void AppendRaw(SharedState* state, const wchar_t* text, int count) {
    if (count <= 0) return;
    if (state->committedLen < 0 || state->committedLen > 1999) state->committedLen = 0;
    int room = 1999 - state->committedLen;
    if (count > room) count = room;
    if (count <= 0) return;
    memcpy(state->committed + state->committedLen, text, count * sizeof(wchar_t));
    state->committedLen += count;
    state->committed[state->committedLen] = 0;
    InterlockedIncrement(&state->seq);
}

static void RememberSkip(const wchar_t* text, int count) {
    if (count > 63) count = 63;
    memcpy(g_skip, text, count * sizeof(wchar_t));
    g_skipLen = count;
    g_skipPos = 0;
}

static void AppendString(SharedState* state, const wchar_t* text, int count) {
    AppendRaw(state, text, count);
    RememberSkip(text, count);
}

static void AppendChar(SharedState* state, wchar_t ch) {
    if (ch < 32) return;
    if (g_skipPos < g_skipLen && g_skip[g_skipPos] == ch) {
        g_skipPos++;
        return;
    }
    g_skipLen = 0;
    g_skipPos = 0;
    AppendRaw(state, &ch, 1);
}

static int ReadIme(HWND hwnd, DWORD index, wchar_t* buffer, int capacity) {
    buffer[0] = 0;
    HIMC context = ImmGetContext(hwnd);
    if (!context) return 0;
    int bytes = ImmGetCompositionStringW(context, index, NULL, 0);
    if (bytes <= 0) {
        ImmReleaseContext(hwnd, context);
        return 0;
    }
    int chars = bytes / (int)sizeof(wchar_t);
    if (chars >= capacity) chars = capacity - 1;
    ImmGetCompositionStringW(context, index, buffer, chars * (int)sizeof(wchar_t));
    buffer[chars] = 0;
    ImmReleaseContext(hwnd, context);
    return chars;
}

static void SetComp(SharedState* state, const wchar_t* text, int count) {
    if (count < 0) count = 0;
    if (count > 399) count = 399;
    memcpy(state->comp, text, count * sizeof(wchar_t));
    state->comp[count] = 0;
    state->compLen = count;
}

static thread_local TfClientId t_client = TF_CLIENTID_NULL;
static thread_local ITfThreadMgr* t_mgr = nullptr;
static thread_local bool t_queued = false;
static thread_local bool t_owns = false;
static thread_local bool t_failed = false;
static thread_local DWORD t_lastRead = 0;

// Both WH_GETMESSAGE and WH_CALLWNDPROC see the same message; keep only the first.
static thread_local UINT t_seenMessage = 0;
static thread_local WPARAM t_seenWParam = 0;
static thread_local LPARAM t_seenLParam = 0;
static thread_local DWORD t_seenTick = 0;

static bool SeenBefore(UINT message, WPARAM wParam, LPARAM lParam) {
    DWORD now = GetTickCount();
    if (message == t_seenMessage && wParam == t_seenWParam && lParam == t_seenLParam && now - t_seenTick < 30)
        return true;
    t_seenMessage = message;
    t_seenWParam = wParam;
    t_seenLParam = lParam;
    t_seenTick = now;
    return false;
}

static int TrimToInput(wchar_t* text, int count) {
    if (count <= 0) return 0;
    int lines = 1;
    for (int i = 0; i < count; i++) {
        if (text[i] == L'\n') lines++;
    }
    if (lines <= 6 && count <= 800) return count;
    int start = 0;
    for (int i = count - 1; i >= 0; i--) {
        if (text[i] == L'\n' || text[i] == L'\r') {
            start = i + 1;
            break;
        }
    }
    int length = count - start;
    if (length <= 0) return 0;
    if (start > 0) memmove(text, text + start, length * sizeof(wchar_t));
    if (length > 800) {
        memmove(text, text + (length - 800), 800 * sizeof(wchar_t));
        length = 800;
    }
    text[length] = 0;
    return length;
}

static void ReplaceCommitted(SharedState* state, const wchar_t* text, int count, bool composing) {
    if (count < 0) count = 0;
    if (count > 1999) count = 1999;
    if (count > 0) memcpy(state->committed, text, count * sizeof(wchar_t));
    state->committed[count] = 0;
    state->committedLen = count;
    state->compLen = 0;
    state->comp[0] = 0;
    state->composing = composing ? 1 : 0;
    InterlockedIncrement(&state->seq);
}

class FieldReadSession : public ITfEditSession {
public:
    explicit FieldReadSession(ITfContext* context) : _ref(1), _context(context) {
        _context->AddRef();
    }

    ~FieldReadSession() {
        if (_context) _context->Release();
    }

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** object) override {
        if (iid == IID_IUnknown || iid == __uuidof(ITfEditSession)) {
            *object = this;
            AddRef();
            return S_OK;
        }
        *object = nullptr;
        return E_NOINTERFACE;
    }

    ULONG STDMETHODCALLTYPE AddRef() override { return (ULONG)InterlockedIncrement(&_ref); }

    ULONG STDMETHODCALLTYPE Release() override {
        LONG count = InterlockedDecrement(&_ref);
        if (count == 0) delete this;
        return (ULONG)count;
    }

    HRESULT STDMETHODCALLTYPE DoEditSession(TfEditCookie cookie) override {
        t_queued = false;
        t_lastRead = GetTickCount();
        wchar_t text[1000];
        int length = ReadField(cookie, text, 999);
        bool composing = HasComposition();
        SharedState* state = State();
        if (state && LockState()) {
            if (length > 0) {
                ReplaceCommitted(state, text, length, composing);
                t_owns = true;
            } else if (t_owns) {
                ReplaceCommitted(state, L"", 0, false);
                t_owns = false;
            }
            UnlockState();
        } else if (length == 0) {
            t_owns = false;
        }
        return S_OK;
    }

private:
    LONG _ref;
    ITfContext* _context;

    int ReadField(TfEditCookie cookie, wchar_t* text, int capacity) {
        text[0] = 0;
        ITfRange* start = nullptr;
        ITfRange* end = nullptr;
        if (FAILED(_context->GetStart(cookie, &start)) || !start) return 0;
        if (FAILED(_context->GetEnd(cookie, &end)) || !end) {
            start->Release();
            return 0;
        }
        start->ShiftEndToRange(cookie, end, TF_ANCHOR_END);
        end->Release();
        ULONG count = 0;
        HRESULT hr = start->GetText(cookie, 0, text, (ULONG)capacity, &count);
        if (SUCCEEDED(hr) && count >= (ULONG)capacity) {
            for (int step = 0; step < 20; step++) {
                LONG moved = 0;
                if (FAILED(start->ShiftStart(cookie, 800, &moved, nullptr)) || moved <= 0) break;
                count = 0;
                hr = start->GetText(cookie, 0, text, (ULONG)capacity, &count);
                if (FAILED(hr) || count < (ULONG)capacity) break;
            }
        }
        start->Release();
        if (FAILED(hr) || count == 0) return 0;
        if (count > (ULONG)capacity) count = (ULONG)capacity;
        text[count] = 0;
        return TrimToInput(text, (int)count);
    }

    bool HasComposition() {
        ITfContextComposition* composition = nullptr;
        if (FAILED(_context->QueryInterface(__uuidof(ITfContextComposition), (void**)&composition)) || !composition)
            return false;
        IEnumITfCompositionView* enumerator = nullptr;
        bool found = false;
        if (SUCCEEDED(composition->EnumCompositions(&enumerator)) && enumerator) {
            ITfCompositionView* view = nullptr;
            ULONG fetched = 0;
            if (enumerator->Next(1, &view, &fetched) == S_OK && view) {
                found = true;
                view->Release();
            }
            enumerator->Release();
        }
        composition->Release();
        return found;
    }
};

static void ReleaseThreadTsf() {
    if (t_mgr && t_client != TF_CLIENTID_NULL) t_mgr->Deactivate();
    if (t_mgr) t_mgr->Release();
    t_mgr = nullptr;
    t_client = TF_CLIENTID_NULL;
    t_queued = false;
    t_owns = false;
    t_failed = false;
}

static void ScheduleFieldRead() {
    if (t_failed || t_queued) return;
    DWORD now = GetTickCount();
    if (now - t_lastRead < 50) return;
    try {
        HMODULE msctf = GetModuleHandleW(L"msctf.dll");
        if (!msctf) msctf = LoadLibraryW(L"msctf.dll");
        auto getMgr = msctf ? (HRESULT (WINAPI*)(ITfThreadMgr**))GetProcAddress(msctf, "TF_GetThreadMgr") : nullptr;
        ITfThreadMgr* mgr = nullptr;
        if (!getMgr || FAILED(getMgr(&mgr)) || !mgr) return;
        if (t_client == TF_CLIENTID_NULL) {
            if (FAILED(mgr->Activate(&t_client))) {
                t_failed = true;
                mgr->Release();
                return;
            }
            t_mgr = mgr;
        } else {
            mgr->Release();
            mgr = t_mgr;
        }
        if (!mgr) return;
        ITfDocumentMgr* document = nullptr;
        if (FAILED(mgr->GetFocus(&document)) || !document) return;
        ITfContext* context = nullptr;
        if (FAILED(document->GetBase(&context)) || !context)
            document->GetTop(&context);
        document->Release();
        if (!context) return;
        t_queued = true;
        auto* session = new FieldReadSession(context);
        HRESULT sessionResult = E_FAIL;
        HRESULT hr = context->RequestEditSession(t_client, session, TF_ES_READ | TF_ES_ASYNC, &sessionResult);
        session->Release();
        context->Release();
        if (FAILED(hr)) t_queued = false;
    } catch (...) {
        t_queued = false;
        t_failed = true;
    }
}

static void HandleMessage(HWND hwnd, UINT message, WPARAM wParam, LPARAM lParam) {
    SharedState* state = State();
    if (!state || !state->active) {
        if (t_client != TF_CLIENTID_NULL) ReleaseThreadTsf();
        return;
    }
    if (!IsForegroundProcess()) return;

    // WM_KEYDOWN is the only reliable signal for TSF-based IMEs (Microsoft Pinyin does not
    // post WM_IME_* to TSF-aware controls), so use it to refresh the TSF field text.
    bool keyDown = message == WM_KEYDOWN || message == WM_SYSKEYDOWN;
    bool textMessage = message == WM_CHAR || message == WM_IME_CHAR_MSG
        || message == WM_IME_COMPOSITION_MSG || message == WM_IME_ENDCOMPOSITION_MSG;
    if (!keyDown && !textMessage) return;
    if (SeenBefore(message, wParam, lParam)) return;
    if (!LockState()) return;

    if (state->reset != state->appliedReset) {
        state->committedLen = 0;
        state->committed[0] = 0;
        state->compLen = 0;
        state->comp[0] = 0;
        state->noteCount = 0;
        state->composing = 0;
        state->appliedReset = state->reset;
        g_lastResult[0] = 0;
        g_skipLen = 0;
        g_skipPos = 0;
        t_failed = false;
    }

    if (!keyDown) {
        state->noteCount++;

        if (message == WM_IME_ENDCOMPOSITION_MSG) {
            SetComp(state, L"", 0);
        } else if (message == WM_IME_COMPOSITION_MSG) {
            wchar_t comp[400];
            int compLen = ReadIme(hwnd, GCS_COMPSTR_W, comp, 400);
            SetComp(state, comp, compLen);
            if (!t_owns && (lParam & GCS_RESULTSTR_W)) {
                wchar_t result[400];
                int resultLen = ReadIme(hwnd, GCS_RESULTSTR_W, result, 400);
                if (resultLen > 0 && wcscmp(result, g_lastResult) != 0) {
                    AppendString(state, result, resultLen);
                    wcsncpy_s(g_lastResult, result, _TRUNCATE);
                }
            }
        } else if (message == WM_CHAR || message == WM_IME_CHAR_MSG) {
            wchar_t ch = (wchar_t)(wParam & 0xFFFF);
            if (!t_owns && ch >= 0x80 && !(g_lastMsg == WM_IME_CHAR_MSG && message == WM_CHAR && g_lastWParam == wParam)) {
                AppendChar(state, ch);
            }
            g_lastMsg = message;
            g_lastWParam = wParam;
        }
    }

    UnlockState();
    ScheduleFieldRead();
}

extern "C" __declspec(dllexport) LRESULT CALLBACK GetMsgProc(int code, WPARAM wParam, LPARAM lParam) {
    if (code >= 0 && wParam == PM_REMOVE) {
        MSG* msg = (MSG*)lParam;
        if (msg) HandleMessage(msg->hwnd, msg->message, msg->wParam, msg->lParam);
    }
    return CallNextHookEx(NULL, code, wParam, lParam);
}

extern "C" __declspec(dllexport) LRESULT CALLBACK CallWndProc(int code, WPARAM wParam, LPARAM lParam) {
    if (code >= 0) {
        CWPSTRUCT* msg = (CWPSTRUCT*)lParam;
        if (msg) HandleMessage(msg->hwnd, msg->message, msg->wParam, msg->lParam);
    }
    return CallNextHookEx(NULL, code, wParam, lParam);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(instance);
    return TRUE;
}
