// OCID - $(runtime.*) 宏展开的实现。证据与出处见 MacroExpander.h 顶部注释。

#include "ocid/MacroExpander.h"

#include <algorithm>

namespace ocid
{
namespace
{
    std::wstring Lower( const std::wstring& s )
    {
        std::wstring r = s;
        for ( wchar_t& c : r )
            if ( c >= L'A' && c <= L'Z' )
                c = (wchar_t)( c - L'A' + L'a' );
        return r;
    }

    /// 去掉尾随的 `\` 或 `/`（`P:\Windows\` -> `P:\Windows`）。
    std::wstring TrimTrailingSeps( const std::wstring& s )
    {
        std::size_t n = s.size();
        while ( n > 0 && ( s[n - 1] == L'\\' || s[n - 1] == L'/' ) )
            --n;
        return s.substr( 0, n );
    }

    std::wstring Join( const std::wstring& base, const wchar_t* suffix )
    {
        if ( base.empty() )
            return std::wstring();
        if ( !suffix || !*suffix )
            return base;
        return base + L"\\" + suffix;
    }

    enum Base
    {
        B_WINDOWS = 0,
        B_BOOTDRIVE,
        B_PROGRAMFILES,
        B_PROGRAMFILES_X86,
        B_COMMONFILES,
        B_PROGRAMDATA,
        B_USERS,
        B_PUBLIC,
        B_STARTMENU,
        B_LITERAL,   ///< `suffix` 就是替换串本身（注册表根这类）
    };

    struct Entry
    {
        const wchar_t* name;    ///< 小写、不含 `$(` `)`
        Base           base;
        const wchar_t* suffix;  ///< B_LITERAL 时是完整值；否则是拼接在基路径后的相对段
    };

    // 19 个 `runtime.*` 路径宏 = 语料里 20 个不同项里去掉 `runtime.telnetClientsGroupSID`
    // （那是 SID，不是路径）；另 4 个 `runtime.host.*` 来自 winsockai.dll 的权威表。
    const Entry g_table[] =
    {
        { L"runtime.windows",           B_WINDOWS,           L"" },
        { L"runtime.windir",            B_WINDOWS,           L"" },
        { L"runtime.systemroot",        B_WINDOWS,           L"" },
        { L"runtime.system32",          B_WINDOWS,           L"system32" },
        { L"runtime.system",            B_WINDOWS,           L"System" },
        { L"runtime.drivers",           B_WINDOWS,           L"system32\\drivers" },
        { L"runtime.wbem",              B_WINDOWS,           L"system32\\wbem" },
        { L"runtime.inf",               B_WINDOWS,           L"inf" },
        { L"runtime.help",              B_WINDOWS,           L"help" },
        { L"runtime.fonts",             B_WINDOWS,           L"Fonts" },
        { L"runtime.apppatch",          B_WINDOWS,           L"AppPatch" },

        { L"runtime.bootdrive",         B_BOOTDRIVE,         L"" },
        { L"runtime.programfiles",      B_PROGRAMFILES,      L"" },
        { L"runtime.programfilesx86",   B_PROGRAMFILES_X86,  L"" },
        { L"runtime.commonfiles",       B_COMMONFILES,       L"" },
        { L"runtime.programdata",       B_PROGRAMDATA,       L"" },
        { L"runtime.public",            B_PUBLIC,            L"" },
        { L"runtime.documentssettings", B_USERS,             L"" },
        { L"runtime.startmenu",         B_STARTMENU,         L"" },

        { L"runtime.host.hklm.software",   B_LITERAL, L"HKEY_LOCAL_MACHINE\\SOFTWARE" },
        { L"runtime.host.hklm.system",     B_LITERAL, L"HKEY_LOCAL_MACHINE\\SYSTEM" },
        { L"runtime.host.hklm.system.ccs", B_LITERAL, L"HKEY_LOCAL_MACHINE\\SYSTEM\\CurrentControlSet" },
        { L"runtime.host.hkcr",            B_LITERAL, L"HKEY_CLASSES_ROOT" },
    };

    std::size_t TableSize() { return sizeof( g_table ) / sizeof( g_table[0] ); }

    /// 宏名（小写）→ 展开串。找不到返回 false。
    bool ResolveRuntimeMacro( const std::wstring& lowerName, const RuntimePaths& p,
                              std::wstring* out )
    {
        for ( std::size_t i = 0; i < TableSize(); ++i )
        {
            if ( lowerName != g_table[i].name )
                continue;

            const std::wstring base =
                g_table[i].base == B_WINDOWS          ? p.windows          :
                g_table[i].base == B_BOOTDRIVE        ? p.bootDrive        :
                g_table[i].base == B_PROGRAMFILES     ? p.programFiles     :
                g_table[i].base == B_PROGRAMFILES_X86 ? p.programFilesX86  :
                g_table[i].base == B_COMMONFILES      ? p.commonFiles      :
                g_table[i].base == B_PROGRAMDATA      ? p.programData      :
                g_table[i].base == B_USERS            ? p.users            :
                g_table[i].base == B_PUBLIC           ? p.publicDir        :
                g_table[i].base == B_STARTMENU        ? p.startMenu        :
                                                        std::wstring();

            std::wstring value = ( g_table[i].base == B_LITERAL )
                                     ? std::wstring( g_table[i].suffix )
                                     : Join( base, g_table[i].suffix );

            // 基路径缺失（例如镜像没告诉我们盘符）⇒ 视为"没认出来"，保留原文并上报，
            // 绝不替换成半截路径 —— 半截路径比原样保留更危险。
            if ( value.empty() )
                return false;

            *out = value;
            return true;
        }
        return false;
    }

    bool IsNameChar( wchar_t c )
    {
        return ( c >= L'A' && c <= L'Z' ) || ( c >= L'a' && c <= L'z' ) ||
               ( c >= L'0' && c <= L'9' ) || c == L'_' || c == L'.';
    }
}

RuntimePaths RuntimePaths::FromWindowsDir( const std::wstring& windowsDir )
{
    RuntimePaths p;
    p.windows = TrimTrailingSeps( windowsDir );

    if ( p.windows.size() >= 2 && p.windows[1] == L':' )
        p.bootDrive = p.windows.substr( 0, 2 );

    if ( !p.bootDrive.empty() )
    {
        const std::wstring drive = p.bootDrive + L"\\";
        p.programFiles    = drive + L"Program Files";
        p.programFilesX86 = drive + L"Program Files (x86)";
        p.commonFiles     = p.programFiles + L"\\Common Files";
        p.programData     = drive + L"ProgramData";
        p.users           = drive + L"Users";
        p.publicDir       = p.users + L"\\Public";
        p.startMenu       = p.programData + L"\\Microsoft\\Windows\\Start Menu";
    }
    return p;
}

void RuntimePaths::ApplyImageOverrides( const std::wstring& pf, const std::wstring& pfX86,
                                        const std::wstring& cf, const std::wstring& pd,
                                        const std::wstring& usersDir )
{
    if ( !pf.empty() )       programFiles    = TrimTrailingSeps( pf );
    if ( !pfX86.empty() )    programFilesX86 = TrimTrailingSeps( pfX86 );
    if ( !cf.empty() )       commonFiles     = TrimTrailingSeps( cf );
    if ( !pd.empty() )       programData     = TrimTrailingSeps( pd );
    if ( !usersDir.empty() ) users           = TrimTrailingSeps( usersDir );

    // 这两个是派生值，基路径一变就得跟着重算（否则会留下旧盘符的路径）。
    if ( !users.empty() )
        publicDir = Join( users, L"Public" );
    if ( !programData.empty() )
        startMenu = Join( programData, L"Microsoft\\Windows\\Start Menu" );
}

bool ContainsMacro( const std::wstring& text )
{
    return text.find( L"$(" ) != std::wstring::npos;
}

std::vector<std::wstring> ExtractMacroNames( const std::wstring& text )
{
    std::vector<std::wstring> names;
    for ( std::size_t i = 0; i + 1 < text.size(); ++i )
    {
        if ( text[i] != L'$' || text[i + 1] != L'(' )
            continue;
        const std::size_t close = text.find( L')', i + 2 );
        if ( close == std::wstring::npos )
            break;
        if ( close == i + 2 ) { i = close; continue; }

        const std::wstring name = text.substr( i, close + 1 - i );
        if ( std::find( names.begin(), names.end(), name ) == names.end() )
            names.push_back( name );
        i = close;
    }
    return names;
}

MacroExpansion ExpandMacros( const std::wstring& text, const RuntimePaths& paths )
{
    MacroExpansion r;
    r.text.reserve( text.size() );

    std::size_t i = 0;
    while ( i < text.size() )
    {
        if ( text[i] == L'$' && i + 1 < text.size() && text[i + 1] == L'(' )
        {
            const std::size_t close = text.find( L')', i + 2 );
            if ( close != std::wstring::npos && close > i + 2 )
            {
                const std::wstring name = text.substr( i + 2, close - i - 2 );

                // 名字里出现非法字符 ⇒ 不是宏，当普通文本（避免把 `$(` 到很远处的 `)`
                // 之间的一大段都当成名字）。
                bool nameLooksValid = true;
                for ( wchar_t c : name )
                    if ( !IsNameChar( c ) ) { nameLooksValid = false; break; }

                std::wstring value;
                if ( nameLooksValid && ResolveRuntimeMacro( Lower( name ), paths, &value ) )
                {
                    r.text += value;
                    i = close + 1;
                    continue;
                }

                const std::wstring literal = text.substr( i, close + 1 - i );
                if ( std::find( r.unresolved.begin(), r.unresolved.end(), literal ) ==
                     r.unresolved.end() )
                    r.unresolved.push_back( literal );
                r.text += literal;
                i = close + 1;
                continue;
            }
        }
        r.text += text[i];
        ++i;
    }
    return r;
}

std::wstring ExpandRuntimeMacros( const std::wstring& text, const RuntimePaths& paths )
{
    return ExpandMacros( text, paths ).text;
}

const wchar_t* const* KnownRuntimeMacroNames( std::size_t* count )
{
    static std::vector<const wchar_t*> names;
    if ( names.empty() )
        for ( std::size_t i = 0; i < TableSize(); ++i )
            names.push_back( g_table[i].name );

    if ( count ) *count = names.size();
    return names.empty() ? nullptr : names.data();
}
}
