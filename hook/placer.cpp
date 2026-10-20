// 작업표시줄 그룹 즉시 배치 DLL (explorer.exe 안에서 동작)
// 새 작업표시줄 버튼이 만들어지기 직전(CTaskListWnd::_CreateTBGroup)에 삽입 위치(기본 0x7fffffff=맨 끝)를
// 그룹 설정(placement.txt)에 맞는 자리로 바꾼다. 판단할 수 없으면 아무것도 바꾸지 않는다.
//
// placement.txt (UTF-8, 이 DLL 과 같은 폴더):
//   MODE 0|1            0=각 그룹 아이콘 바로 오른쪽, 1=맨 오른쪽 그룹 아이콘 뒤에 모아서
//   G <그룹 ID>         그룹 시작 (그룹은 설정의 순서대로)
//   E <키>|<키>|...     그 그룹의 항목 하나 (순서대로). 키 = 소문자 실행 파일 경로 또는 앱 ID
// 끄기: 같은 폴더에 placer.off 파일을 만들면 된다.
// 종료: 같은 폴더에 placer.stop 파일이 생기면 후킹을 풀고 스스로 explorer 에서 내려간다.
// 고정/해제 요청: pin_request.txt 에 "PIN|바로가기경로" 또는 "UNPIN|고정폴더안바로가기경로" 한 줄 →
//   explorer 안에서 IPinnedList3::Modify 를 부르고 결과를 pin_result.txt ("hr=0x...") 에 쓴다.
// 버튼 목록 요청: buttons_request.txt 에 버튼 목록 창(MSTaskListWClass) 핸들(16진수) →
//   작업표시줄 UI 스레드에서 버튼마다 앱 ID·실행 파일·창 핸들을 읽어 buttons.txt 에 쓴다.
//   (버튼 이름만으로는 "Python" 처럼 같은 이름의 버튼을 구분할 수 없어서, 도우미가 창 → 프로세스로 잇는다)
#include <windows.h>
#include <objbase.h>
#include <shlobj.h>
#include <stdio.h>
#include <stdlib.h>
#include <stdarg.h>
#include <string>
#include <vector>
#include "MinHook.h"

static const DWORD kStamp = 0x7a77dc0c;     // explorer.exe 10.0.19041.7725 (이 빌드면 아래 RVA 를 그대로 쓴다)
static const DWORD kRvaCreateTBGroup = 0x5CA24;
// 다른 빌드에서는 함수 앞부분 32바이트로 .text 를 찾는다 (19041.7725·7802 에서 한 곳만 맞음 확인).
// 마지막 7바이트는 버튼 목록 위치(kWndDpaOffset=0xd8)를 쓰는 명령이라, 그 구조가 바뀌면 패턴이 안 맞아 배치를 하지 않는다.
static const BYTE kSigCreateTBGroup[] = {
    0x48, 0x8b, 0xc4, 0x48, 0x89, 0x58, 0x10, 0x48, 0x89, 0x68, 0x18, 0x48, 0x89, 0x70, 0x20, 0x57,
    0x48, 0x83, 0xec, 0x30, 0x48, 0x83, 0x60, 0x08, 0x00, 0x48, 0x8d, 0x99, 0xd8, 0x00, 0x00, 0x00 };
static const int kAppIdSlot = 19;           // ITaskGroup::GetAppID
static const int kExeSlot = 16;             // ITaskGroup::GetExeName
static const int kWndDpaOffset = 0xd8;      // CTaskListWnd 안의 버튼 목록(DPA)
static const int kBtnGroupTgOffset = 32;    // CTaskBtnGroup 안의 CTaskGroup 포인터
// CTaskGroup::GetItemFromWindow 의 앞부분(+0x1c 부터 0x60 바이트). 그룹의 창 목록 DPA 가 [CTaskGroup+0x20],
// 항목의 창 핸들을 돌려주는 함수가 vtable 12번째(+0x60)임을 이 코드에서 확인했다 (19041.7725·7802).
// 이 바이트가 .text 에 정확히 한 곳 있을 때만 버튼 목록을 내보낸다.
static const BYTE kSigGetItemFromWindow[] = {
    0x49, 0x83, 0x20, 0x00, 0x4d, 0x8b, 0xf8, 0x48, 0x8b, 0x41, 0x20, 0x4c, 0x8b, 0xea, 0x48, 0x8b,
    0xf1, 0xbd, 0x05, 0x40, 0x00, 0x80, 0x48, 0x85, 0xc0, 0x74, 0x08, 0x4c, 0x63, 0x30, 0x4d, 0x85,
    0xf6, 0x7f, 0x20, 0x48, 0x8b, 0x5c, 0x24, 0x50, 0x8b, 0xc5, 0x48, 0x8b, 0x6c, 0x24, 0x58, 0x48,
    0x8b, 0x74, 0x24, 0x60, 0x48, 0x83, 0xc4, 0x20, 0x41, 0x5f, 0x41, 0x5e, 0x41, 0x5d, 0x41, 0x5c,
    0x5f, 0xc3, 0xcc, 0x33, 0xdb, 0x48, 0x8b, 0x46, 0x20, 0x48, 0x8b, 0x48, 0x08, 0x48, 0x8b, 0x3c,
    0xd9, 0x48, 0x85, 0xff, 0x74, 0x37, 0x48, 0x8b, 0x07, 0x48, 0x8b, 0xcf, 0x48, 0x8b, 0x40, 0x60 };
static const int kTgItemsOffset = 0x20;     // CTaskGroup 안의 창 항목 목록(DPA)
static const int kItemWindowSlot = 12;      // ITaskItem::GetWindow

typedef unsigned long long u64;
typedef u64(__fastcall* Fn8)(u64, u64, u64, u64, u64, u64, u64, u64);
static Fn8 g_orig;
static const BYTE* g_modBase;
static HMODULE g_self;
static wchar_t g_dir[MAX_PATH];
static CRITICAL_SECTION g_cs;
static LONG g_lines;

static void Log(const char* fmt, ...)
{
    if (InterlockedIncrement(&g_lines) > 3000) return;
    char buf[900];
    SYSTEMTIME t; GetLocalTime(&t);
    int n = sprintf_s(buf, "%02d:%02d:%02d.%03d ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    va_list ap; va_start(ap, fmt);
    n += vsnprintf_s(buf + n, sizeof(buf) - n - 2, _TRUNCATE, fmt, ap);
    va_end(ap);
    buf[n++] = '\n';
    wchar_t path[MAX_PATH];
    wcscpy_s(path, g_dir); wcscat_s(path, L"placer.log");
    EnterCriticalSection(&g_cs);
    HANDLE h = CreateFileW(path, FILE_APPEND_DATA, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (h != INVALID_HANDLE_VALUE) { DWORD w; WriteFile(h, buf, n, &w, NULL); CloseHandle(h); }
    LeaveCriticalSection(&g_cs);
}

// ---------- 안전하게 읽기 (SEH 는 C++ 개체와 한 함수에 두지 않는다) ----------
static bool SafeRead(const void* p, void* out, size_t n)
{
    __try { memcpy(out, p, n); return true; } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}
typedef const wchar_t* (__fastcall* StrFn)(void*);
static bool CallStr(void* obj, int slot, wchar_t* out, int cap)
{
    out[0] = 0;
    __try {
        void** vt = *(void***)obj;
        const wchar_t* s = ((StrFn)vt[slot])(obj);
        if (!s) return true;
        int i = 0;
        for (; i < cap - 1 && s[i]; i++) out[i] = s[i];
        out[i] = 0;
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { out[0] = 0; return false; }
}

static DWORD g_modSize;
static bool InImage(const void* p)
{
    return g_modBase && (const BYTE*)p >= g_modBase && (const BYTE*)p < g_modBase + g_modSize;
}

// vtable 의 함수가 explorer 안을 가리킬 때만 부른다 (해제된 개체를 잘못 부르지 않도록)
typedef u64(__fastcall* PtrFn)(void*);
static bool CallPtr(void* obj, int slot, u64& out)
{
    out = 0;
    __try {
        void** vt = *(void***)obj;
        if (!InImage(vt) || !InImage(vt[slot])) return false;
        out = ((PtrFn)vt[slot])(obj);
        return true;
    } __except (EXCEPTION_EXECUTE_HANDLER) { return false; }
}

static std::wstring Lower(const wchar_t* s)
{
    std::wstring r(s);
    for (size_t i = 0; i < r.size(); i++) r[i] = (wchar_t)towlower(r[i]);
    return r;
}

struct Ident { std::wstring appid, exe; };
static bool IdentOf(void* tg, Ident& id)
{
    wchar_t a[520], e[520];
    if (!CallStr(tg, kAppIdSlot, a, 520)) return false;
    CallStr(tg, kExeSlot, e, 520);
    id.appid = Lower(a); id.exe = Lower(e);
    return true;
}

// ---------- 배치 표 ----------
struct EntryRec { std::vector<std::wstring> keys; };
struct GroupRec { std::wstring id; std::vector<EntryRec> entries; };
static std::vector<GroupRec> g_groups;
static int g_mode = 0;
static FILETIME g_tableTime;
static DWORD g_lastCheck;

static std::wstring Utf8ToW(const std::string& s)
{
    if (s.empty()) return L"";
    int n = MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), NULL, 0);
    std::wstring w(n, 0);
    MultiByteToWideChar(CP_UTF8, 0, s.data(), (int)s.size(), &w[0], n);
    return w;
}

static void LoadTable()
{
    wchar_t path[MAX_PATH];
    wcscpy_s(path, g_dir); wcscat_s(path, L"placement.txt");
    WIN32_FILE_ATTRIBUTE_DATA fa;
    if (!GetFileAttributesExW(path, GetFileExInfoStandard, &fa)) { g_groups.clear(); return; }
    if (CompareFileTime(&fa.ftLastWriteTime, &g_tableTime) == 0) return;
    HANDLE h = CreateFileW(path, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    std::string data; char buf[4096]; DWORD r;
    while (ReadFile(h, buf, sizeof(buf), &r, NULL) && r) data.append(buf, r);
    CloseHandle(h);
    g_tableTime = fa.ftLastWriteTime;

    std::vector<GroupRec> groups; int mode = 0;
    size_t pos = 0;
    while (pos < data.size()) {
        size_t nl = data.find('\n', pos);
        if (nl == std::string::npos) nl = data.size();
        std::string line = data.substr(pos, nl - pos);
        pos = nl + 1;
        while (!line.empty() && (line.back() == '\r' || line.back() == ' ')) line.pop_back();
        if (line.size() < 2) continue;
        std::wstring w = Utf8ToW(line.substr(2));
        if (line[0] == 'M') mode = _wtoi(w.c_str());
        else if (line[0] == 'G') { GroupRec g; g.id = Lower(w.c_str()); groups.push_back(g); }
        else if (line[0] == 'E' && !groups.empty()) {
            EntryRec e; size_t s = 0;
            while (s <= w.size()) {
                size_t bar = w.find(L'|', s);
                if (bar == std::wstring::npos) bar = w.size();
                if (bar > s) e.keys.push_back(Lower(w.substr(s, bar - s).c_str()));
                s = bar + 1;
            }
            groups.back().entries.push_back(e);
        }
    }
    g_groups.swap(groups); g_mode = mode;
    Log("배치 표 읽음: 그룹 %d개, MODE %d", (int)g_groups.size(), mode);
}

static void RefreshTable()
{
    DWORD now = GetTickCount();
    if (now - g_lastCheck < 500) return;
    g_lastCheck = now;
    LoadTable();
}

static bool Matches(const Ident& id, const EntryRec& e)
{
    for (size_t i = 0; i < e.keys.size(); i++) {
        if (!id.appid.empty() && id.appid == e.keys[i]) return true;
        if (!id.exe.empty() && id.exe == e.keys[i]) return true;
    }
    return false;
}

static std::wstring GroupIconAppId(const GroupRec& g) { return L"taskbargroup.group." + g.id; }

// ---------- 삽입 위치 계산 (-1 = 바꾸지 않음) ----------
static int ComputeIndex(void* wnd, void* newTg)
{
    RefreshTable();
    if (g_groups.empty()) return -1;
    wchar_t offp[MAX_PATH]; wcscpy_s(offp, g_dir); wcscat_s(offp, L"placer.off");
    if (GetFileAttributesW(offp) != INVALID_FILE_ATTRIBUTES) return -1;

    Ident nid;
    if (!IdentOf(newTg, nid)) return -1;
    bool isIcon = nid.appid.compare(0, 19, L"taskbargroup.group.") == 0;   // 그룹 아이콘 자체

    int gi = -1, ei = -1;
    if (isIcon) {
        std::wstring id = nid.appid.substr(19);
        for (size_t g = 0; g < g_groups.size(); g++) if (g_groups[g].id == id) { gi = (int)g; break; }
        if (gi < 0) return -1;
    } else {
        for (size_t g = 0; g < g_groups.size() && gi < 0; g++)
            for (size_t e = 0; e < g_groups[g].entries.size(); e++)
                if (Matches(nid, g_groups[g].entries[e])) { gi = (int)g; ei = (int)e; break; }
        if (gi < 0) { Log("표에 없음: appid='%ls' exe='%ls'", nid.appid.c_str(), nid.exe.c_str()); return -1; }
    }

    // 현재 버튼 목록 (새 버튼은 아직 없음)
    BYTE* dpa = NULL;
    if (!SafeRead((BYTE*)wnd + kWndDpaOffset, &dpa, 8) || !dpa) return -1;
    int cnt = 0; BYTE** arr = NULL;
    if (!SafeRead(dpa, &cnt, 4) || !SafeRead(dpa + 8, &arr, 8) || cnt <= 0 || cnt > 256 || !arr) return -1;
    std::vector<Ident> btn(cnt);
    for (int i = 0; i < cnt; i++) {
        BYTE* bg = NULL; void* tg = NULL;
        if (!SafeRead(arr + i, &bg, 8) || !bg || !SafeRead(bg + kBtnGroupTgOffset, &tg, 8) || !tg) return -1;
        if (!IdentOf(tg, btn[i])) return -1;
    }

    // 각 그룹 아이콘의 현재 위치
    std::vector<int> gIcon(g_groups.size(), -1);
    for (size_t g = 0; g < g_groups.size(); g++) {
        std::wstring want = GroupIconAppId(g_groups[g]);
        for (int i = 0; i < cnt; i++) if (btn[i].appid == want) { gIcon[g] = i; break; }
    }

    if (isIcon) {
        // 새로 고정된 그룹 아이콘: 설정에서 바로 앞 그룹의 (아이콘 + 그 앱 버튼들) 뒤에 둔다.
        // 앞 그룹이 없으면 뒤 그룹 아이콘 바로 앞, 둘 다 없으면 기본(맨 끝).
        if (gIcon[gi] >= 0) return -1;      // 이미 있는 아이콘은 건드리지 않음
        for (int k = gi - 1; k >= 0; k--) {
            if (gIcon[k] < 0) continue;
            int p = gIcon[k] + 1;
            // 모드 1 은 그룹 아이콘끼리 붙여 두므로 앱 버튼 앞(아이콘 바로 뒤)에 넣는다
            while (g_mode != 1 && p < cnt) {
                bool mine = false;
                for (size_t e = 0; e < g_groups[k].entries.size() && !mine; e++)
                    if (Matches(btn[p], g_groups[k].entries[e])) mine = true;
                if (!mine) break;
                p++;
            }
            return p;
        }
        for (size_t k = gi + 1; k < g_groups.size(); k++)
            if (gIcon[k] >= 0) return gIcon[k];
        return -1;
    }
    if (gIcon[gi] < 0) return -1;

    int pos;
    if (g_mode == 1) {
        // 맨 오른쪽 그룹 아이콘 뒤에, 작업표시줄의 그룹 순서 → 그룹 안 항목 순서로 모은다
        std::vector<int> order;
        for (size_t g = 0; g < g_groups.size(); g++) if (gIcon[g] >= 0) order.push_back((int)g);
        for (size_t a = 0; a < order.size(); a++)
            for (size_t b = a + 1; b < order.size(); b++)
                if (gIcon[order[b]] < gIcon[order[a]]) std::swap(order[a], order[b]);
        pos = gIcon[order.back()] + 1;
        for (size_t o = 0; o < order.size(); o++) {
            const GroupRec& G = g_groups[order[o]];
            for (size_t e = 0; e < G.entries.size(); e++) {
                if (order[o] == gi && (int)e == ei) return pos;
                while (pos < cnt && Matches(btn[pos], G.entries[e])) pos++;
            }
        }
        return pos;
    }
    // 각 그룹 아이콘 바로 오른쪽
    pos = gIcon[gi] + 1;
    for (int e = 0; e < ei; e++)
        while (pos < cnt && Matches(btn[pos], g_groups[gi].entries[e])) pos++;
    return pos;
}

static int SafeCompute(void* wnd, void* tg)
{
    __try { return ComputeIndex(wnd, tg); }
    __except (EXCEPTION_EXECUTE_HANDLER) { return -1; }
}

static void* volatile g_seenWnd[8];         // 배치 함수가 받은 CTaskListWnd (버튼 목록 찾기에 참고)

static u64 __fastcall HookCreateTBGroup(u64 a, u64 b, u64 c, u64 d, u64 e, u64 f, u64 g, u64 h)
{
    for (int i = 0; i < 8; i++) {
        if (g_seenWnd[i] == (void*)a) break;
        if (!g_seenWnd[i]) { g_seenWnd[i] = (void*)a; break; }
    }
    u64 c2 = c;
    if ((c & 0xffffffffULL) == 0x7fffffffULL) {
        int idx = SafeCompute((void*)a, (void*)b);
        if (idx >= 0) { c2 = (u64)idx; Log("삽입 위치 %d (기본: 맨 끝)", idx); }
    }
    return g_orig(a, b, c2, d, e, f, g, h);
}

// ---------- 버튼 목록 내보내기 (작업표시줄 UI 스레드에서) ----------
static bool g_canDump;                      // GetItemFromWindow 패턴이 맞을 때만
static UINT g_dumpMsg;
static HHOOK g_msgHook;
static int g_listDelta = -1;                // 창에 붙은 개체(CImpWndProc) → CTaskListWnd 시작까지의 거리

// c 가 버튼 목록을 가진 CTaskListWnd 처럼 보이는지 (읽기만 해서 확인)
static bool LooksLikeList(BYTE* c)
{
    void* vt = NULL;
    if (!SafeRead(c, &vt, 8) || !InImage(vt)) return false;
    BYTE* dpa = NULL;
    if (!SafeRead(c + kWndDpaOffset, &dpa, 8) || !dpa || InImage(dpa)) return false;
    int cnt = 0; BYTE** arr = NULL;
    if (!SafeRead(dpa, &cnt, 4) || !SafeRead(dpa + 8, &arr, 8) || cnt <= 0 || cnt > 256 || !arr) return false;
    for (int i = 0; i < cnt; i++) {
        BYTE* bg = NULL; BYTE* tg = NULL; void** tvt = NULL; void* fn = NULL;
        if (!SafeRead(arr + i, &bg, 8) || !bg || !SafeRead(bg + kBtnGroupTgOffset, &tg, 8) || !tg) return false;
        if (!SafeRead(tg, &tvt, 8) || !InImage(tvt) || !SafeRead(tvt + kAppIdSlot, &fn, 8) || !InImage(fn)) return false;
    }
    return true;
}

static BYTE* FindListObj(HWND list)
{
    BYTE* u = (BYTE*)GetWindowLongPtrW(list, 0);
    if (!u) return NULL;
    if (g_listDelta >= 0 && LooksLikeList(u - g_listDelta)) return u - g_listDelta;
    // 배치 함수가 받은 개체 중 이 창의 것이 있으면 그것을 쓰고, 없으면 가까운 앞쪽에서 찾는다
    for (int i = 0; i < 8; i++) {
        BYTE* a = (BYTE*)g_seenWnd[i];
        if (a && a <= u && u - a < 0x400 && LooksLikeList(a)) {
            g_listDelta = (int)(u - a);
            Log("버튼 목록 개체: 창 개체 - 0x%x (배치 함수로 확인)", g_listDelta);
            return a;
        }
    }
    for (int d = 0; d < 0x400; d += 8) {
        if (!LooksLikeList(u - d)) continue;
        g_listDelta = d;
        Log("버튼 목록 개체: 창 개체 - 0x%x (구조로 찾음)", d);
        return u - d;
    }
    return NULL;
}

static std::string WToUtf8(const std::wstring& w)
{
    if (w.empty()) return "";
    int n = WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), NULL, 0, NULL, NULL);
    std::string s(n, 0);
    WideCharToMultiByte(CP_UTF8, 0, w.data(), (int)w.size(), &s[0], n, NULL, NULL);
    return s;
}

// buttons.txt: 첫 줄 "LIST <창 핸들> <버튼 수>", 그다음 버튼마다 "<순번>|<앱 ID>|<실행 파일>|<창 핸들>,<창 핸들>..."
static void DumpButtons(HWND list)
{
    std::string out;
    char line[128];
    BYTE* obj = FindListObj(list);
    BYTE* dpa = NULL; int cnt = 0; BYTE** arr = NULL;
    if (!obj || !SafeRead(obj + kWndDpaOffset, &dpa, 8) || !dpa ||
        !SafeRead(dpa, &cnt, 4) || !SafeRead(dpa + 8, &arr, 8) || cnt <= 0 || cnt > 256 || !arr) {
        sprintf_s(line, "FAIL %llx\n", (u64)list);
        out = line;
    } else {
        sprintf_s(line, "LIST %llx %d\n", (u64)list, cnt);
        out = line;
        for (int i = 0; i < cnt; i++) {
            BYTE* bg = NULL; void* tg = NULL;
            Ident id;
            if (!SafeRead(arr + i, &bg, 8) || !bg || !SafeRead(bg + kBtnGroupTgOffset, &tg, 8) || !tg || !IdentOf(tg, id)) {
                sprintf_s(line, "%d|||\n", i); out += line; continue;
            }
            sprintf_s(line, "%d|", i);
            out += line;
            out += WToUtf8(id.appid) + "|" + WToUtf8(id.exe) + "|";
            BYTE* idpa = NULL; int icnt = 0; BYTE** iarr = NULL;
            if (SafeRead((BYTE*)tg + kTgItemsOffset, &idpa, 8) && idpa &&
                SafeRead(idpa, &icnt, 4) && SafeRead(idpa + 8, &iarr, 8) && icnt > 0 && icnt <= 256 && iarr) {
                for (int k = 0; k < icnt; k++) {
                    void* item = NULL; u64 hwnd = 0;
                    if (!SafeRead(iarr + k, &item, 8) || !item || !CallPtr(item, kItemWindowSlot, hwnd) || !hwnd) continue;
                    sprintf_s(line, "%s%llx", out.back() == '|' ? "" : ",", hwnd);
                    out += line;
                }
            }
            out += "\n";
        }
    }
    wchar_t tmp[MAX_PATH], dst[MAX_PATH];
    wcscpy_s(tmp, g_dir); wcscat_s(tmp, L"buttons.tmp");
    wcscpy_s(dst, g_dir); wcscat_s(dst, L"buttons.txt");
    HANDLE w = CreateFileW(tmp, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (w == INVALID_HANDLE_VALUE) return;
    DWORD x; WriteFile(w, out.data(), (DWORD)out.size(), &x, NULL); CloseHandle(w);
    MoveFileExW(tmp, dst, MOVEFILE_REPLACE_EXISTING);
}

static void SafeDump(HWND list)
{
    __try { DumpButtons(list); }
    __except (EXCEPTION_EXECUTE_HANDLER) { Log("버튼 목록 내보내기 중 예외"); }
}

// 작업표시줄 UI 스레드의 메시지 훅: 우리가 보낸 메시지를 받으면 그 스레드에서 목록을 읽는다
// (버튼 목록을 바꾸는 스레드와 같은 스레드라서 읽는 도중에 목록이 바뀌지 않는다)
static LRESULT CALLBACK MsgHook(int code, WPARAM wp, LPARAM lp)
{
    if (code == HC_ACTION && wp == PM_REMOVE) {
        MSG* m = (MSG*)lp;
        if (g_dumpMsg && m->message == g_dumpMsg) {
            SafeDump(m->hwnd);
            m->message = WM_NULL;
        }
    }
    return CallNextHookEx(g_msgHook, code, wp, lp);
}

static void ProcessButtonsRequest()
{
    wchar_t req[MAX_PATH];
    wcscpy_s(req, g_dir); wcscat_s(req, L"buttons_request.txt");
    if (GetFileAttributesW(req) == INVALID_FILE_ATTRIBUTES) return;
    char buf[64] = { 0 }; DWORD r = 0;
    HANDLE h = CreateFileW(req, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    ReadFile(h, buf, sizeof(buf) - 1, &r, NULL);
    CloseHandle(h);
    DeleteFileW(req);
    if (!g_canDump) return;
    HWND list = (HWND)(ULONG_PTR)_strtoui64(buf, NULL, 16);
    wchar_t cls[64] = { 0 };
    if (!list || !GetClassNameW(list, cls, 64) || wcscmp(cls, L"MSTaskListWClass") != 0) return;
    DWORD pid = 0, tid = GetWindowThreadProcessId(list, &pid);
    if (pid != GetCurrentProcessId()) return;
    if (!g_msgHook) {
        g_msgHook = SetWindowsHookExW(WH_GETMESSAGE, MsgHook, g_self, tid);
        if (!g_msgHook) { Log("메시지 훅 실패 err=%u", GetLastError()); return; }
    }
    PostMessageW(list, g_dumpMsg, 0, 0);
}

// ---------- 작업표시줄 고정/해제 (explorer 안에서 호출) ----------
static const GUID kClsidTaskbandPin = { 0x90aa3a4e, 0x1cba, 0x4233, { 0xb8, 0xbb, 0x53, 0x57, 0x73, 0xd4, 0x84, 0x49 } };
static const GUID kIidPinnedList3 = { 0x0dd79ae2, 0xd156, 0x45d4, { 0x9e, 0xeb, 0x3b, 0x54, 0x97, 0x69, 0xe9, 0x40 } };
struct IPinnedList3Vtbl;
struct IPinnedList3 { IPinnedList3Vtbl* v; };
typedef ULONG(STDMETHODCALLTYPE* ReleaseFn)(IPinnedList3*);
typedef HRESULT(STDMETHODCALLTYPE* ModifyFn)(IPinnedList3*, PCIDLIST_ABSOLUTE, PCIDLIST_ABSOLUTE, int);
struct IPinnedList3Vtbl { void* qi; void* addref; ReleaseFn rel; void* other[13]; ModifyFn modify; };

static HRESULT DoPin(const std::wstring& path, bool pin)
{
    PIDLIST_ABSOLUTE pidl = NULL;
    HRESULT hr = SHParseDisplayName(path.c_str(), NULL, &pidl, 0, NULL);
    if (FAILED(hr) || !pidl) return FAILED(hr) ? hr : E_FAIL;
    IPinnedList3* pl = NULL;
    hr = CoCreateInstance(kClsidTaskbandPin, NULL, CLSCTX_INPROC_SERVER, kIidPinnedList3, (void**)&pl);
    if (SUCCEEDED(hr) && pl) {
        hr = pin ? pl->v->modify(pl, NULL, pidl, INT_MAX) : pl->v->modify(pl, pidl, NULL, INT_MAX);
        pl->v->rel(pl);
    }
    CoTaskMemFree(pidl);
    return hr;
}

static void ProcessPinRequest()
{
    wchar_t req[MAX_PATH], res[MAX_PATH];
    wcscpy_s(req, g_dir); wcscat_s(req, L"pin_request.txt");
    wcscpy_s(res, g_dir); wcscat_s(res, L"pin_result.txt");
    if (GetFileAttributesW(req) == INVALID_FILE_ATTRIBUTES) return;
    std::string data;
    HANDLE h = CreateFileW(req, GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL, OPEN_EXISTING, 0, NULL);
    if (h == INVALID_HANDLE_VALUE) return;
    char buf[2048]; DWORD r;
    while (ReadFile(h, buf, sizeof(buf), &r, NULL) && r) data.append(buf, r);
    CloseHandle(h);
    DeleteFileW(req);
    while (!data.empty() && (data.back() == '\n' || data.back() == '\r')) data.pop_back();
    size_t bar = data.find('|');
    if (bar == std::string::npos) return;
    bool pin = data.substr(0, bar) == "PIN";
    std::wstring path = Utf8ToW(data.substr(bar + 1));
    HRESULT hr = DoPin(path, pin);
    Log("%s %ls -> hr=0x%08x", pin ? "고정" : "해제", path.c_str(), (unsigned)hr);
    char out[64]; int n = sprintf_s(out, "hr=0x%08x", (unsigned)hr);
    HANDLE w = CreateFileW(res, GENERIC_WRITE, FILE_SHARE_READ, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (w != INVALID_HANDLE_VALUE) { DWORD x; WriteFile(w, out, n, &x, NULL); CloseHandle(w); }
}

// .text 에서 sig 가 맞는 곳의 수 (2 이상이면 거기서 멈춤). 한 곳이면 *found 에 그 주소.
static int SearchText(BYTE* base, IMAGE_NT_HEADERS* nt, const BYTE* sig, size_t len, BYTE** found)
{
    *found = NULL;
    IMAGE_SECTION_HEADER* sec = IMAGE_FIRST_SECTION(nt);
    for (WORD i = 0; i < nt->FileHeader.NumberOfSections; i++, sec++) {
        if (memcmp(sec->Name, ".text", 6) != 0) continue;
        BYTE* p = base + sec->VirtualAddress;
        size_t n = sec->Misc.VirtualSize;
        int hits = 0;
        for (size_t k = 0; k + len <= n; k++) {
            if (p[k] != sig[0] || memcmp(p + k, sig, len) != 0) continue;
            *found = p + k;
            if (++hits > 1) break;
        }
        return hits;
    }
    Log(".text 섹션을 못 찾음");
    return 0;
}

// 배치할 함수 주소. 빌드가 같으면 고정 RVA, 다르면 .text 에서 패턴이 정확히 한 곳만 맞을 때 그 주소. 못 찾으면 NULL.
static BYTE* FindCreateTBGroup(BYTE* base, IMAGE_NT_HEADERS* nt)
{
    if (nt->FileHeader.TimeDateStamp == kStamp) return base + kRvaCreateTBGroup;
    BYTE* found;
    int hits = SearchText(base, nt, kSigCreateTBGroup, sizeof(kSigCreateTBGroup), &found);
    Log("패턴 검색: %d곳 일치%s", hits, hits == 1 ? "" : " (한 곳이 아니라서 배치하지 않음)");
    return hits == 1 ? found : NULL;
}

// 후킹을 걸고 요청을 처리하다가, 종료 요청(placer.stop)이 오면 후킹을 풀고 스스로 DLL 을 내린다.
static DWORD WINAPI Worker(LPVOID)
{
    wchar_t stop[MAX_PATH];
    wcscpy_s(stop, g_dir); wcscat_s(stop, L"placer.stop");
    DeleteFileW(stop);                        // 이전에 남은 종료 요청은 무시

    BYTE* base = (BYTE*)GetModuleHandleW(NULL);
    IMAGE_NT_HEADERS* nt = (IMAGE_NT_HEADERS*)(base + ((IMAGE_DOS_HEADER*)base)->e_lfanew);
    bool hooked = false;
    g_modBase = base;
    g_modSize = nt->OptionalHeader.SizeOfImage;
    BYTE* found;
    int itemHits = SearchText(base, nt, kSigGetItemFromWindow, sizeof(kSigGetItemFromWindow), &found);
    g_canDump = itemHits == 1;
    g_dumpMsg = RegisterWindowMessageW(L"TaskbarGroup.DumpButtons");
    Log("버튼 목록 내보내기 %s (창 목록 패턴 %d곳 일치)", g_canDump ? "사용" : "끔", itemHits);
    BYTE* target = FindCreateTBGroup(base, nt);
    if (!target) {
        Log("배치 함수를 찾지 못해 배치는 하지 않음 (stamp=%x size=%x)", nt->FileHeader.TimeDateStamp, nt->OptionalHeader.SizeOfImage);
    } else {
        Log("배치 함수 rva=%x (stamp=%x)", (unsigned)(target - base), nt->FileHeader.TimeDateStamp);
        MH_STATUS st = MH_Initialize();
        if (st == MH_OK) st = MH_CreateHook(target, (LPVOID)HookCreateTBGroup, (LPVOID*)&g_orig);
        if (st == MH_OK) st = MH_EnableHook(MH_ALL_HOOKS);
        hooked = (st == MH_OK);
        Log("배치 시작 status=%d", (int)st);
    }

    CoInitializeEx(NULL, COINIT_APARTMENTTHREADED);
    while (GetFileAttributesW(stop) == INVALID_FILE_ATTRIBUTES) {
        Sleep(100);
        __try { ProcessPinRequest(); } __except (EXCEPTION_EXECUTE_HANDLER) { Log("고정 요청 처리 중 예외"); }
        __try { ProcessButtonsRequest(); } __except (EXCEPTION_EXECUTE_HANDLER) { Log("버튼 목록 요청 처리 중 예외"); }
    }

    if (g_msgHook) {
        UnhookWindowsHookEx(g_msgHook);
        g_msgHook = NULL;
        Sleep(200);                           // UI 스레드에서 돌던 훅 호출이 끝나길 기다림
    }
    if (hooked) {
        MH_DisableHook(MH_ALL_HOOKS);
        Sleep(400);                           // 실행 중이던 후킹 호출이 끝나길 기다림
        MH_Uninitialize();
    }
    CoUninitialize();
    DeleteFileW(stop);
    Log("배치 종료");
    FreeLibrary(g_self);                      // 주입기가 올린 참조
    FreeLibraryAndExitThread(g_self, 0);      // 이 스레드가 잡고 있던 참조 (마지막이면 DLL 해제)
    return 0;
}

BOOL WINAPI DllMain(HMODULE h, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(h);
        InitializeCriticalSection(&g_cs);
        GetModuleFileNameW(h, g_dir, MAX_PATH);
        wchar_t* sl = wcsrchr(g_dir, L'\\');
        if (sl) sl[1] = 0;
        // 작업 스레드가 도는 동안 DLL 이 내려가지 않도록 참조를 하나 더 잡는다
        GetModuleHandleExW(0, (LPCWSTR)&DllMain, &g_self);
        CreateThread(NULL, 0, Worker, NULL, 0, NULL);
    }
    return TRUE;
}
