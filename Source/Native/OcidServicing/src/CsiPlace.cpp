// OCID - CSI 落位执行器实现（见 include/ocid/CsiPlace.h 的机制说明）

#include "ocid/CsiPlace.h"

#include <windows.h>

#include <cstdio>
#include <cwchar>
#include <string>

#include "ocid/SecurityDescriptor.h"

namespace ocid
{
    namespace
    {
        std::string ToUtf8(const std::wstring& text)
        {
            if (text.empty()) return std::string();
            int n = ::WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()),
                                          nullptr, 0, nullptr, nullptr);
            std::string out(static_cast<std::size_t>(n), '\0');
            ::WideCharToMultiByte(CP_UTF8, 0, text.data(), static_cast<int>(text.size()),
                                  out.data(), n, nullptr, nullptr);
            return out;
        }

        std::wstring FromUtf8(const std::string& text)
        {
            if (text.empty()) return std::wstring();
            int n = ::MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), nullptr, 0);
            std::wstring out(static_cast<std::size_t>(n), L'\0');
            ::MultiByteToWideChar(CP_UTF8, 0, text.data(), static_cast<int>(text.size()), out.data(), n);
            return out;
        }

        bool FileExists(const std::wstring& path)
        {
            const DWORD attr = ::GetFileAttributesW(path.c_str());
            return attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY) == 0;
        }

        bool DirExists(const std::wstring& path)
        {
            const DWORD attr = ::GetFileAttributesW(path.c_str());
            return attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY) != 0;
        }

        /// 建目录（逐级）。返回"是否新建了至少一层"。
        bool EnsureDirectory(const std::wstring& path, bool& created, std::string& error)
        {
            created = false;
            if (path.empty()) return true;
            if (DirExists(path)) return true;

            const std::size_t slash = path.find_last_of(L"\\/");
            if (slash != std::wstring::npos && slash > 2)
            {
                if (!EnsureDirectory(path.substr(0, slash), created, error)) return false;
            }
            if (::CreateDirectoryW(path.c_str(), nullptr))
            {
                created = true;
                return true;
            }
            const DWORD code = ::GetLastError();
            if (code == ERROR_ALREADY_EXISTS) return true;
            error = "CreateDirectory 失败（" + ToUtf8(path) + "，错误码 " + std::to_string(code) + "）";
            return false;
        }

        /// 从 JSON 的 `"key":"value"` 里取字符串值。返回值是否存在。
        bool JsonString(const std::string& body, const char* key, std::string& value)
        {
            const std::string needle = std::string("\"") + key + "\":";
            std::size_t pos = body.find(needle);
            if (pos == std::string::npos) return false;
            pos += needle.size();
            while (pos < body.size() && (body[pos] == ' ' || body[pos] == '\t')) ++pos;
            if (pos >= body.size()) return false;
            if (body[pos] == 'n') { value.clear(); return true; } // null
            if (body[pos] != '"') return false;
            ++pos;
            value.clear();
            while (pos < body.size())
            {
                const char c = body[pos++];
                if (c == '\\')
                {
                    if (pos >= body.size()) return false;
                    const char e = body[pos++];
                    switch (e)
                    {
                        case '"': value.push_back('"'); break;
                        case '\\': value.push_back('\\'); break;
                        case '/': value.push_back('/'); break;
                        case 'b': value.push_back('\b'); break;
                        case 'f': value.push_back('\f'); break;
                        case 'n': value.push_back('\n'); break;
                        case 'r': value.push_back('\r'); break;
                        case 't': value.push_back('\t'); break;
                        case 'u':
                        {
                            if (pos + 4 > body.size()) return false;
                            unsigned cp = 0;
                            for (int i = 0; i < 4; ++i)
                            {
                                const char h = body[pos++];
                                cp <<= 4;
                                if (h >= '0' && h <= '9') cp |= static_cast<unsigned>(h - '0');
                                else if (h >= 'a' && h <= 'f') cp |= static_cast<unsigned>(h - 'a' + 10);
                                else if (h >= 'A' && h <= 'F') cp |= static_cast<unsigned>(h - 'A' + 10);
                                else return false;
                            }
                            // UTF-8 编码（含代理对时按单一码元处理，足够覆盖清单里的路径字符）
                            if (cp < 0x80) value.push_back(static_cast<char>(cp));
                            else if (cp < 0x800)
                            {
                                value.push_back(static_cast<char>(0xC0 | (cp >> 6)));
                                value.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
                            }
                            else
                            {
                                value.push_back(static_cast<char>(0xE0 | (cp >> 12)));
                                value.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
                                value.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
                            }
                            break;
                        }
                        default: return false;
                    }
                }
                else if (c == '"')
                {
                    return true;
                }
                else
                {
                    value.push_back(c);
                }
            }
            return false;
        }

        bool JsonBool(const std::string& body, const char* key, bool& value)
        {
            const std::string needle = std::string("\"") + key + "\":";
            std::size_t pos = body.find(needle);
            if (pos == std::string::npos) return false;
            pos += needle.size();
            while (pos < body.size() && (body[pos] == ' ' || body[pos] == '\t')) ++pos;
            if (body.compare(pos, 4, "true") == 0) { value = true; return true; }
            if (body.compare(pos, 5, "false") == 0) { value = false; return true; }
            return false;
        }

        bool GetPrivilege(HANDLE token, const wchar_t* name)
        {
            LUID luid{};
            if (!::LookupPrivilegeValueW(nullptr, name, &luid)) return false;
            TOKEN_PRIVILEGES tp{};
            tp.PrivilegeCount = 1;
            tp.Privileges[0].Luid = luid;
            tp.Privileges[0].Attributes = SE_PRIVILEGE_ENABLED;
            return ::AdjustTokenPrivileges(token, FALSE, &tp, sizeof(tp), nullptr, nullptr) != FALSE &&
                   ::GetLastError() == ERROR_SUCCESS;
        }

        /// 新建的正本目录最后套 WinSxS 的策略：读父目录的 owner/group/DACL 照抄。
        void SecureNewStoreDir(const std::wstring& dir, CsiPlaceResult& r)
        {
            const std::size_t slash = dir.find_last_of(L"\\/");
            if (slash == std::wstring::npos) return;
            const std::wstring parent = dir.substr(0, slash);

            std::wstring sddl, readError;
            const unsigned bits = 0x1u /*Owner*/ | 0x2u /*Group*/ | 0x4u /*Dacl*/;
            if (!ReadPathSddl(parent, bits, sddl, readError))
            {
                r.notes.push_back("[注意] 读不到 " + ToUtf8(parent) + " 的安全描述符，新目录沿用继承：" + ToUtf8(readError));
                return;
            }
            const SddlApplyResult result = ApplySddlToPath(dir, sddl, readError);
            if (result == SddlApplyResult::Applied)
            {
                ++r.storeDirSecured;
            }
            else
            {
                r.notes.push_back("[注意] 新正本目录 " + ToUtf8(dir) + " 套安全策略失败：" + ToUtf8(readError));
            }
        }
    }

    std::string CsiPlaceResult::Summary() const
    {
        char buf[320];
        std::snprintf(buf, sizeof(buf),
                      "planned=%d placed=%d copied=%d linked=%d dirs=%d acl=%d storeDirSecured=%d "
                      "already=%d skipped=%d missingPayload=%d failed=%d",
                      planned, placed, copied, linked, dirs, aclApplied, storeDirSecured,
                      alreadyPresent, skipped, missingPayload, failed);
        return std::string(buf);
    }

    bool EnableCsiPlacePrivileges(std::string& error)
    {
        HANDLE token = nullptr;
        if (!::OpenProcessToken(::GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, &token))
        {
            error = "OpenProcessToken 失败（错误码 " + std::to_string(::GetLastError()) + "）";
            return false;
        }
        const bool restore = GetPrivilege(token, SE_RESTORE_NAME);
        const bool backup = GetPrivilege(token, SE_BACKUP_NAME);
        ::CloseHandle(token);
        if (!restore && !backup)
        {
            error = "SeRestorePrivilege/SeBackupPrivilege 都取不到（不是管理员？）";
            return false;
        }
        if (!restore) error = "SeRestorePrivilege 取不到（写 WinSxS 会失败）";
        else if (!backup) error = "SeBackupPrivilege 取不到（读受限文件会失败）";
        return restore;
    }

    bool ParseCsiPlaceJsonLine(const std::string& line, CsiPlaceOp& op, std::string& error)
    {
        op = CsiPlaceOp{};
        if (line.find_first_not_of(" \t\r\n") == std::string::npos) return false;
        if (line[line.find_first_not_of(" \t\r\n")] != '{')
        {
            error = "不是 JSON 对象";
            return false;
        }
        std::string value;
        if (JsonString(line, "component", value)) op.component = value;
        if (JsonString(line, "identity", value)) op.identity = value;
        if (JsonString(line, "kind", value)) op.kind = value;

        std::wstring wide;
        if (JsonString(line, "path", value)) op.path = FromUtf8(value);
        if (JsonString(line, "storeDir", value)) op.storeDir = FromUtf8(value);
        if (JsonString(line, "storePath", value)) op.storePath = FromUtf8(value);
        if (JsonString(line, "destinationPath", value)) op.destinationPath = FromUtf8(value);
        if (JsonString(line, "payloadPath", value)) op.payloadPath = FromUtf8(value);
        if (JsonString(line, "sddl", value)) op.sddl = FromUtf8(value);
        if (JsonString(line, "unresolved", value)) op.unresolved = FromUtf8(value);
        bool flag = false;
        if (JsonBool(line, "redirected", flag)) op.redirected = flag;
        return true;
    }

    CsiPlaceResult PlaceCsiOps(const std::vector<CsiPlaceOp>& ops, bool applyAcl)
    {
        CsiPlaceResult r;
        std::string privilegeError;
        if (!EnableCsiPlacePrivileges(privilegeError) && !privilegeError.empty())
            r.notes.push_back("[注意] " + privilegeError);

        std::vector<std::wstring> newStoreDirs;

        for (const CsiPlaceOp& op : ops)
        {
            ++r.planned;

            if (!op.unresolved.empty())
            {
                ++r.skipped;
                r.notes.push_back("[跳过] " + op.component + "：含未展开宏 " + ToUtf8(op.unresolved));
                continue;
            }

            if (op.kind == "directory")
            {
                bool created = false;
                std::string err;
                if (!EnsureDirectory(op.path, created, err))
                {
                    ++r.failed;
                    r.notes.push_back("[失败] 目录 " + ToUtf8(op.path) + "：" + err);
                    continue;
                }
                if (created) ++r.dirs;
                if (applyAcl && !op.sddl.empty() && !IsInheritSddl(op.sddl))
                {
                    std::wstring aclError;
                    if (ApplySddlToPath(op.path, op.sddl, aclError) == SddlApplyResult::Applied) ++r.aclApplied;
                    else r.notes.push_back("[注意] 目录 ACL " + ToUtf8(op.path) + "：" + ToUtf8(aclError));
                }
                ++r.placed;
                continue;
            }

            // ---- 文件 ----
            if (op.destinationPath.empty())
            {
                ++r.skipped;
                r.notes.push_back("[跳过] " + op.component + "：" + ToUtf8(op.storePath) + " 没有 destinationPath（驱动类，归 DMI）");
                continue;
            }

            bool storeExisted = FileExists(op.storePath);
            if (!storeExisted)
            {
                if (!FileExists(op.payloadPath))
                {
                    ++r.missingPayload;
                    r.notes.push_back("[缺载荷] " + op.component + "：" + ToUtf8(op.payloadPath) + " 不存在");
                    continue;
                }
                std::string err;
                bool created = false;
                if (!EnsureDirectory(op.storeDir, created, err))
                {
                    ++r.failed;
                    r.notes.push_back("[失败] 正本目录 " + ToUtf8(op.storeDir) + "：" + err);
                    continue;
                }
                if (created) { ++r.dirs; newStoreDirs.push_back(op.storeDir); }
                if (!::CopyFileW(op.payloadPath.c_str(), op.storePath.c_str(), FALSE))
                {
                    ++r.failed;
                    r.notes.push_back("[失败] 复制 " + ToUtf8(op.payloadPath) + " -> " + ToUtf8(op.storePath) +
                                      "（错误码 " + std::to_string(::GetLastError()) + "）");
                    continue;
                }
                ++r.copied;
                storeExisted = true;
            }

            bool destExisted = FileExists(op.destinationPath);
            if (!destExisted)
            {
                const std::size_t slash = op.destinationPath.find_last_of(L"\\/");
                if (slash != std::wstring::npos && slash > 2)
                {
                    std::string err;
                    bool created = false;
                    if (!EnsureDirectory(op.destinationPath.substr(0, slash), created, err))
                    {
                        ++r.failed;
                        r.notes.push_back("[失败] 落点目录 " + ToUtf8(op.destinationPath) + "：" + err);
                        continue;
                    }
                    if (created) ++r.dirs;
                }
                if (!::CreateHardLinkW(op.destinationPath.c_str(), op.storePath.c_str(), nullptr))
                {
                    // 有的文件系统（如跨卷）不支持硬链接；退化成复制，并在 notes 里说清楚。
                    if (::CopyFileW(op.storePath.c_str(), op.destinationPath.c_str(), FALSE))
                    {
                        ++r.copied;
                        r.notes.push_back("[注意] 硬链接失败，落点退化为复制：" + ToUtf8(op.destinationPath));
                    }
                    else
                    {
                        ++r.failed;
                        r.notes.push_back("[失败] 落点 " + ToUtf8(op.destinationPath) +
                                          "（错误码 " + std::to_string(::GetLastError()) + "）");
                        continue;
                    }
                }
                else
                {
                    ++r.linked;
                }
                destExisted = true;
            }
            else
            {
                ++r.alreadyPresent;
            }

            if (destExisted) ++r.placed;

            if (applyAcl && !op.sddl.empty() && !IsInheritSddl(op.sddl))
            {
                std::wstring aclError;
                if (ApplySddlToPath(op.destinationPath, op.sddl, aclError) == SddlApplyResult::Applied) ++r.aclApplied;
                else r.notes.push_back("[注意] 文件 ACL " + ToUtf8(op.destinationPath) + "：" + ToUtf8(aclError));
            }

            if (storeExisted && !destExisted) ++r.failed;
        }

        // 新建的正本目录最后统一上锁（顺序见文件头注释）
        if (applyAcl)
        {
            for (const std::wstring& dir : newStoreDirs) SecureNewStoreDir(dir, r);
        }
        return r;
    }
}
