// OCID - 控制台 UTF-8 输出
//
// 为什么需要这个：Windows 的 CRT 在窄字符 `printf("%ls", …)` 时按 **C locale** 做
// 宽→窄转换，遇到非 ASCII（本项目的日志与诊断全是中文）会变成 `?` 甚至整段消失。
// 不是"显示乱码"那么轻——是**诊断信息直接没了**，排障时会以为程序没输出。
//
// 正确做法两步：把 stdout 设成二进制（避免 CRT 再插 `\r`），把控制台代码页设成 UTF-8，
// 然后**自己**把宽字符转成 UTF-8 字节再用 `%s` 打印。
//
// 只放在这里一份：`tools/ocid_manifest.cpp` 与各测试都要用，两份实现必然漂移。

#pragma once

#include <string>

#ifdef _WIN32
#include <windows.h>
#include <io.h>
#include <fcntl.h>
#endif

namespace ocid
{
    /// 在 `main()` 开头调一次（不改 stdin 之外的行为，可重复调用）。
    inline void SetupConsoleUtf8()
    {
#ifdef _WIN32
        _setmode(_fileno(stdout), _O_BINARY);
        SetConsoleOutputCP(CP_UTF8);
#endif
    }

    /// 宽字符 → UTF-8 字节。失败（极少数非法代理对）时退回逐字符截断，不抛。
    inline std::string ToUtf8(const std::wstring& w)
    {
#ifdef _WIN32
        if (w.empty()) return std::string();
        int n = WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), nullptr, 0, nullptr, nullptr);
        if (n <= 0) return std::string();
        std::string out((size_t)n, '\0');
        WideCharToMultiByte(CP_UTF8, 0, w.c_str(), (int)w.size(), &out[0], n, nullptr, nullptr);
        return out;
#else
        return std::string(w.begin(), w.end());
#endif
    }

    /// UTF-8 字节 → 宽字符（`argv` 用；非法 UTF-8 时按 ANSI 再试一次）。
    inline std::wstring FromUtf8OrAnsi(const char* s)
    {
#ifdef _WIN32
        if (!s) return std::wstring();
        int n = MultiByteToWideChar(CP_UTF8, MB_ERR_INVALID_CHARS, s, -1, nullptr, 0);
        if (n > 0)
        {
            std::wstring w((size_t)(n - 1), L'\0');
            MultiByteToWideChar(CP_UTF8, 0, s, -1, &w[0], n);
            return w;
        }
        n = MultiByteToWideChar(CP_ACP, 0, s, -1, nullptr, 0);
        if (n <= 0) return std::wstring();
        std::wstring w((size_t)(n - 1), L'\0');
        MultiByteToWideChar(CP_ACP, 0, s, -1, &w[0], n);
        return w;
#else
        return s ? std::wstring(s, s + std::char_traits<char>::length(s)) : std::wstring();
#endif
    }

} // namespace ocid
