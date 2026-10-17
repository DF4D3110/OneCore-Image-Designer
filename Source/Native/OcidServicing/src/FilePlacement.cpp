// OCID - CSI 落位计划（实现）
//
// 机制与实测证据见 `ocid/FilePlacement.h` 顶部注释。这里只做三件事：
//   1) 把 `destinationPath` 里的 `$(runtime.*)` 宏展开（`MacroExpander`）
//   2) 拼出"载荷 → 正本 → 落点"三个路径
//   3) 把 `<securityDescriptor name="…"/>` 按清单的定义表**解析成 SDDL 原文**
//      （解析不了必须显式记下来：静默丢掉 ACL 会得到一个"看起来成功"的错误镜像）

#include <windows.h>

#include "ocid/FilePlacement.h"
#include "ocid/SecurityDescriptor.h"

namespace ocid {

    namespace {

        std::wstring TrimTrailingSlashes(std::wstring s)
        {
            while (!s.empty() && (s.back() == L'\\' || s.back() == L'/')) s.pop_back();
            return s;
        }

        /// 只用反斜杠拼接（清单里目标路径一律是 Windows 形式）。
        std::wstring Join(const std::wstring& a, const std::wstring& b)
        {
            if (a.empty()) return b;
            if (b.empty()) return a;
            std::wstring r = TrimTrailingSlashes(a);
            r += L'\\';
            r += (b.front() == L'\\' || b.front() == L'/') ? b.substr(1) : b;
            return r;
        }

        std::wstring JoinList(const std::vector<std::wstring>& v)
        {
            std::wstring r;
            for (const auto& s : v) {
                if (!r.empty()) r += L' ';
                r += s;
            }
            return r;
        }

        bool Exists(const std::wstring& p)
        {
            if (p.empty()) return false;
            return ::GetFileAttributesW(p.c_str()) != INVALID_FILE_ATTRIBUTES;
        }

        /// 解析一条 `<securityDescriptor name="…"/>` 引用。
        /// 返回是否**成功**（空引用名按"没引用"处理，算成功）。
        bool ResolveRef(const SddlDefinitions& defs, const std::wstring& name,
                        std::wstring& sddl, std::wstring& err)
        {
            sddl.clear();
            err.clear();
            if (name.empty()) return true;
            return ResolveSddl(defs, name, sddl, err);
        }

    } // namespace

    std::size_t PlacementPlan::executableFiles() const
    {
        std::size_t n = 0;
        for (const auto& f : files) {
            if (!f.destinationPath.empty() && f.unresolved.empty()) ++n;
        }
        return n;
    }

    std::wstring ComponentArchOf(const std::wstring& componentDir)
    {
        const std::size_t p = componentDir.find(L'_');
        if (p == std::wstring::npos || p == 0) return std::wstring();
        return componentDir.substr(0, p);
    }

    bool IsWow64Redirected(const std::wstring& componentArch, const std::wstring& imageArch)
    {
        if (componentArch.empty() || imageArch.empty()) return false;
        // ★ 只列**实测确认**的组合：8061 ARM 镜像（imageArch=arm）上的 x86 组件。
        //   裸写"x86 组件在非 x86 镜像上就红移"会把没量过的组合一并断言成事实。
        if (_wcsicmp(componentArch.c_str(), L"x86") == 0 &&
            _wcsicmp(imageArch.c_str(), L"arm") == 0) {
            return true;
        }
        return false;
    }

    std::wstring DetectImageArch(const std::wstring& windowsDir)
    {
        if (windowsDir.empty()) return std::wstring();

        const std::wstring kernel = Join(Join(TrimTrailingSlashes(windowsDir), L"System32"), L"ntoskrnl.exe");
        HANDLE h = ::CreateFileW(kernel.c_str(), GENERIC_READ,
                                 FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                                 nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (h == INVALID_HANDLE_VALUE) return std::wstring();

        unsigned char buf[4096] = {};
        DWORD got = 0;
        const BOOL read = ::ReadFile(h, buf, sizeof(buf), &got, nullptr);
        ::CloseHandle(h);
        if (!read || got < 0x40) return std::wstring();

        const unsigned char* b = buf;
        if (b[0] != 'M' || b[1] != 'Z') return std::wstring();
        const unsigned long peOff = static_cast<unsigned long>(b[0x3C]) |
                                    (static_cast<unsigned long>(b[0x3D]) << 8) |
                                    (static_cast<unsigned long>(b[0x3E]) << 16) |
                                    (static_cast<unsigned long>(b[0x3F]) << 24);
        // 本函数只读前 4 KB：PE 头正常就在文件开头（e_lfanew 一般 < 0x200），
        // 超出就说明这个文件不是普通 PE，宁可不判定。
        if (peOff + 6 > sizeof(buf) || peOff + 6 > got) return std::wstring();
        if (b[peOff] != 'P' || b[peOff + 1] != 'E' || b[peOff + 2] != 0 || b[peOff + 3] != 0) {
            return std::wstring();
        }
        const unsigned machine = static_cast<unsigned>(b[peOff + 4]) |
                                 (static_cast<unsigned>(b[peOff + 5]) << 8);
        switch (machine) {
        case 0x014C: return L"x86";
        case 0x8664: return L"amd64";
        case 0x01C0: return L"arm";
        case 0x01C4: return L"arm";      // ARMNT（实测的 8061 镜像就是它）
        case 0xAA64: return L"arm64";
        default:     return std::wstring();
        }
    }

    PlacementPlan BuildPlacementPlan(const ComponentManifest& manifest,
                                     const std::wstring& componentDir,
                                     const std::wstring& packageRoot,
                                     const std::wstring& windowsDir,
                                     const std::wstring& imageArch)
    {
        return BuildPlacementPlan(manifest, componentDir, packageRoot,
                                  RuntimePaths::FromWindowsDir(windowsDir), imageArch);
    }

    PlacementPlan BuildPlacementPlan(const ComponentManifest& manifest,
                                     const std::wstring& componentDir,
                                     const std::wstring& packageRoot,
                                     const RuntimePaths& paths,
                                     const std::wstring& imageArch)
    {
        PlacementPlan plan;
        plan.identity      = manifest.identity.ToString();
        plan.componentDir  = componentDir;
        plan.storeDir      = Join(Join(paths.windows, L"WinSxS"), componentDir);
        plan.componentArch = ComponentArchOf(componentDir);
        plan.imageArch     = imageArch;

        const bool redirect = IsWow64Redirected(plan.componentArch, plan.imageArch);
        // 要求红移判定、却连架构都推不出来 —— 必须说出来（否则就是悄悄按 System32 落位）。
        if (!imageArch.empty() && plan.componentArch.empty()) {
            plan.notes.push_back(L"组件目录名里没有架构前缀，无法判定是否红移：" + componentDir);
        }
        const std::wstring sys32 = Join(TrimTrailingSlashes(paths.windows), L"System32");
        const std::wstring wow64 = Join(TrimTrailingSlashes(paths.windows), L"SysWOW64");

        // 把落点开头的 `<windows>\System32\` 换成 `<windows>\SysWOW64\`
        // （大小写不敏感：清单里 `$(Runtime.System32)` 与大写 `$(RUNTIME.SYSTEM32)` 都出现过）。
        auto applyRedirection = [&](std::wstring& p) -> bool {
            if (p.size() <= sys32.size()) return false;
            if (_wcsnicmp(p.c_str(), sys32.c_str(), sys32.size()) != 0) return false;
            const wchar_t nxt = p[sys32.size()];
            if (nxt != L'\\' && nxt != L'/') return false;   // `System32dll` 这种不算
            p = wow64 + p.substr(sys32.size());
            return true;
        };

        const std::wstring payloadRoot =
            (packageRoot.empty() || componentDir.empty()) ? std::wstring() : Join(packageRoot, componentDir);

        // ── `<file>` ────────────────────────────────────────────────────────
        for (const auto& f : manifest.files) {
            std::wstring sddl, sdErr;
            if (!ResolveRef(manifest.securityDescriptorDefinitions, f.securityDescriptorName, sddl, sdErr)) {
                ++plan.sddlUnresolved;
                plan.notes.push_back(L"文件的安全描述符引用解析失败：" + f.name + L" ← "
                                     + f.securityDescriptorName + L"（" + sdErr + L"）");
            }

            // 没有 destinationPath 的一律**不猜**：驱动/存储类的落位规则不同。
            if (f.destinationPath.empty()) {
                ++plan.filesWithoutDestination;
                plan.notes.push_back(L"无 destinationPath（不落位，另属驱动/存储类）：name=" + f.name
                                     + L" source=" + f.sourceName
                                     + (f.securityDescriptorName.empty()
                                            ? std::wstring()
                                            : L" sd=" + f.securityDescriptorName));
                continue;
            }

            FilePlacementOp op;
            op.name = f.name.empty() ? f.sourceName : f.name;
            op.sddl = sddl;
            op.sdName = f.securityDescriptorName;
            op.writeableType = f.writeableType;
            op.writeable = !f.writeableType.empty();

            const MacroExpansion exp = ExpandMacros(f.destinationPath, paths);
            op.unresolved = JoinList(exp.unresolved);
            if (!op.unresolved.empty()) ++plan.unresolvedCount;

            op.destinationPath = Join(TrimTrailingSlashes(exp.text), op.name);
            op.storePath       = Join(plan.storeDir, op.name);

            if (redirect && applyRedirection(op.destinationPath)) {
                op.redirected = true;
                ++plan.redirectedFiles;
            }
            if (op.writeable) ++plan.writeableFiles;

            if (!payloadRoot.empty()) {
                const std::wstring src = f.sourceName.empty() ? op.name : f.sourceName;
                op.payloadPath = Join(payloadRoot, src);
                if (!Exists(op.payloadPath)) {
                    ++plan.missingPayload;
                    plan.notes.push_back(L"载荷在包展开目录里不存在（只能落正本+链接，没有源可拷）："
                                         + op.payloadPath);
                }
            }

            plan.files.push_back(std::move(op));
        }

        // ── `<directories><directory>` ──────────────────────────────────────
        for (const auto& d : manifest.directories) {
            DirectoryPlacementOp op;
            op.attributes = d.attributes;
            op.owner      = d.owner;
            op.sdName     = d.securityDescriptorName;

            std::wstring sddl, sdErr;
            if (!ResolveRef(manifest.securityDescriptorDefinitions, d.securityDescriptorName, sddl, sdErr)) {
                ++plan.sddlUnresolved;
                plan.notes.push_back(L"目录的安全描述符引用解析失败：" + d.destinationPath + L" ← "
                                     + d.securityDescriptorName + L"（" + sdErr + L"）");
            }
            op.sddl = sddl;

            const MacroExpansion exp = ExpandMacros(d.destinationPath, paths);
            op.unresolved = JoinList(exp.unresolved);
            op.path = TrimTrailingSlashes(exp.text);

            if (op.path.empty()) {
                plan.notes.push_back(L"目录 destinationPath 为空，跳过");
                continue;
            }
            if (!op.unresolved.empty()) ++plan.unresolvedCount;

            plan.directories.push_back(std::move(op));
        }

        return plan;
    }

} // namespace ocid
