#include "ocid/CbsProfile.h"

#include <cstdint>
#include <cstring>

#ifdef _WIN32
#include <windows.h>
#pragma comment(lib, "version.lib")  // GetFileVersionInfoW / VerQueryValueW
#endif

namespace ocid
{
    namespace
    {
        bool ParseInt(const std::string& text, std::size_t begin, std::size_t end, std::int32_t& value)
        {
            if (begin >= end)
                return false;
            std::int64_t acc = 0;
            for (std::size_t i = begin; i < end; ++i)
            {
                const char c = text[i];
                if (c < '0' || c > '9')
                    return false;
                acc = acc * 10 + (c - '0');
                if (acc > 0x7FFFFFFF)
                    return false;
            }
            value = static_cast<std::int32_t>(acc);
            return true;
        }
    }

    bool CbsBuildFromVersionText(const std::string& text, std::int32_t& build)
    {
        // 形如 "6.3.9600.16384 (winblue_rtm_partner.130828-1900)"：
        // 取第三个点分段（构建号）。允许前面有空白，允许后面跟任意内容。
        std::size_t pos = 0;
        while (pos < text.size() && (text[pos] == ' ' || text[pos] == '\t'))
            ++pos;
        std::int32_t parts[4] = {0, 0, 0, 0};
        int partCount = 0;
        std::size_t segment = pos;
        for (std::size_t i = pos; i <= text.size() && partCount < 4; ++i)
        {
            const bool atEnd = (i == text.size());
            const char c = atEnd ? '.' : text[i];
            if (c == '.')
            {
                if (!ParseInt(text, segment, i, parts[partCount]))
                    return false;
                ++partCount;
                segment = i + 1;
                continue;
            }
            if (c < '0' || c > '9')
                break;
        }
        if (partCount < 3)
            return false;
        build = parts[2];
        return build > 0;
    }

    bool CbsBuildFromImageFile(const std::wstring& path, std::int32_t& build, std::string& error)
    {
#ifdef _WIN32
        DWORD ignored = 0;
        const DWORD size = GetFileVersionInfoSizeW(path.c_str(), &ignored);
        if (size == 0)
        {
            error = "没有版本资源（或文件不存在）";
            return false;
        }
        std::string buffer(size, '\0');
        if (!GetFileVersionInfoW(path.c_str(), 0, size, &buffer[0]))
        {
            error = "读版本资源失败";
            return false;
        }
        VS_FIXEDFILEINFO* info = nullptr;
        UINT infoBytes = 0;
        if (!VerQueryValueW(buffer.data(), L"\\", reinterpret_cast<void**>(&info), &infoBytes) ||
            info == nullptr || infoBytes < sizeof(VS_FIXEDFILEINFO))
        {
            error = "版本资源里没有 VS_FIXEDFILEINFO";
            return false;
        }
        build = static_cast<std::int32_t>(HIWORD(info->dwFileVersionLS));
        if (build <= 0)
        {
            error = "版本资源的构建号是 0";
            return false;
        }
        return true;
#else
        (void)path;
        (void)build;
        error = "只有 Windows 上能读 PE 版本资源";
        return false;
#endif
    }

    CbsVersionProfile CbsProfileForBuild(std::int32_t build)
    {
        CbsVersionProfile profile;
        profile.build = build <= 0 ? 0 : build;
        if (profile.build <= 0)
        {
            profile.family = "legacy";
            return profile;
        }

        const int b = profile.build;
        if (b < 9600)
        {
            // 6.1 / 6.2：明文清单、字符串 f!、CF 只用 0x3F 内、驱动部署不写 thumb。
            profile.family = b < 7600 ? "win7" : "win8";
        }
        else if (b < 10240)
        {
            profile.family = "win81";
        }
        else if (b < 15063)
        {
            profile.family = "win10-th";
        }
        else if (b < 16299)
        {
            profile.family = "win10-rs";
        }
        else
        {
            profile.family = "win10-rs3+";
        }

        if (b >= 9600)
        {
            profile.dcmManifests = true;
            profile.fileValuesAreFlags = true;
            profile.configFlagsFullDword = true;
            profile.catalogForDrivers = true;
            profile.sha256OfDecodedManifest = true;
            profile.f256FromDigestValue = true;
        }
        // Drivers 台账：14357 没有，10.0.16212 已经出现（1 个键）⇒ 阈值取 16212。
        if (b >= 16212)
            profile.driversLedger = true;
        // 载荷值名里的连续 `_` 折叠：9600 的 Product-WinPE__Microsoft-… 在真机台账
        // 里已经写成 `product-winpe_microsoft-…`（118 个载荷实测）⇒ 与 DCM 同代。
        if (b >= 9600)
            profile.payloadNameCollapse = true;
        // identity 的现代归一（.inf.resources 整名小写、en-us→en-US）从 16299 起。
        if (b >= 16299)
            profile.modernIdentity = true;
        return profile;
    }

    std::string CbsProfileSummary(const CbsVersionProfile& profile)
    {
        std::string out = "build=";
        out += profile.build > 0 ? std::to_string(profile.build) : "unknown";
        out += " family=";
        out += profile.family;
        auto append = [&out](const char* name, bool on) {
            out += ' ';
            out += name;
            out += on ? "=on" : "=off";
        };
        append("dcm", profile.dcmManifests);
        append("f!dword", profile.fileValuesAreFlags);
        append("cf32", profile.configFlagsFullDword);
        append("drvthumb", profile.catalogForDrivers);
        append("drvledger", profile.driversLedger);
        append("payload-collapse", profile.payloadNameCollapse);
        append("identity-modern", profile.modernIdentity);
        return out;
    }
}
