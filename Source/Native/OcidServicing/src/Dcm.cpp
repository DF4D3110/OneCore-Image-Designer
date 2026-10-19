#include "ocid/Dcm.h"

#include <windows.h>

#include <cstdio>
#include <cstring>

#include <msdelta.h>

#include "ocid/Delta.h"

#pragma comment(lib, "msdelta.lib")

namespace ocid {
namespace {

constexpr std::uint16_t kDictionaryResourceType = 614;  // 0x266
constexpr std::uint16_t kDictionaryResourceId = 1;

bool ReadWholeFile(const std::wstring& path, std::vector<std::uint8_t>& bytes, std::string& error) {
  bytes.clear();
  FILE* f = ::_wfopen(path.c_str(), L"rb");
  if (f == nullptr) {
    error = "cannot open dictionary file";
    return false;
  }
  std::fseek(f, 0, SEEK_END);
  const long size = std::ftell(f);
  std::fseek(f, 0, SEEK_SET);
  if (size <= 0) {
    std::fclose(f);
    error = "dictionary file is empty";
    return false;
  }
  bytes.resize(static_cast<std::size_t>(size));
  const std::size_t read = std::fread(bytes.data(), 1, bytes.size(), f);
  std::fclose(f);
  if (read != bytes.size()) {
    bytes.clear();
    error = "cannot read dictionary file";
    return false;
  }
  return true;
}

}  // namespace

bool DcmIsCompressed(const std::vector<std::uint8_t>& data) {
  static const char kMagic[4] = {'D', 'C', 'M', 0x01};
  return data.size() >= 8 && std::memcmp(data.data(), kMagic, sizeof(kMagic)) == 0;
}

bool LoadManifestDictionary(const std::wstring& wcpPath,
                            std::vector<std::uint8_t>& dictionary,
                            std::string& error) {
  dictionary.clear();
  HMODULE module = ::LoadLibraryExW(wcpPath.c_str(), nullptr,
                                    LOAD_LIBRARY_AS_DATAFILE | LOAD_LIBRARY_AS_IMAGE_RESOURCE);
  if (module == nullptr) {
    error = "cannot load wcp.dll";
    return false;
  }
  HRSRC resource = ::FindResourceW(module, MAKEINTRESOURCEW(kDictionaryResourceId),
                                   MAKEINTRESOURCEW(kDictionaryResourceType));
  if (resource == nullptr) {
    // 少数版本的资源类型号会不同，退一步按名字找（"MANIFESTDICTIONARY"）。
    resource = ::FindResourceW(module, MAKEINTRESOURCEW(kDictionaryResourceId), L"MANIFESTDICTIONARY");
  }
  if (resource == nullptr) {
    ::FreeLibrary(module);
    error = "manifest dictionary resource not found in wcp.dll";
    return false;
  }
  const DWORD size = ::SizeofResource(module, resource);
  HGLOBAL handle = ::LoadResource(module, resource);
  const void* data = handle != nullptr ? ::LockResource(handle) : nullptr;
  if (data == nullptr || size == 0) {
    ::FreeLibrary(module);
    error = "cannot lock manifest dictionary resource";
    return false;
  }
  const auto* bytes = static_cast<const std::uint8_t*>(data);
  dictionary.assign(bytes, bytes + size);
  ::FreeLibrary(module);
  return true;
}

bool LoadManifestDictionaryFile(const std::wstring& path,
                                std::vector<std::uint8_t>& dictionary,
                                std::string& error) {
  if (!ReadWholeFile(path, dictionary, error)) {
    return false;
  }
  if (dictionary.size() < 16) {
    dictionary.clear();
    error = "dictionary file is too small";
    return false;
  }
  return true;
}

bool DumpManifestDictionary(const std::wstring& wcpPath,
                            const std::wstring& outPath,
                            std::string& error) {
  std::vector<std::uint8_t> dictionary;
  if (!LoadManifestDictionary(wcpPath, dictionary, error)) {
    return false;
  }
  FILE* out = ::_wfopen(outPath.c_str(), L"wb");
  if (out == nullptr) {
    error = "cannot open output file";
    return false;
  }
  const std::size_t written = std::fwrite(dictionary.data(), 1, dictionary.size(), out);
  std::fclose(out);
  if (written != dictionary.size()) {
    error = "cannot write output file";
    return false;
  }
  return true;
}

bool DcmDecodeManifest(const std::vector<std::uint8_t>& compressed,
                       const std::vector<std::uint8_t>& dictionary,
                       std::vector<std::uint8_t>& output,
                       std::string& error) {  output.clear();
  if (!DcmIsCompressed(compressed)) {
    error = "not a DCM manifest";
    return false;
  }
  if (dictionary.empty()) {
    error = "manifest dictionary is empty";
    return false;
  }

  const std::uint8_t* deltaStart = compressed.data() + 4;
  const std::size_t deltaSize = compressed.size() - 4;
  if (deltaSize < 4 || std::memcmp(deltaStart, "PA30", 4) != 0) {
    error = "DCM payload is not a PA30 delta";
    return false;
  }

  std::vector<std::uint8_t> delta(deltaStart, deltaStart + deltaSize);
  std::string deltaError;
  if (ApplyPa30Delta(dictionary, delta, output, deltaError) && !output.empty()) {
    return true;
  }

  // 过渡回退：容器包里的 PE 载荷（file type != RAW）与少数多格式补丁还没进自研解码器，
  // 这里仍走 msdelta，保证镜像装配结果不变。自研解码器覆盖后必须删掉这段。
  DELTA_INPUT source{};
  source.lpStart = const_cast<void*>(reinterpret_cast<const void*>(dictionary.data()));
  source.uSize = static_cast<ULONG_PTR>(dictionary.size());
  source.Editable = FALSE;

  DELTA_INPUT deltaInput{};
  deltaInput.lpStart = const_cast<void*>(reinterpret_cast<const void*>(delta.data()));
  deltaInput.uSize = static_cast<ULONG_PTR>(delta.size());
  deltaInput.Editable = FALSE;

  DELTA_OUTPUT result{};

  if (!::ApplyDeltaB(DELTA_FLAG_NONE, source, deltaInput, &result)) {
    error = "PA30 delta failed (own decoder: " + deltaError + "; msdelta fallback also failed)";
    return false;
  }
  if (result.lpStart == nullptr || result.uSize == 0) {
    if (result.lpStart != nullptr) {
      ::DeltaFree(result.lpStart);
    }
    error = "DCM manifest decompressed to nothing";
    return false;
  }
  const auto* out = static_cast<const std::uint8_t*>(result.lpStart);
  output.assign(out, out + result.uSize);
  ::DeltaFree(result.lpStart);
  return true;
}

}  // namespace ocid

