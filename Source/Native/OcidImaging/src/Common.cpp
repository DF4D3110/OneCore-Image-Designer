// OcidImaging / Common —— 基础层实现
#include "ocid/imaging/Common.h"

#include <cstdio>
#include <cstring>
#include <ctime>
#include <algorithm>
#include <filesystem>
#include <mutex>

namespace ocid::imaging {
namespace {

std::mutex g_logLock;
FILE* g_log = nullptr;

// 解析器：只处理 imageapp 那些 XML 实际用到的语法（元素/属性/文本/CDATA/注释/PI/DOCTYPE）。
// 不做命名空间解析——调用方一律按"局部名"比较，见 Common.h 的说明。
class Parser {
public:
    Parser(const std::string& text, std::string& error) : text_(text), error_(error) {}

    std::unique_ptr<XmlNode> ParseDocument() {
        SkipBom();
        SkipWhitespace();
        while (Peek() == '<') {
            if (StartsWith("<?")) { SkipPast("?>"); SkipWhitespace(); }
            else if (StartsWith("<!--")) { SkipPast("-->"); SkipWhitespace(); }
            else if (StartsWith("<!")) { SkipPast(">"); SkipWhitespace(); }
            else break;
        }
        if (Peek() != '<') { Fail("文档里没有根元素"); return nullptr; }
        auto root = ParseElement(nullptr);
        if (!root) return nullptr;
        SkipWhitespace();
        for (;;) {
            if (i_ >= text_.size()) break;
            if (StartsWith("<!--")) { SkipPast("-->"); SkipWhitespace(); continue; }
            if (StartsWith("<?")) { SkipPast("?>"); SkipWhitespace(); continue; }
            // 根元素之后的杂项（空白或多余内容）直接忽略，imageapp 的输入不会出现多个根。
            ++i_;
        }
        return root;
    }

private:
    void SkipBom() {
        if (text_.size() >= 3 && static_cast<unsigned char>(text_[0]) == 0xEF &&
            static_cast<unsigned char>(text_[1]) == 0xBB && static_cast<unsigned char>(text_[2]) == 0xBF) {
            i_ = 3;
        }
    }

    char Peek() const { return i_ < text_.size() ? text_[i_] : '\0'; }

    bool StartsWith(const char* token) const {
        const std::size_t len = std::strlen(token);
        return text_.compare(i_, len, token) == 0;
    }

    void SkipWhitespace() {
        while (i_ < text_.size()) {
            const char c = text_[i_];
            if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { ++i_; continue; }
            break;
        }
    }

    void SkipPast(const char* token) {
        const std::size_t at = text_.find(token, i_);
        i_ = (at == std::string::npos) ? text_.size() : at + std::strlen(token);
    }

    void Fail(const char* what) {
        if (error_.empty()) {
            error_ = std::string(what) + "（偏移 " + std::to_string(i_) + "）";
        }
    }

    static void SplitLocal(const std::string& raw, XmlNode& node) {
        const std::size_t colon = raw.find(':');
        node.local = (colon == std::string::npos) ? raw : raw.substr(colon + 1);
    }

    std::string ReadName() {
        std::size_t start = i_;
        while (i_ < text_.size()) {
            const char c = text_[i_];
            const bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') ||
                            c == '_' || c == '-' || c == '.' || c == ':';
            if (!ok) break;
            ++i_;
        }
        return text_.substr(start, i_ - start);
    }

    std::string ReadQuoted() {
        if (Peek() != '"' && Peek() != '\'') { Fail("属性值缺少引号"); return {}; }
        const char quote = text_[i_++];
        std::string value;
        while (i_ < text_.size() && text_[i_] != quote) {
            value.push_back(text_[i_++]);
        }
        if (i_ < text_.size()) ++i_;
        return DecodeEntities(value);
    }

    static void AppendUtf8(std::string& out, std::uint32_t cp) {
        if (cp < 0x80) {
            out.push_back(static_cast<char>(cp));
        } else if (cp < 0x800) {
            out.push_back(static_cast<char>(0xC0 | (cp >> 6)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        } else if (cp < 0x10000) {
            out.push_back(static_cast<char>(0xE0 | (cp >> 12)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        } else {
            out.push_back(static_cast<char>(0xF0 | (cp >> 18)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
            out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
        }
    }

    std::string DecodeEntities(const std::string& raw) {
        if (raw.find('&') == std::string::npos) return raw;
        std::string out;
        out.reserve(raw.size());
        std::size_t p = 0;
        while (p < raw.size()) {
            if (raw[p] != '&') { out.push_back(raw[p++]); continue; }
            const std::size_t semi = raw.find(';', p);
            if (semi == std::string::npos) { out.push_back(raw[p++]); continue; }
            const std::string name = raw.substr(p + 1, semi - p - 1);
            if (name == "amp") out.push_back('&');
            else if (name == "lt") out.push_back('<');
            else if (name == "gt") out.push_back('>');
            else if (name == "quot") out.push_back('"');
            else if (name == "apos") out.push_back('\'');
            else if (name.size() > 1 && name[0] == '#') {
                std::uint32_t cp = 0;
                if (name[1] == 'x' || name[1] == 'X') {
                    for (std::size_t k = 2; k < name.size(); ++k) {
                        const char c = name[k];
                        std::uint32_t digit = 0;
                        if (c >= '0' && c <= '9') digit = static_cast<std::uint32_t>(c - '0');
                        else if (c >= 'a' && c <= 'f') digit = static_cast<std::uint32_t>(c - 'a' + 10);
                        else if (c >= 'A' && c <= 'F') digit = static_cast<std::uint32_t>(c - 'A' + 10);
                        else { cp = 0; break; }
                        cp = cp * 16 + digit;
                    }
                } else {
                    for (std::size_t k = 1; k < name.size(); ++k) {
                        const char c = name[k];
                        if (c < '0' || c > '9') { cp = 0; break; }
                        cp = cp * 10 + static_cast<std::uint32_t>(c - '0');
                    }
                }
                if (cp != 0) AppendUtf8(out, cp);
            } else {
                out.append(raw, p, semi - p + 1);
            }
            p = semi + 1;
        }
        return out;
    }

    std::unique_ptr<XmlNode> ParseElement(XmlNode* parent) {
        if (Peek() != '<') { Fail("期望元素开始"); return nullptr; }
        ++i_;
        auto node = std::make_unique<XmlNode>();
        node->parent = parent;
        node->name = ReadName();
        if (node->name.empty()) { Fail("元素名为空"); return nullptr; }
        SplitLocal(node->name, *node);

        // 属性
        for (;;) {
            SkipWhitespace();
            if (Peek() == '/') {
                if (i_ + 1 < text_.size() && text_[i_ + 1] == '>') { i_ += 2; return node; }
                Fail("自闭合标签不完整");
                return nullptr;
            }
            if (Peek() == '>') { ++i_; break; }
            if (i_ >= text_.size()) { Fail("元素没有结束"); return nullptr; }
            XmlAttr attr;
            attr.name = ReadName();
            if (attr.name.empty()) { Fail("属性名为空"); return nullptr; }
            const std::size_t colon = attr.name.find(':');
            attr.local = (colon == std::string::npos) ? attr.name : attr.name.substr(colon + 1);
            SkipWhitespace();
            if (Peek() != '=') { Fail("属性缺少 ="); return nullptr; }
            ++i_;
            SkipWhitespace();
            attr.value = ReadQuoted();
            node->attrs.push_back(std::move(attr));
        }

        // 内容
        std::string text;
        for (;;) {
            if (i_ >= text_.size()) { Fail("元素内容未闭合"); return nullptr; }
            if (StartsWith("</")) {
                i_ += 2;
                const std::string closing = ReadName();
                if (closing != node->name) {
                    Fail("闭合标签不匹配");
                    return nullptr;
                }
                SkipWhitespace();
                if (Peek() != '>') { Fail("闭合标签缺少 >"); return nullptr; }
                ++i_;
                node->text = DecodeEntities(text);
                return node;
            }
            if (StartsWith("<!--")) { SkipPast("-->"); continue; }
            if (StartsWith("<![CDATA[")) {
                const std::size_t at = text_.find("]]>", i_);
                if (at == std::string::npos) { Fail("CDATA 未闭合"); return nullptr; }
                text.append(text_, i_ + 9, at - (i_ + 9));
                i_ = at + 3;
                continue;
            }
            if (StartsWith("<?")) { SkipPast("?>"); continue; }
            if (Peek() == '<') {
                auto child = ParseElement(node.get());
                if (!child) return nullptr;
                node->kids.push_back(std::move(child));
                continue;
            }
            text.push_back(text_[i_++]);
        }
    }

    const std::string& text_;
    std::string& error_;
    std::size_t i_ = 0;
};

std::string Timestamp() {
    std::time_t now = std::time(nullptr);
    std::tm local{};
    localtime_s(&local, &now);
    char buffer[64];
    std::snprintf(buffer, sizeof(buffer), "{%d/%d/%d %d:%02d:%02d}  ",
                  local.tm_year + 1900, local.tm_mon + 1, local.tm_mday,
                  local.tm_hour, local.tm_min, local.tm_sec);
    return buffer;
}

const char* LevelName(LogLevel level) {
    switch (level) {
    case LogLevel::Debug: return "DBG";
    case LogLevel::Info: return "INF";
    case LogLevel::Warn: return "WRN";
    case LogLevel::Error: return "ERR";
    }
    return "INF";
}

} // namespace

const char* CommonVersion() { return "1.0.0"; }

void ConsoleInit() {
    // 与 ocid_manifest / ocid_wssm 一致：控制台设 UTF-8 代码页，程序直接写 UTF-8 字节。
    SetConsoleOutputCP(CP_UTF8);
    SetConsoleCP(CP_UTF8);
    std::setvbuf(stdout, nullptr, _IOFBF, 1 << 16);
}

std::string WideToUtf8(const std::wstring& text) {
    if (text.empty()) return {};
    const int need = WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()),
                                        nullptr, 0, nullptr, nullptr);
    if (need <= 0) return {};
    std::string out(static_cast<std::size_t>(need), '\0');
    WideCharToMultiByte(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), out.data(), need, nullptr, nullptr);
    return out;
}

std::wstring Utf8ToWide(const std::string& text) {
    if (text.empty()) return {};
    const int need = MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), nullptr, 0);
    if (need <= 0) return {};
    std::wstring out(static_cast<std::size_t>(need), L'\0');
    MultiByteToWideChar(CP_UTF8, 0, text.c_str(), static_cast<int>(text.size()), out.data(), need);
    return out;
}

void Out(const std::string& utf8) {
    if (!utf8.empty()) std::fwrite(utf8.data(), 1, utf8.size(), stdout);
}

void OutLn(const std::string& utf8) {
    Out(utf8);
    std::fputc('\n', stdout);
    std::fflush(stdout);
}

void Err(const std::string& utf8) {
    std::fflush(stdout);
    std::fwrite(utf8.data(), 1, utf8.size(), stderr);
    std::fputc('\n', stderr);
    std::fflush(stderr);
}

bool LogOpen(const std::wstring& path, std::string& error) {
    std::lock_guard<std::mutex> guard(g_logLock);
    if (g_log != nullptr) { std::fclose(g_log); g_log = nullptr; }
    // _wfopen 的 mode 串必须是 UTF-8 之外的窄串，这里用 "wb" 即可（内容自己写成 UTF-8）。
    FILE* handle = _wfopen(path.c_str(), L"wb");
    if (handle == nullptr) {
        error = "无法打开日志文件（错误码 " + std::to_string(GetLastError()) + "）";
        return false;
    }
    g_log = handle;
    return true;
}

void LogClose() {
    std::lock_guard<std::mutex> guard(g_logLock);
    if (g_log != nullptr) {
        std::fclose(g_log);
        g_log = nullptr;
    }
}

void LogWrite(LogLevel level, const std::string& utf8) {
    if (level == LogLevel::Debug) return; // 调试行默认不进日志，保持与 imageapp 日志同量级
    std::lock_guard<std::mutex> guard(g_logLock);
    if (g_log == nullptr) return;
    std::string line = Timestamp();
    line += LevelName(level);
    line += " ";
    line += utf8;
    line += "\r\n";
    std::fwrite(line.data(), 1, line.size(), g_log);
    std::fflush(g_log);
}

bool FileExists(const std::wstring& path) {
    const DWORD attr = GetFileAttributesW(path.c_str());
    return attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY) == 0;
}

bool DirExists(const std::wstring& path) {
    const DWORD attr = GetFileAttributesW(path.c_str());
    return attr != INVALID_FILE_ATTRIBUTES && (attr & FILE_ATTRIBUTE_DIRECTORY) != 0;
}

bool ReadFileBytes(const std::wstring& path, std::vector<std::uint8_t>& out, std::string& error) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr,
                              OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        error = "打开失败（错误码 " + std::to_string(GetLastError()) + "）";
        return false;
    }
    LARGE_INTEGER size{};
    if (!GetFileSizeEx(file, &size) || size.QuadPart < 0 || size.QuadPart > (1LL << 32)) {
        error = "取文件大小失败或文件过大";
        CloseHandle(file);
        return false;
    }
    out.resize(static_cast<std::size_t>(size.QuadPart));
    std::size_t done = 0;
    while (done < out.size()) {
        DWORD got = 0;
        const DWORD want = static_cast<DWORD>(std::min<std::size_t>(out.size() - done, 1u << 20));
        if (!ReadFile(file, out.data() + done, want, &got, nullptr)) {
            error = "读取失败（错误码 " + std::to_string(GetLastError()) + "）";
            CloseHandle(file);
            return false;
        }
        if (got == 0) break;
        done += got;
    }
    CloseHandle(file);
    out.resize(done);
    return true;
}

bool WriteFileBytes(const std::wstring& path, const void* data, std::size_t size, std::string& error) {
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS,
                              FILE_ATTRIBUTE_NORMAL, nullptr);
    if (file == INVALID_HANDLE_VALUE) {
        error = "创建失败（错误码 " + std::to_string(GetLastError()) + "）";
        return false;
    }
    const auto* bytes = static_cast<const std::uint8_t*>(data);
    std::size_t done = 0;
    while (done < size) {
        DWORD wrote = 0;
        const DWORD want = static_cast<DWORD>(std::min<std::size_t>(size - done, 1u << 20));
        if (!WriteFile(file, bytes + done, want, &wrote, nullptr)) {
            error = "写入失败（错误码 " + std::to_string(GetLastError()) + "）";
            CloseHandle(file);
            return false;
        }
        done += wrote;
    }
    CloseHandle(file);
    return true;
}

bool WriteFileText(const std::wstring& path, const std::string& utf8, std::string& error) {
    return WriteFileBytes(path, utf8.data(), utf8.size(), error);
}

std::wstring CombinePath(const std::wstring& left, const std::wstring& right) {
    if (left.empty()) return right;
    std::wstring result = left;
    if (result.back() != L'\\' && result.back() != L'/') result.push_back(L'\\');
    result += right;
    return result;
}

std::wstring ExeDirectory() {
    wchar_t buffer[MAX_PATH * 4];
    const DWORD got = GetModuleFileNameW(nullptr, buffer, static_cast<DWORD>(std::size(buffer)));
    if (got == 0) return {};
    std::wstring path(buffer, got);
    const std::size_t slash = path.find_last_of(L"\\/");
    return (slash == std::wstring::npos) ? std::wstring() : path.substr(0, slash);
}

std::wstring FullPath(const std::wstring& path) {
    if (path.empty()) return path;
    wchar_t buffer[MAX_PATH * 4];
    const DWORD got = GetFullPathNameW(path.c_str(), static_cast<DWORD>(std::size(buffer)), buffer, nullptr);
    if (got == 0) return path;
    return std::wstring(buffer, got);
}

bool EnsureDirectory(const std::wstring& path, std::string& error) {
    if (path.empty()) { error = "目录为空"; return false; }
    std::error_code code;
    std::filesystem::create_directories(std::filesystem::path(path), code);
    if (code) {
        error = "创建目录失败：" + code.message();
        return false;
    }
    return DirExists(path);
}

std::unique_ptr<XmlDoc> XmlParseText(const std::string& utf8, std::string& error) {
    std::string localError;
    Parser parser(utf8, localError);
    auto root = parser.ParseDocument();
    if (!root) {
        error = localError.empty() ? "XML 解析失败" : localError;
        return nullptr;
    }
    auto doc = std::make_unique<XmlDoc>();
    doc->root = root.get();
    // 池里持有一份指针，重挂父节点不必要——root 自己就是树，这里只为生命周期统一。
    doc->pool.push_back(std::move(root));
    return doc;
}

std::unique_ptr<XmlDoc> XmlParseFile(const std::wstring& path, std::string& error) {
    std::vector<std::uint8_t> bytes;
    if (!ReadFileBytes(path, bytes, error)) {
        error = "读取 XML 失败：" + error;
        return nullptr;
    }
    // 允许 UTF-8 BOM；其它编码（UTF-16）imageapp 的输入里不会出现，明确报错比静默乱码好。
    if (bytes.size() >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) {
        error = "UTF-16LE XML 暂不支持：" + WideToUtf8(path);
        return nullptr;
    }
    const std::string text(reinterpret_cast<const char*>(bytes.data()), bytes.size());
    auto doc = XmlParseText(text, error);
    if (!doc) error = WideToUtf8(path) + "：" + error;
    return doc;
}

const XmlNode* Child(const XmlNode* node, const char* local) {
    if (node == nullptr) return nullptr;
    for (const auto& kid : node->kids) {
        if (kid && kid->local == local) return kid.get();
    }
    return nullptr;
}

const XmlNode* Next(const XmlNode* node, const char* local) {
    if (node == nullptr || node->parent == nullptr) return nullptr;
    bool seen = false;
    for (const auto& kid : node->parent->kids) {
        if (kid.get() == node) { seen = true; continue; }
        if (seen && kid && kid->local == local) return kid.get();
    }
    return nullptr;
}

std::vector<const XmlNode*> Children(const XmlNode* node, const char* local) {
    std::vector<const XmlNode*> out;
    if (node == nullptr) return out;
    for (const auto& kid : node->kids) {
        if (kid && kid->local == local) out.push_back(kid.get());
    }
    return out;
}

std::vector<const XmlNode*> ChildrenAny(const XmlNode* node) {
    std::vector<const XmlNode*> out;
    if (node == nullptr) return out;
    for (const auto& kid : node->kids) {
        if (kid) out.push_back(kid.get());
    }
    return out;
}

bool HasAttr(const XmlNode* node, const char* local) {
    if (node == nullptr) return false;
    for (const auto& attr : node->attrs) {
        if (attr.local == local) return true;
    }
    return false;
}

std::string Attr(const XmlNode* node, const char* local) {
    if (node == nullptr) return {};
    for (const auto& attr : node->attrs) {
        if (attr.local == local) return attr.value;
    }
    return {};
}

std::string AttrOr(const XmlNode* node, const char* local, const char* fallback) {
    const std::string value = Attr(node, local);
    return value.empty() ? std::string(fallback) : value;
}

std::string Trim(const std::string& text) {
    std::size_t begin = 0;
    std::size_t end = text.size();
    while (begin < end) {
        const char c = text[begin];
        if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { ++begin; continue; }
        break;
    }
    while (end > begin) {
        const char c = text[end - 1];
        if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { --end; continue; }
        break;
    }
    return text.substr(begin, end - begin);
}

std::string Text(const XmlNode* node) {
    return node == nullptr ? std::string() : Trim(node->text);
}

std::string TextOf(const XmlNode* node, const char* local) {
    return Text(Child(node, local));
}

bool EqualsNoCase(const std::string& left, const std::string& right) {
    if (left.size() != right.size()) return false;
    for (std::size_t i = 0; i < left.size(); ++i) {
        char a = left[i];
        char b = right[i];
        if (a >= 'A' && a <= 'Z') a = static_cast<char>(a - 'A' + 'a');
        if (b >= 'A' && b <= 'Z') b = static_cast<char>(b - 'A' + 'a');
        if (a != b) return false;
    }
    return true;
}

void XmlEscapeTo(std::string& out, const std::string& text) {
    for (const char c : text) {
        switch (c) {
        case '&': out += "&amp;"; break;
        case '<': out += "&lt;"; break;
        case '>': out += "&gt;"; break;
        case '"': out += "&quot;"; break;
        case '\'': out += "&apos;"; break;
        default: out.push_back(c); break;
        }
    }
}

} // namespace ocid::imaging
