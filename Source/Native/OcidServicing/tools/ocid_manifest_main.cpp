// ocid_manifest.exe 的入口：只透传给共享的命令实现。
//
// 之所以拆成两个翻译单元：ocid_wssm.exe（Windows Servicing Stack Manager）
// 需要复用同一套命令实现，但两条 exe 各自要有自己的 main。
#include "OcidManifestRun.h"

int main(int argc, char** argv) { return OcidManifestRun(argc, argv); }
