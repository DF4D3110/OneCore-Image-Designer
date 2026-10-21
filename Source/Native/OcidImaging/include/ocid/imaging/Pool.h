// OcidImaging / Pool 池层（薄封装 ImageStorageService.dll 的建池/建 space）
//
// 为什么单独一层：把“存储空间”这套私有 IOCTL / 服务句柄的调用关在一个 DLL 里，
// 出问题时能立刻分辨是“池没建起来”还是“文件没写进去”。ImageStorageService.dll
// 由 ADK 的 Imaging 工具目录提供（imagestorageservice.dll，server2022 那次量到 403,456 字节）。
//
// 上层（imagegen pool ...）只认这里导出的纯数据接口，不直接碰 Win32 句柄。
#pragma once

#include <string>
#include <vector>

namespace ocid::imaging {

#if defined(OCID_POOL_BUILD)
#define OCID_POOL_API __declspec(dllexport)
#else
#define OCID_POOL_API __declspec(dllimport)
#endif

// 探测 ImageStorageService.dll：返回实际加载到的路径，并列出期望导出的函数里
// 哪些找到了（found）哪些缺失（missing）。只做 LoadLibrary + GetProcAddress，
// 不创建服务句柄、不碰磁盘。
OCID_POOL_API bool PoolProbe(std::string& dllPath,
                             std::vector<std::string>& found,
                             std::vector<std::string>& missing,
                             std::string& error);

// 列出 DLL 导出的全部函数名（按名称排序）。同样只做 LoadLibrary，不碰磁盘。
OCID_POOL_API bool PoolExports(std::string& dllPath,
                              std::vector<std::string>& names,
                              std::string& error);

} // namespace ocid::imaging
