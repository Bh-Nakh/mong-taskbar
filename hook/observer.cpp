// 관찰용 후킹 DLL: explorer.exe 안에서 작업표시줄 버튼 관련 함수가 불리는 순간을 로그로만 남긴다.
// 동작을 바꾸지 않는다 (인자를 그대로 넘기고 반환값도 그대로 돌려준다).
// 로그: 이 DLL 옆의 observer.log
#include <windows.h>
#include <stdio.h>
#include <stdarg.h>
#include "MinHook.h"

// explorer.exe 10.0.19041.7725 (PE TimeDateStamp / SizeOfImage) 에서만 동작
static const DWORD kStamp = 0x7a77dc0c;
static const DWORD kSize = 0x5c9000;

static wchar_t g_logPath[MAX_PATH];
static volatile LONG g_lines = 0;
static const LONG kMaxLines = 20000;
static CRITICAL_SECTION g_cs;

static void Log(const char* fmt, ...)
{
    if (InterlockedIncrement(&g_lines) > kMaxLines) return;
    char buf[1024];
    SYSTEMTIME t; GetLocalTime(&t);
    int n = sprintf_s(buf, "%02d:%02d:%02d.%03d [%5lu] ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds, GetCurrentThreadId());
    va_list ap; va_start(ap, fmt);
    n += vsnprintf_s(buf + n, sizeof(buf) - n - 2, _TRUNCATE, fmt, ap);
    va_end(ap);
    buf[n++] = '\n';
    EnterCriticalSection(&g_cs);
    HANDLE h = CreateFileW(g_logPath, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h != INVALID_HANDLE_VALUE) { DWORD w; WriteFile(h, buf, n, &w, NULL); CloseHandle(h); }
    LeaveCriticalSection(&g_cs);
}

typedef unsigned long long u64;
// 인자 개수를 모르므로 레지스터 4개 + 스택 4개를 전부 그대로 전달한다 (실제보다 많아도 무해).
typedef u64(__fastcall* Fn8)(u64, u64, u64, u64, u64, u64, u64, u64);

struct Target { const char* name; DWORD rva; Fn8 orig; };

#define DEFHOOK(IDX) \
    static u64 __fastcall Hook##IDX(u64 a, u64 b, u64 c, u64 d, u64 e, u64 f, u64 g, u64 h) { \
        Log("> %s(%llx, %llx, %llx, %llx, %llx, %llx)", g_t[IDX].name, a, b, c, d, e, f); \
        u64 r = g_t[IDX].orig(a, b, c, d, e, f, g, h); \
        Log("< %s = %llx", g_t[IDX].name, r); \
        return r; }

static Target g_t[] = {
    { "CTaskListWnd::_TaskCreated",        0x3F058, NULL },
    { "CTaskListWnd::_CreateTBGroup",      0x5CA24, NULL },
    { "CTaskListWnd::SetRelativeTaskOrder",0x81E00, NULL },
    { "CTaskListWnd::_MoveTaskBtnGroup",   0x820EC, NULL },
    { "CTaskListWnd::_FixupTaskIndicies",  0x6BA24, NULL },
    { "CTaskListWnd::_NotifyItemDestroyed",0x3E058, NULL },
    { "CTaskBtnGroup::AddTaskItem",        0x5CB90, NULL },
    { "CTaskGroup::AddTaskItem",           0x26080, NULL },
};

// ---- 탐색: 버튼 목록(DPA)과 각 버튼의 앱 ID / 실행 파일 이름 읽기 ----
static const BYTE* g_modBase;
static int g_tgOff = -1;   // CTaskBtnGroup 안에서 CTaskGroup 포인터가 있는 위치

static bool SafeRead(const void* p, void* out, size_t n)
{
    __try { memcpy(out, p, n); return true; } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
static void WToUtf8(const wchar_t* w, char* out, int cap)
{
    out[0] = 0;
    if (!w) return;
    __try { WideCharToMultiByte(CP_UTF8, 0, w, -1, out, cap - 1, NULL, NULL); out[cap - 1] = 0; }
    __except (EXCEPTION_EXECUTE_HANDLER) { strcpy_s(out, cap, "?"); }
}
typedef const wchar_t* (__fastcall* StrFn)(void*);
static const wchar_t* CallStr(void* obj, int slot)
{
    __try {
        void** vt = *(void***)obj;
        return ((StrFn)vt[slot])(obj);
    } __except (EXCEPTION_EXECUTE_HANDLER) { return NULL; }
}
static void DescribeGroup(const char* tag, void* tg)
{
    char app[260], exe[260];
    WToUtf8(CallStr(tg, 19), app, sizeof(app));
    WToUtf8(CallStr(tg, 16), exe, sizeof(exe));
    Log("  %s tg=%p vt=%llx appid='%s' exe='%s'", tag, tg, (unsigned long long)(*(BYTE**)tg - g_modBase), app, exe);
}
static void DumpButtons(void* wnd, void* newBtn, void* newTg)
{
    __try {
        if (g_tgOff < 0 && newBtn) {
            for (int off = 8; off < 0x400; off += 8) {
                void* v; if (!SafeRead((BYTE*)newBtn + off, &v, 8)) break;
                if (v == newTg) { g_tgOff = off; break; }
            }
            Log("  CTaskBtnGroup 안의 CTaskGroup 위치 = %d", g_tgOff);
        }
        BYTE* dpa = NULL;
        if (!SafeRead((BYTE*)wnd + 0xd8, &dpa, 8) || !dpa) return;
        int cnt = 0; BYTE** arr = NULL;
        SafeRead(dpa, &cnt, 4); SafeRead(dpa + 8, &arr, 8);
        Log("  버튼 목록: %d 개", cnt);
        for (int i = 0; i < cnt && i < 64; i++) {
            BYTE* bg = NULL;
            if (!SafeRead(arr + i, &bg, 8) || !bg) continue;
            void* tg = NULL;
            if (g_tgOff > 0) SafeRead(bg + g_tgOff, &tg, 8);
            char tag[32]; sprintf_s(tag, "[%d]", i);
            if (tg) DescribeGroup(tag, tg); else Log("  [%d] bg=%p", i, bg);
        }
    } __except (EXCEPTION_EXECUTE_HANDLER) { Log("  DumpButtons 예외"); }
}

// 실험: force_index.txt 가 있으면 _CreateTBGroup 의 삽입 위치(0x7fffffff=맨 끝)를 그 값으로 바꾼다.
static int ReadForceIndex()
{
    wchar_t p[MAX_PATH];
    wcscpy_s(p, g_logPath);
    wchar_t* sl = wcsrchr(p, L'\\');
    if (!sl) return -1;
    wcscpy_s(sl + 1, 32, L"force_index.txt");
    HANDLE h = CreateFileW(p, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return -1;
    char b[16] = {0}; DWORD r = 0; ReadFile(h, b, 15, &r, NULL); CloseHandle(h);
    return atoi(b);
}
static u64 __fastcall Hook1(u64 a, u64 b, u64 c, u64 d, u64 e, u64 f, u64 g, u64 h) {
    u64 c2 = c;
    int fi = ReadForceIndex();
    if (fi >= 0 && (c & 0xffffffffULL) == 0x7fffffffULL) c2 = (u64)fi;
    Log("> %s(%llx, %llx, %llx->%llx, %llx, %llx, %llx)", g_t[1].name, a, b, c, c2, d, e, f);
    u64 r = g_t[1].orig(a, b, c2, d, e, f, g, h);
    Log("< %s = %llx", g_t[1].name, r);
    DumpButtons((void*)a, (void*)r, (void*)b);
    return r;
}
DEFHOOK(0) DEFHOOK(2) DEFHOOK(3) DEFHOOK(4) DEFHOOK(5) DEFHOOK(6) DEFHOOK(7)
static LPVOID g_hooks[] = { (LPVOID)Hook0, (LPVOID)Hook1, (LPVOID)Hook2, (LPVOID)Hook3, (LPVOID)Hook4, (LPVOID)Hook5, (LPVOID)Hook6, (LPVOID)Hook7 };

static HMODULE g_self;

static DWORD WINAPI Init(LPVOID)
{
    BYTE* base = (BYTE*)GetModuleHandleW(NULL);
    IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
    if (nt->FileHeader.TimeDateStamp != kStamp || nt->OptionalHeader.SizeOfImage != kSize) {
        Log("explorer 빌드가 달라 후킹하지 않음 (stamp=%x size=%x)", nt->FileHeader.TimeDateStamp, nt->OptionalHeader.SizeOfImage);
        return 0;
    }
    MH_STATUS s = MH_Initialize();
    g_modBase = base;
    Log("관찰 시작. base=%p MH_Initialize=%d", base, (int)s);
    for (int i = 0; i < (int)(sizeof(g_t) / sizeof(g_t[0])); i++) {
        s = MH_CreateHook(base + g_t[i].rva, g_hooks[i], (LPVOID*)&g_t[i].orig);
        Log("hook %-40s rva=%x status=%d", g_t[i].name, g_t[i].rva, (int)s);
    }
    s = MH_EnableHook(MH_ALL_HOOKS);
    Log("EnableHook=%d", (int)s);
    return 0;
}

BOOL WINAPI DllMain(HMODULE h, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) {
        g_self = h;
        DisableThreadLibraryCalls(h);
        InitializeCriticalSection(&g_cs);
        GetModuleFileNameW(h, g_logPath, MAX_PATH);
        wchar_t* dot = wcsrchr(g_logPath, L'.');
        if (dot) wcscpy_s(dot, 5, L".log");
        CreateThread(NULL, 0, Init, NULL, 0, NULL);   // 로더 락 밖에서 후킹
    } else if (reason == DLL_PROCESS_DETACH) {
        MH_DisableHook(MH_ALL_HOOKS);
        MH_Uninitialize();
        Log("관찰 종료");
    }
    return TRUE;
}
